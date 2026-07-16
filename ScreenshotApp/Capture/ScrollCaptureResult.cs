using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// 一次滚动长截图的结果与停止原因。
/// </summary>
public sealed record ScrollCaptureResult(
    BitmapSource Bitmap,
    int FrameCount,
    string StopReason);
