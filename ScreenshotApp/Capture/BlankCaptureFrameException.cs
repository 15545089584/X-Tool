namespace ScreenshotApp.Capture;

internal sealed class BlankCaptureFrameException : Exception
{
    internal BlankCaptureFrameException()
        : base("DXGI 返回了空白帧。")
    {
    }
}
