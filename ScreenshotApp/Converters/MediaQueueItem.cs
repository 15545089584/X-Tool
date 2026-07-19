using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ScreenshotApp.Converters;

/// <summary>音视频转换队列项，统一承载探测信息、状态和进度。</summary>
internal sealed class MediaQueueItem : INotifyPropertyChanged
{
    private string _status = "等待探测";
    private double _progress;
    private string _details = "正在读取媒体信息…";

    public MediaQueueItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Format = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        FileSize = FormatFileSize(new FileInfo(filePath).Length);
    }

    public string FilePath { get; }
    public string FileName { get; }
    public string Format { get; }
    public string FileSize { get; }
    public string DurationDisplay { get; private set; } = "--:--";
    public string CodecDisplay { get; private set; } = "--";
    public MediaProbeInfo? Probe { get; private set; }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public string Details
    {
        get => _details;
        set => SetField(ref _details, value);
    }

    public void ApplyProbe(MediaProbeInfo probe, bool isVideo)
    {
        Probe = probe;
        Status = "等待转换";
        DurationDisplay = FormatDuration(probe.Duration);
        CodecDisplay = string.IsNullOrWhiteSpace(probe.Codec) ? "未知" : probe.Codec;
        OnPropertyChanged(nameof(DurationDisplay));
        OnPropertyChanged(nameof(CodecDisplay));
        Details = isVideo
            ? $"{probe.Width} × {probe.Height} · {probe.FramesPerSecond:0.##} FPS · {probe.Codec} · {FormatDuration(probe.Duration)}"
            : $"{FormatDuration(probe.Duration)} · {probe.Codec} · {probe.SampleRate} Hz · {FormatChannels(probe.Channels)}";
    }

    public void MarkFailed(string reason)
    {
        Status = "失败";
        Details = reason;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private static string FormatChannels(int channels) => channels switch
    {
        1 => "单声道",
        2 => "双声道",
        > 2 => $"{channels} 声道",
        _ => "声道未知"
    };

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"hh\:mm\:ss") : duration.ToString(@"mm\:ss");

    internal static string FormatFileSize(long length) => length switch
    {
        < 1024 => $"{length} B",
        < 1024 * 1024 => $"{length / 1024d:0.0} KB",
        < 1024L * 1024L * 1024L => $"{length / 1024d / 1024d:0.00} MB",
        _ => $"{length / 1024d / 1024d / 1024d:0.00} GB"
    };
}
