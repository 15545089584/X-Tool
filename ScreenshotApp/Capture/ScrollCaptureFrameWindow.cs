using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ScreenshotApp.Capture;

/// <summary>
/// 滚动采集期间突出当前选区：选区外降低亮度，选区内保持原始画面，
/// 同时整层完全穿透鼠标并从屏幕采集结果中排除。
/// </summary>
internal sealed class ScrollCaptureFrameWindow : Window
{
    private const double BorderSize = 3;
    private readonly Int32Rect _screenRegion;
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Rectangle _topMask = CreateMask();
    private readonly Rectangle _leftMask = CreateMask();
    private readonly Rectangle _rightMask = CreateMask();
    private readonly Rectangle _bottomMask = CreateMask();
    private readonly Border _selectionBorder = new()
    {
        BorderBrush = new SolidColorBrush(Color.FromRgb(77, 149, 255)),
        BorderThickness = new Thickness(BorderSize),
        CornerRadius = new CornerRadius(4),
        Background = Brushes.Transparent,
        IsHitTestVisible = false
    };
    private NativeMethods.Rect _monitorBounds;

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

        _canvas.Children.Add(_topMask);
        _canvas.Children.Add(_leftMask);
        _canvas.Children.Add(_rightMask);
        _canvas.Children.Add(_bottomMask);
        _canvas.Children.Add(_selectionBorder);
        Content = _canvas;

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => UpdateVisualGeometry();
        ContentRendered += (_, _) => UpdateVisualGeometry();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeWindowMouseTransparent(handle);
        _ = NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);

        var center = new NativeMethods.Point
        {
            X = _screenRegion.X + _screenRegion.Width / 2,
            Y = _screenRegion.Y + _screenRegion.Height / 2
        };
        var monitorHandle = NativeMethods.MonitorFromPoint(center, NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        _ = NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo);
        _monitorBounds = monitorInfo.Monitor;

        _ = NativeMethods.SetWindowPos(
            handle,
            new IntPtr(NativeMethods.HwndTopmost),
            _monitorBounds.Left,
            _monitorBounds.Top,
            _monitorBounds.Right - _monitorBounds.Left,
            _monitorBounds.Bottom - _monitorBounds.Top,
            NativeMethods.SwpNoActivate);
    }

    private void UpdateVisualGeometry()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _monitorBounds.Right <= _monitorBounds.Left)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = Math.Clamp(
            (_screenRegion.X - _monitorBounds.Left) / dpi.DpiScaleX,
            0,
            ActualWidth);
        var y = Math.Clamp(
            (_screenRegion.Y - _monitorBounds.Top) / dpi.DpiScaleY,
            0,
            ActualHeight);
        var width = Math.Clamp(_screenRegion.Width / dpi.DpiScaleX, 0, ActualWidth - x);
        var height = Math.Clamp(_screenRegion.Height / dpi.DpiScaleY, 0, ActualHeight - y);

        SetBounds(_topMask, 0, 0, ActualWidth, y);
        SetBounds(_leftMask, 0, y, x, height);
        SetBounds(_rightMask, x + width, y, Math.Max(0, ActualWidth - x - width), height);
        SetBounds(_bottomMask, 0, y + height, ActualWidth, Math.Max(0, ActualHeight - y - height));
        SetBounds(_selectionBorder, x - BorderSize, y - BorderSize, width + BorderSize * 2, height + BorderSize * 2);
        Panel.SetZIndex(_selectionBorder, 1);
    }

    private static Rectangle CreateMask() => new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(104, 26, 34, 45)),
        IsHitTestVisible = false
    };

    private static void SetBounds(FrameworkElement element, double left, double top, double width, double height)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }
}
