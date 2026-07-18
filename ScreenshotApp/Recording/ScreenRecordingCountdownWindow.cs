using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenshotApp.Capture;

namespace ScreenshotApp.Recording;

/// <summary>
/// 录像开始前的短暂倒计时提示，显示在所选屏幕上方且不会进入录像文件。
/// </summary>
internal sealed class ScreenRecordingCountdownWindow : Window
{
    private readonly Int32Rect _region;
    private readonly TextBlock _countdownText = new();

    internal ScreenRecordingCountdownWindow(Int32Rect region)
    {
        _region = region;
        Width = 226;
        Height = 74;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;

        _countdownText.Foreground = Brushes.White;
        _countdownText.FontSize = 26;
        _countdownText.FontWeight = FontWeights.Bold;
        _countdownText.HorizontalAlignment = HorizontalAlignment.Center;
        Content = new Border
        {
            Padding = new Thickness(18, 10, 18, 11),
            Background = new SolidColorBrush(Color.FromArgb(236, 24, 31, 42)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "即将开始录制",
                        Foreground = new SolidColorBrush(Color.FromRgb(211, 224, 241)),
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    _countdownText
                }
            }
        };
        SourceInitialized += OnSourceInitialized;
    }

    internal void SetRemainingSeconds(int seconds) => _countdownText.Text = seconds.ToString();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _ = NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(Width * dpi.DpiScaleX);
        var height = (int)Math.Round(Height * dpi.DpiScaleY);
        var x = _region.X + (_region.Width - width) / 2;
        var y = Math.Max(_region.Y + 22, 10);
        _ = NativeMethods.SetWindowPos(handle, new IntPtr(NativeMethods.HwndTopmost), x, y, width, height, NativeMethods.SwpNoActivate);
    }
}
