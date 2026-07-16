namespace ScreenshotApp.Capture;

public interface ICaptureBackend
{
    string Name { get; }

    Task<CaptureFrame> CaptureCurrentMonitorAsync(CancellationToken cancellationToken = default);
}
