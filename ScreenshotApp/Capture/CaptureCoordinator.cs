namespace ScreenshotApp.Capture;

/// <summary>
/// 优先使用高性能后端，失败或返回空帧时自动切换到兼容后端。
/// </summary>
public sealed class CaptureCoordinator : ICaptureBackend
{
    private readonly ICaptureBackend _primary;
    private readonly ICaptureBackend _fallback;

    public CaptureCoordinator(ICaptureBackend primary, ICaptureBackend fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public string Name => $"{_primary.Name} / {_fallback.Name}";

    public async Task<CaptureFrame> CaptureCurrentMonitorAsync(CancellationToken cancellationToken = default)
    {
        Exception? primaryError = null;

        try
        {
            var frame = await _primary.CaptureCurrentMonitorAsync(cancellationToken);
            if (!CaptureFrameAnalyzer.IsLikelyBlank(frame.Bitmap))
            {
                return frame;
            }

            primaryError = new BlankCaptureFrameException();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            primaryError = exception;
        }

        try
        {
            return await _fallback.CaptureCurrentMonitorAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception fallbackError)
        {
            throw new AggregateException("DXGI 与 GDI 截图均失败。", primaryError!, fallbackError);
        }
    }
}
