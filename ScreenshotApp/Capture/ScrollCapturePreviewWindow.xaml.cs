using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenshotApp.Capture;

/// <summary>
/// 长截图期间显示的实时总览。窗口不获取焦点、不接收鼠标，也不会进入截图画面。
/// </summary>
public partial class ScrollCapturePreviewWindow : Window
{
    private const int WindowHorizontalPadding = 36;
    private const int WindowVerticalChrome = 66;
    private const int RegionGap = 14;
    private readonly Int32Rect _screenRegion;
    private readonly object _previewSync = new();
    private readonly CancellationTokenSource _previewCancellation = new();
    private IntPtr _handle;
    private PreviewRequest? _pendingPreview;
    private bool _previewWorkerRunning;

    private sealed record PreviewRequest(
        VerticalBitmapStitcher.PositionedFrame[] Frames,
        int CapturedHeight,
        FixedViewportInsets Insets);

    internal ScrollCapturePreviewWindow(Int32Rect screenRegion)
    {
        _screenRegion = screenRegion;
        InitializeComponent();
        Closed += (_, _) => _previewCancellation.Cancel();
#if SCROLL_CAPTURE_TEST
        // 专用回归构建允许自动化直接定位并检查预览窗口；正式版仍隐藏任务栏入口。
        ShowInTaskbar = true;
#endif
    }

    internal void UpdatePreview(
        IReadOnlyList<VerticalBitmapStitcher.PositionedFrame> frames,
        int capturedHeight,
        FixedViewportInsets insets = default)
    {
        HeightText.Text = $"已采集 {capturedHeight:N0} 像素";
        lock (_previewSync)
        {
            // 采样速度高于预览绘制速度时只保留最新请求，避免排队生成已经过时的缩略图。
            _pendingPreview = new PreviewRequest(frames.ToArray(), capturedHeight, insets);
            if (_previewWorkerRunning)
            {
                return;
            }

            _previewWorkerRunning = true;
        }

        _ = ProcessPreviewQueueAsync();
    }

    private async Task ProcessPreviewQueueAsync()
    {
        try
        {
            while (!_previewCancellation.IsCancellationRequested)
            {
                PreviewRequest? request;
                lock (_previewSync)
                {
                    request = _pendingPreview;
                    _pendingPreview = null;
                    if (request is null)
                    {
                        _previewWorkerRunning = false;
                        return;
                    }
                }

                // 接缝搜索和像素比较放到后台线程；被冻结的 BitmapSource 可安全跨线程读取。
                var layout = await Task.Run(
                    () => VerticalBitmapStitcher.BuildPositionedLayout(
                        request.Frames,
                        request.Insets),
                    _previewCancellation.Token);

                lock (_previewSync)
                {
                    if (_pendingPreview is not null)
                    {
                        // 计算期间已经有更新的帧，跳过旧结果并立即处理最新请求。
                        continue;
                    }
                }

                var bitmap = ScrollCapturePreviewRenderer.RenderLayout(layout);
                if (_previewCancellation.IsCancellationRequested)
                {
                    return;
                }

                PreviewImage.Source = bitmap;
                PreviewImage.Height = bitmap.PixelHeight;
                HeightText.Text = $"已采集 {request.CapturedHeight:N0} 像素";
                Width = ScrollCapturePreviewRenderer.DefaultPreviewWidth + WindowHorizontalPadding;
                Height = bitmap.PixelHeight + WindowVerticalChrome;
                UpdateLayout();
                PositionWindow();
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭预览窗口时取消后台计算，不再更新已销毁的界面。
        }
        catch
        {
            // 预览失败不应中断长截图主流程，下一帧到来时仍可继续尝试。
        }
        finally
        {
            lock (_previewSync)
            {
                _previewWorkerRunning = false;
            }
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
#if !SCROLL_CAPTURE_TEST
        NativeMethods.MakeWindowMouseTransparent(_handle);
        _ = NativeMethods.SetWindowDisplayAffinity(
            _handle,
            NativeMethods.WdaExcludeFromCapture);
#endif
        PositionWindow();
    }

    private void PositionWindow()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var center = new NativeMethods.Point
        {
            X = _screenRegion.X + _screenRegion.Width / 2,
            Y = _screenRegion.Y + _screenRegion.Height / 2
        };
        var monitorHandle = NativeMethods.MonitorFromPoint(
            center,
            NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        _ = NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo);

        var dpi = VisualTreeHelper.GetDpi(this);
        var physicalWidth = Math.Max(1, (int)Math.Round(Width * dpi.DpiScaleX));
        var physicalHeight = Math.Max(1, (int)Math.Round(Height * dpi.DpiScaleY));
        var workArea = monitorInfo.WorkArea;

        var rightCandidate = _screenRegion.X + _screenRegion.Width + RegionGap;
        var leftCandidate = _screenRegion.X - physicalWidth - RegionGap;
        int x;
        if (rightCandidate + physicalWidth <= workArea.Right - 8)
        {
            x = rightCandidate;
        }
        else if (leftCandidate >= workArea.Left + 8)
        {
            x = leftCandidate;
        }
        else
        {
            // 选区接近全屏时放在选区右上角。窗口已从采集层排除，
            // 且完全穿透鼠标，不会遮挡滚动操作或进入最终图片。
            x = Math.Clamp(
                _screenRegion.X + _screenRegion.Width - physicalWidth - 12,
                workArea.Left + 8,
                Math.Max(workArea.Left + 8, workArea.Right - physicalWidth - 8));
        }

        var y = Math.Clamp(
            _screenRegion.Y + 12,
            workArea.Top + 8,
            Math.Max(workArea.Top + 8, workArea.Bottom - physicalHeight - 8));
        _ = NativeMethods.SetWindowPos(
            _handle,
            new IntPtr(NativeMethods.HwndTopmost),
            x,
            y,
            physicalWidth,
            physicalHeight,
            NativeMethods.SwpNoActivate);
    }
}
