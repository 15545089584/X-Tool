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
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private readonly List<float> _samples = new();
    private WaveInEvent? _waveIn;
    private OfflineRecognizer? _recognizer;
    private DateTime _recordingStartedAt;
    private int _autoStopRequested;
    private double _peakRms;
    private string? _recordingError;
    private CancellationTokenSource? _partialRecognitionCancellation;
    private Task? _partialRecognitionTask;

    private readonly bool _preferGpu;
    private string _activeBackendDescription = "CPU";

    internal event EventHandler? AutoStopRequested;
    internal event Action<double>? SoundLevelChanged;
    internal event Action<string>? RecordingFaulted;
    internal event Action<string>? PartialResultAvailable;

    internal VoiceInputService(bool preferGpu)
    {
        _preferGpu = preferGpu;
    }

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

    /// <summary>CUDA 原生运行时由 Release 构建复制到 win-x64 目录；缺失时保持 CPU 路径。</summary>
    internal bool IsCudaRuntimeAvailable =>
        Environment.Is64BitProcess &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "xtool-voice-cuda-runtime.txt")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "onnxruntime_providers_cuda.dll"));

    internal string PreferredBackendDescription => _preferGpu && IsCudaRuntimeAvailable
        ? "CUDA（NVIDIA GPU）"
        : "CPU";

    internal string ActiveBackendDescription => _activeBackendDescription;

    internal void Start()
    {
        if (IsRecording)
        {
            return;
        }

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
        // CUDA 会在独立推理流上使用 cuDNN 上下文。录音期间的高频预识别会造成
        // 连续 GPU 解码；正式确认时只保留一次完整识别，避免多轮预览与最终识别交错。
        if (!(_preferGpu && IsCudaRuntimeAvailable))
        {
            _partialRecognitionCancellation = new CancellationTokenSource();
            _partialRecognitionTask = RunPartialRecognitionAsync(_partialRecognitionCancellation.Token);
        }
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
        Cancel();
        _recognizer?.Dispose();
        _recognizer = null;
        _recognitionGate.Dispose();
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
        await _recognitionGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Recognize(samples, cancellationToken), cancellationToken);
        }
        finally
        {
            _recognitionGate.Release();
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
        // CUDA 运行时不存在时不请求 GPU，保证普通设备仍可稳定使用本地语音输入。
        var useCuda = _preferGpu && IsCudaRuntimeAvailable;
        config.ModelConfig.Provider = useCuda ? "cuda" : "cpu";
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.ModelConfig.Debug = 0;
        try
        {
            _recognizer = new OfflineRecognizer(config);
            _activeBackendDescription = useCuda ? "CUDA（NVIDIA GPU）" : "CPU";
        }
        catch when (useCuda)
        {
            // 驱动或 CUDA 初始化异常时，当前会话自动降级，不阻断离线语音输入。
            config.ModelConfig.Provider = "cpu";
            _recognizer = new OfflineRecognizer(config);
            _activeBackendDescription = "CPU（CUDA 初始化失败后回退）";
        }

        return _recognizer;
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
