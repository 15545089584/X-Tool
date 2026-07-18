using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenshotApp.Capture;

namespace ScreenshotApp.Recording;

/// <summary>
/// 录像期间标识当前采集区域。窗口不接收输入且被系统排除在捕获画面外。
/// </summary>
internal sealed class ScreenRecordingRegionWindow : Window
{
    private readonly Int32Rect _region;

    internal ScreenRecordingRegionWindow(Int32Rect region)
    {
        _region = region;
        // 初始化后按当前监视器 DPI 折算为 WPF 逻辑尺寸，避免高 DPI 下被裁切。
        Width = 1;
        Height = 1;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;

        var root = new Grid { IsHitTestVisible = false };
        root.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(244, 79, 91)),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        });
        var label = new Border
        {
            Padding = new Thickness(10, 5, 10, 5),
            Background = new SolidColorBrush(Color.FromArgb(228, 225, 61, 75)),
            CornerRadius = new CornerRadius(0, 0, 7, 0),
            Child = new TextBlock
            {
                Text = "● 正在录制此区域",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            }
        };
        label.HorizontalAlignment = HorizontalAlignment.Left;
        label.VerticalAlignment = VerticalAlignment.Top;
        label.Margin = new Thickness(3);
        root.Children.Add(label);
        Content = root;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _ = NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        var dpi = VisualTreeHelper.GetDpi(this);
        Width = _region.Width / dpi.DpiScaleX;
        Height = _region.Height / dpi.DpiScaleY;
        _ = NativeMethods.SetWindowPos(
            handle,
            new IntPtr(NativeMethods.HwndTopmost),
            _region.X,
            _region.Y,
            _region.Width,
            _region.Height,
            NativeMethods.SwpNoActivate);
    }
}
