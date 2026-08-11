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
