using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenshotApp.Capture;

/// <summary>
/// 自由长截图控制条。用户手动滚动，可随时结束或取消采集。
/// </summary>
public partial class ScrollCaptureToolbarWindow : Window
{
    private readonly Int32Rect _screenRegion;

    internal ScrollCaptureToolbarWindow(Int32Rect screenRegion)
    {
        _screenRegion = screenRegion;
        InitializeComponent();
#if DEBUG
        // 调试构建保留任务栏入口，便于自动化回归定位控制条。
        ShowInTaskbar = true;
#endif
    }

    internal bool IsFinishRequested { get; private set; }

    internal bool IsCancelRequested { get; private set; }

    internal void SetProgress(string status, string detail, bool isWarning = false)
    {
        StatusText.Text = status;
        DetailText.Text = detail;
        if (isWarning)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(173, 101, 15));
            StatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(220, 139, 35));
            StatusIconBackground.Background = new SolidColorBrush(Color.FromRgb(255, 241, 218));
            StatusIcon.Text = "\uE7BA";
        }
        else
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(32, 38, 48));
            StatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(49, 124, 245));
            StatusIconBackground.Background = new SolidColorBrush(Color.FromRgb(231, 240, 255));
            StatusIcon.Text = "\uE74A";
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
#if !DEBUG
        _ = NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
#endif

        var center = new NativeMethods.Point
        {
            X = _screenRegion.X + _screenRegion.Width / 2,
            Y = _screenRegion.Y + _screenRegion.Height / 2
        };
        var monitorHandle = NativeMethods.MonitorFromPoint(center, NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        _ = NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo);

        // SetWindowPos 使用物理像素，而 XAML 的 Width/Height 使用 DIP。
        // 在 125%/150% 缩放下必须换算，否则窗口会被系统压窄，提示文本和按钮就会重叠。
        var dpi = VisualTreeHelper.GetDpi(this);
        var toolbarWidth = Math.Max(1, (int)Math.Round(Width * dpi.DpiScaleX));
        var toolbarHeight = Math.Max(1, (int)Math.Round(Height * dpi.DpiScaleY));
        const int gap = 10;
        var workArea = monitorInfo.WorkArea;
        var x = Math.Clamp(
            _screenRegion.X + _screenRegion.Width - toolbarWidth,
            workArea.Left + 8,
            Math.Max(workArea.Left + 8, workArea.Right - toolbarWidth - 8));
        var y = _screenRegion.Y + _screenRegion.Height + gap;
        if (y + toolbarHeight > workArea.Bottom - 8)
        {
            y = _screenRegion.Y - toolbarHeight - gap;
        }

        if (y < workArea.Top + 8)
        {
            y = Math.Clamp(
                _screenRegion.Y + _screenRegion.Height - toolbarHeight - 12,
                workArea.Top + 8,
                Math.Max(workArea.Top + 8, workArea.Bottom - toolbarHeight - 8));
        }

        _ = NativeMethods.SetWindowPos(
            handle,
            new IntPtr(NativeMethods.HwndTopmost),
            x,
            y,
            toolbarWidth,
            toolbarHeight,
            0);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            IsCancelRequested = true;
            e.Handled = true;
        }
    }

    private void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        IsFinishRequested = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        IsCancelRequested = true;
    }
}
