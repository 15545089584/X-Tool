namespace ScreenshotApp.Capture;

/// <summary>
/// 区域选择器的后续用途。普通截图返回裁剪位图，长截图返回屏幕坐标区域。
/// </summary>
public enum SelectionPurpose
{
    Screenshot,
    ScrollCaptureRegion
}
