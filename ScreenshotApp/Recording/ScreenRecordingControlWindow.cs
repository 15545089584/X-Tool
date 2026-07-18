using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenshotApp.Capture;

namespace ScreenshotApp.Recording;

/// <summary>录像期间的轻量控制条，不进入最终录像画面。</summary>
internal sealed class ScreenRecordingControlWindow : Window
{
    private readonly Int32Rect _region;
    private readonly TextBlock _elapsedText = new()
    {
        Foreground = Brushes.White,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center
    };

    internal ScreenRecordingControlWindow(Int32Rect region)
    {
        _region = region;
        Width = 246;
        Height = 52;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;

        var stopButton = new Button
        {
            Content = "停止",
            Width = 62,
            Height = 30,
            Background = new SolidColorBrush(Color.FromRgb(236, 83, 96)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        stopButton.Click += (_, _) => IsStopRequested = true;
        Content = new Border
        {
            Padding = new Thickness(14, 10, 14, 10),
            Background = new SolidColorBrush(Color.FromArgb(232, 25, 31, 42)),
            CornerRadius = new CornerRadius(12),
            Child = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(16) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(62) }
                }
            }
        };
        var contentGrid = (Grid)((Border)Content).Child;
        var recordingDot = new TextBlock
        {
            Text = "●",
            Foreground = new SolidColorBrush(Color.FromRgb(255, 90, 104)),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Grid.SetColumn(_elapsedText, 1);
        Grid.SetColumn(stopButton, 2);
        contentGrid.Children.Add(recordingDot);
        contentGrid.Children.Add(_elapsedText);
        contentGrid.Children.Add(stopButton);
        SourceInitialized += OnSourceInitialized;
    }

    internal bool IsStopRequested { get; private set; }

    internal void SetElapsed(TimeSpan elapsed) => _elapsedText.Text = $"正在录像 {elapsed:mm\\:ss}";

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _ = NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(Width * dpi.DpiScaleX);
        var height = (int)Math.Round(Height * dpi.DpiScaleY);
        var x = _region.X + _region.Width - width;
        var y = _region.Y + _region.Height + 12;
        _ = NativeMethods.SetWindowPos(handle, new IntPtr(NativeMethods.HwndTopmost), x, y, width, height, NativeMethods.SwpNoActivate);
        SetElapsed(TimeSpan.Zero);
    }
}
