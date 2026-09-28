using NAudio.Wave;
using SherpaOnnx;
using System.IO;

namespace ScreenshotApp.VoiceInput;

/// <summary>负责本地录音、停顿检测与 SenseVoice 离线识别的服务。</summary>
internal sealed class VoiceInputService : IDisposable
{
    private const int SampleRate = 16_000;
    // 长段口述由用户再次按右 Alt 完成；时长上限仅用于避免忘记结束时持续录音。
    private const int MaximumRecordingSeconds = 90;
    // 识别模型较大，保留短时间可避免连续语音输入反复冷启动。
    private static readonly TimeSpan RecognizerIdleTimeout = TimeSpan.FromMinutes(10);
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private readonly System.Threading.Timer _recognizerIdleTimer;
    private readonly List<float> _samples = new();
    private WaveInEvent? _waveIn;
    private OfflineRecognizer? _recognizer;
    private DateTime _recordingStartedAt;
    private int _autoStopRequested;
    private double _peakRms;
    private string? _recordingError;
    private CancellationTokenSource? _partialRecognitionCancellation;
    private Task? _partialRecognitionTask;
    private long _recognizerReleaseDeadlineUtcTicks;
    private int _disposed;

    internal VoiceInputService()
    {
        _recognizerIdleTimer = new System.Threading.Timer(
            ReleaseRecognizerWhenIdle,
            null,
            System.Threading.Timeout.InfiniteTimeSpan,
            System.Threading.Timeout.InfiniteTimeSpan);
    }

    internal event EventHandler? AutoStopRequested;
    internal event Action<double>? SoundLevelChanged;
    internal event Action<string>? RecordingFaulted;
    internal event Action<string>? PartialResultAvailable;

    internal bool IsRecording => _waveIn is not null;

    internal string CaptureDiagnostics
    {
        get
        {
            lock (_syncRoot)
            {
                var seconds = (double)_samples.Count / SampleRate;
                return $"已采集 {seconds:F1} 秒，峰值音量 {_peakRms:F3}";
            }
        }
    }

    internal string ModelDirectory => Path.Combine(
        AppContext.BaseDirectory,
        "models",
        "voice-input",
        "default");

    internal bool IsModelAvailable =>
        File.Exists(Path.Combine(ModelDirectory, "model.int8.onnx")) &&
        File.Exists(Path.Combine(ModelDirectory, "tokens.txt"));

