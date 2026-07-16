using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenshotApp.Capture;

/// <summary>
/// 长截图期间常驻的框线。框线绘制在采集区域外侧，并完全穿透鼠标输入。
/// </summary>
internal sealed class ScrollCaptureFrameWindow : Window
{
    private const int BorderSize = 3;
    private readonly Int32Rect _screenRegion;

    internal ScrollCaptureFrameWindow(Int32Rect screenRegion)
    {
        _screenRegion = screenRegion;
        Title = "长截图采集区域";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(77, 149, 255)),
            BorderThickness = new Thickness(BorderSize),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent
        };

        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeWindowMouseTransparent(handle);
        _ = NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        _ = NativeMethods.SetWindowPos(
            handle,
            new IntPtr(NativeMethods.HwndTopmost),
            _screenRegion.X - BorderSize,
            _screenRegion.Y - BorderSize,
            _screenRegion.Width + BorderSize * 2,
            _screenRegion.Height + BorderSize * 2,
            NativeMethods.SwpNoActivate);
        // HWND 本身也只保留外侧四条框线，避免透明窗口内部被 GDI 当作采集图层。
        NativeMethods.MakeWindowFrameOnly(
            handle,
            _screenRegion.Width + BorderSize * 2,
            _screenRegion.Height + BorderSize * 2,
            BorderSize);
    }
}
