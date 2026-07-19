using NAudio.Wave;
using SherpaOnnx;
using System.IO;

namespace ScreenshotApp.VoiceInput;

/// <summary>负责本地录音、停顿检测与 SenseVoice 离线识别的服务。</summary>
internal sealed class VoiceInputService : IDisposable
{
    private const int SampleRate = 16_000;
    private const int MaximumRecordingSeconds = 28;
    // 较低的阈值可兼容笔记本内置麦克风；识别阶段仍由模型决定是否返回文字。
    private const double VoiceThreshold = 0.004;
    private readonly object _syncRoot = new();
    private readonly List<float> _samples = new();
    private WaveInEvent? _waveIn;
    private OfflineRecognizer? _recognizer;
    private DateTime _recordingStartedAt;
    private DateTime _lastVoiceAt;
    private bool _hasDetectedVoice;
    private int _autoStopRequested;
    private double _peakRms;
    private string? _recordingError;

    internal event EventHandler? AutoStopRequested;
    internal event Action<double>? SoundLevelChanged;
    internal event Action<string>? RecordingFaulted;

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

        _hasDetectedVoice = false;
        _autoStopRequested = 0;
        _recordingError = null;
        _recordingStartedAt = DateTime.UtcNow;
        _lastVoiceAt = _recordingStartedAt;
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 80
        };
        _waveIn.DataAvailable += WaveIn_DataAvailable;
        _waveIn.RecordingStopped += WaveIn_RecordingStopped;
        _waveIn.StartRecording();
    }

    internal async Task<string> StopAndRecognizeAsync(CancellationToken cancellationToken)
    {
        var waveIn = _waveIn;
        if (waveIn is null)
        {
            return string.Empty;
        }

        _waveIn = null;
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

        return await Task.Run(() => Recognize(samples, cancellationToken), cancellationToken);
    }

    internal void Cancel()
    {
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

        // 以对数感知的方式压缩音量，安静时保持细小波形，说话时才明显抬升。
        var displayLevel = Math.Clamp((rms - 0.0015) / 0.055, 0, 1);
        SoundLevelChanged?.Invoke(displayLevel);
        if (rms >= VoiceThreshold)
        {
            _hasDetectedVoice = true;
            _lastVoiceAt = now;
            return;
        }

        var isLongEnough = now - _recordingStartedAt >= TimeSpan.FromMilliseconds(650);
        var isSilentAfterSpeech = _hasDetectedVoice && now - _lastVoiceAt >= TimeSpan.FromMilliseconds(850);
        var isTooLong = now - _recordingStartedAt >= TimeSpan.FromSeconds(MaximumRecordingSeconds);
        if (isLongEnough && (isSilentAfterSpeech || isTooLong) && Interlocked.Exchange(ref _autoStopRequested, 1) == 0)
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
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.ModelConfig.Debug = 0;
        _recognizer = new OfflineRecognizer(config);
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
