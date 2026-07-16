using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// 截图后端产出的统一帧，后续长截图、OCR 和录屏都基于这个模型扩展。
/// </summary>
public sealed record CaptureFrame(
    BitmapSource Bitmap,
    Int32Rect ScreenBounds,
    DateTimeOffset Timestamp,
    string BackendName);