    internal void Start()
    {
        ThrowIfDisposed();
        if (IsRecording)
        {
            return;
        }

        // 已开始一次新的语音输入时，不在录音期间回收上一轮留下的模型。
        CancelRecognizerIdleRelease();
        if (!IsModelAvailable)
        {
            throw new FileNotFoundException("未找到本地语音识别模型，请修复或重新安装 X-Tool。");
        }

        lock (_syncRoot)
        {
            _samples.Clear();
            _peakRms = 0;
        }

        _autoStopRequested = 0;
        _recordingError = null;
        _recordingStartedAt = DateTime.UtcNow;
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 80
        };
        _waveIn.DataAvailable += WaveIn_DataAvailable;
        _waveIn.RecordingStopped += WaveIn_RecordingStopped;
        _waveIn.StartRecording();
        _partialRecognitionCancellation = new CancellationTokenSource();
        _partialRecognitionTask = RunPartialRecognitionAsync(_partialRecognitionCancellation.Token);
    }

    internal async Task<string> StopAndRecognizeAsync(CancellationToken cancellationToken)
    {
        var waveIn = _waveIn;
        if (waveIn is null)
        {
            return string.Empty;
        }

        _waveIn = null;
        await StopPartialRecognitionAsync();
        waveIn.DataAvailable -= WaveIn_DataAvailable;
        waveIn.RecordingStopped -= WaveIn_RecordingStopped;
        waveIn.StopRecording();
        waveIn.Dispose();

        if (!string.IsNullOrWhiteSpace(_recordingError))
        {
            throw new InvalidOperationException(_recordingError);
        }

        float[] samples;
        lock (_syncRoot)
        {
            samples = _samples.ToArray();
            _samples.Clear();
        }

        // 即使静音阈值未触发，也尝试识别足够长的录音，避免低灵敏度麦克风被提前丢弃。
        if (samples.Length < SampleRate / 3)
        {
            return string.Empty;
        }

        return await RecognizeAsync(samples, cancellationToken);
    }

    internal void Cancel()
    {
        _partialRecognitionCancellation?.Cancel();
        _partialRecognitionCancellation = null;
        _partialRecognitionTask = null;
        var waveIn = _waveIn;
        _waveIn = null;
        if (waveIn is not null)
        {
            waveIn.DataAvailable -= WaveIn_DataAvailable;
            waveIn.RecordingStopped -= WaveIn_RecordingStopped;
            waveIn.StopRecording();
            waveIn.Dispose();
        }

        lock (_syncRoot)
        {
            _samples.Clear();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _recognizerIdleTimer.Dispose();
        Cancel();
        OfflineRecognizer? recognizer = null;
        try
        {
            _recognitionGate.Wait();
            try
            {
                recognizer = _recognizer;
                _recognizer = null;
                Interlocked.Exchange(ref _recognizerReleaseDeadlineUtcTicks, 0);
            }
            finally
            {
                _recognitionGate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // 应用退出时并发取消识别任务，不需要继续处理。
        }

        recognizer?.Dispose();
    }

    private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        var buffer = e.Buffer;
        var sampleCount = e.BytesRecorded / sizeof(short);
        if (sampleCount == 0)
        {
            return;
        }

        var frame = new float[sampleCount];
        double totalEnergy = 0;
        for (var index = 0; index < sampleCount; index++)
        {
            var value = BitConverter.ToInt16(buffer, index * sizeof(short)) / 32768f;
            frame[index] = value;
            totalEnergy += value * value;
        }

        lock (_syncRoot)
        {
            _samples.AddRange(frame);
        }

        var now = DateTime.UtcNow;
        var rms = Math.Sqrt(totalEnergy / sampleCount);
        lock (_syncRoot)
        {
            _peakRms = Math.Max(_peakRms, rms);
        }

        // 放大常见笔记本麦克风的低电平，并用非线性映射使正常说话有明显律动。
        var normalizedLevel = Math.Clamp((rms - 0.0005) / 0.028, 0, 1);
        var displayLevel = Math.Pow(normalizedLevel, 0.42);
        SoundLevelChanged?.Invoke(displayLevel);

        var isTooLong = now - _recordingStartedAt >= TimeSpan.FromSeconds(MaximumRecordingSeconds);
        if (isTooLong && Interlocked.Exchange(ref _autoStopRequested, 1) == 0)
        {
            AutoStopRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
        {
            return;
        }

        _recordingError = $"麦克风采集失败：{e.Exception.Message}";
        RecordingFaulted?.Invoke(_recordingError);
    }

    private async Task RunPartialRecognitionAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1_200), cancellationToken);
                float[] samples;
                lock (_syncRoot)
                {
                    samples = _samples.ToArray();
                }

                if (samples.Length < SampleRate)
                {
                    continue;
                }

                var text = await RecognizeAsync(samples, cancellationToken);
                if (!string.IsNullOrWhiteSpace(text) && !cancellationToken.IsCancellationRequested)
                {
                    PartialResultAvailable?.Invoke(text);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止录音时正常取消后台分段识别。
        }
        catch (ObjectDisposedException)
        {
            // 应用退出时识别服务已释放。
        }
    }

    private async Task StopPartialRecognitionAsync()
    {
        var cancellation = _partialRecognitionCancellation;
        var task = _partialRecognitionTask;
        _partialRecognitionCancellation = null;
        _partialRecognitionTask = null;
        cancellation?.Cancel();
        if (task is not null)
        {
            await task;
        }

        cancellation?.Dispose();
    }

    private async Task<string> RecognizeAsync(float[] samples, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _recognitionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            CancelRecognizerIdleRelease();
            return await Task.Run(() => Recognize(samples, cancellationToken), cancellationToken);
        }
        finally
        {
            _recognitionGate.Release();
            ScheduleRecognizerIdleRelease();
        }
    }

    private string Recognize(float[] samples, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recognizer = GetRecognizer();
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(SampleRate, samples);
        recognizer.Decode(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return NormalizeResult(stream.Result.Text);
    }

    private OfflineRecognizer GetRecognizer()
    {
        ThrowIfDisposed();
        if (_recognizer is not null)
        {
            return _recognizer;
        }

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Tokens = Path.Combine(ModelDirectory, "tokens.txt");
        config.ModelConfig.SenseVoice.Model = Path.Combine(ModelDirectory, "model.int8.onnx");
        config.ModelConfig.SenseVoice.Language = "auto";
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.ModelConfig.Debug = 0;
        _recognizer = new OfflineRecognizer(config);
        return _recognizer;
    }

    /// <summary>取消已排队的空闲释放，避免识别进行中被计时器回收。</summary>
    private void CancelRecognizerIdleRelease()
    {
        Interlocked.Exchange(ref _recognizerReleaseDeadlineUtcTicks, 0);
        try
        {
            _recognizerIdleTimer.Change(
                System.Threading.Timeout.InfiniteTimeSpan,
                System.Threading.Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 应用正在退出。
        }
    }

    /// <summary>识别完成后延迟释放模型，兼顾连续输入速度与空闲内存占用。</summary>
    private void ScheduleRecognizerIdleRelease()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var deadline = DateTime.UtcNow.Add(RecognizerIdleTimeout).Ticks;
        Interlocked.Exchange(ref _recognizerReleaseDeadlineUtcTicks, deadline);
        try
        {
            _recognizerIdleTimer.Change(
                RecognizerIdleTimeout,
                System.Threading.Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 应用正在退出。
        }
    }

    /// <summary>计时到期后在识别锁内摘除模型，避免与正在运行的识别共享同一实例。</summary>
    private void ReleaseRecognizerWhenIdle(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        OfflineRecognizer? recognizer = null;
        TimeSpan? retryDelay = null;
        try
        {
            _recognitionGate.Wait();
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                var deadline = Interlocked.Read(ref _recognizerReleaseDeadlineUtcTicks);
                if (deadline <= 0)
                {
                    return;
                }

                var remainingTicks = deadline - DateTime.UtcNow.Ticks;
                if (remainingTicks > 0)
                {
                    retryDelay = TimeSpan.FromTicks(remainingTicks);
                }
                else
                {
                    recognizer = _recognizer;
                    _recognizer = null;
                    Interlocked.Exchange(ref _recognizerReleaseDeadlineUtcTicks, 0);
                }
            }
            finally
            {
                _recognitionGate.Release();
            }

            if (retryDelay is TimeSpan delay)
            {
                _recognizerIdleTimer.Change(delay, System.Threading.Timeout.InfiniteTimeSpan);
                return;
            }

            recognizer?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 应用退出时计时器可能与释放流程重叠。
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(VoiceInputService));
        }
    }

    private static string NormalizeResult(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // SenseVoice 可能附带情绪、语言等控制标记；语言输入仅保留可粘贴文本。
        var normalized = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        return System.Text.RegularExpressions.Regex.Replace(normalized, "\\s+", " ").Trim();
    }
}
