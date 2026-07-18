using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
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
        Width = region.Width;
        Height = region.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;

        var canvas = new Canvas { IsHitTestVisible = false };
        canvas.Children.Add(new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(244, 79, 91)),
            StrokeThickness = 3,
            RadiusX = 4,
            RadiusY = 4,
            Width = Math.Max(0, region.Width - 3),
            Height = Math.Max(0, region.Height - 3)
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
        Canvas.SetLeft(label, 2);
        Canvas.SetTop(label, 2);
        canvas.Children.Add(label);
        Content = canvas;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _ = NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
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
