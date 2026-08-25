using System.Windows;

namespace ScreenshotApp.Recording;

public enum ScreenRecordingMode
{
    Mp4,
    Gif
}

public sealed record ScreenRecordingOptions(
    Int32Rect ScreenRegion,
    bool RecordSystemAudio,
    bool RecordMicrophone,
    int FramesPerSecond = 15,
    ScreenRecordingMode Mode = ScreenRecordingMode.Mp4,
    int GifMaxWidth = 960,
    int GifMaxDurationSeconds = 10);

public sealed record ScreenRecordingResult(
    string FilePath,
    string? CoverImagePath,
    TimeSpan Duration,
    int FrameCount,
    bool IncludesSystemAudio,
    bool IncludesMicrophone,
    ScreenRecordingMode Mode);

/// <summary>GIF 采集完成后交给后台编码器的原始帧任务。</summary>
public sealed record GifRecordingCapture(
    string RawFramePath,
    Task RawFrameReady,
    string FilePath,
    string? CoverImagePath,
    int Width,
    int Height,
    int FrameRate,
    int MaxWidth,
    TimeSpan Duration,
    int FrameCount);
