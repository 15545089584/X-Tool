using System.Windows;

namespace ScreenshotApp.Recording;

public sealed record ScreenRecordingOptions(
    Int32Rect ScreenRegion,
    bool RecordSystemAudio,
    bool RecordMicrophone,
    int FramesPerSecond = 15);

public sealed record ScreenRecordingResult(
    string FilePath,
    string? CoverImagePath,
    TimeSpan Duration,
    int FrameCount,
    bool IncludesSystemAudio,
    bool IncludesMicrophone);
