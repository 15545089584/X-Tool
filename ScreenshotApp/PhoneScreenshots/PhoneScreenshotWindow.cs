using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenshotApp.ClipboardUi;
using ScreenshotApp.Collaboration;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.PhoneScreenshots;

/// <summary>独立截图气泡，按钮仅操作该气泡绑定的原图。</summary>
internal sealed class PhoneScreenshotWindow : Window
{
    private readonly string _path;
    private readonly Window _pet;
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _status;
    private bool _busy;
    private DateTime _expires = DateTime.UtcNow.AddSeconds(30);

    internal PhoneScreenshotWindow(string path, BitmapSource thumbnail, Window pet)
    {
        _path = path; _pet = pet;
        Title = "X-Tool 手机截图";
        Width = 340; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var stack = new StackPanel();
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = MakeButton("×", false);
        close.Width = 32; close.Height = 30; close.Padding = new Thickness(0);
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
        heading.Children.Add(new TextBlock { Text = "手机截图", FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = ColorBrush("#29445F"), VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(heading);
        var image = new Image { Source = thumbnail, MaxHeight = 220, Stretch = Stretch.Uniform, Cursor = Cursors.Hand,
            ToolTip = "点击使用 Windows 默认图片程序打开" };
        var preview = MakeButton("", false);
        preview.Content = image; preview.Padding = new Thickness(6); preview.Height = double.NaN;
        preview.Click += (_, _) =>
        {
            try { PhoneScreenshotPresenter.Open(_path); }
            catch (Exception e) { _status!.Text = "无法打开：" + e.Message; }
        };
        stack.Children.Add(preview);
        _status = new TextBlock { Text = "已接收 · 点击图片查看原图", Foreground = ColorBrush("#72889D"), FontSize = 12,
            Margin = new Thickness(0, 10, 0, 12), TextWrapping = TextWrapping.Wrap };
        stack.Children.Add(_status);
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
        var copy = MakeButton("复制", true); copy.Margin = new Thickness(0, 0, 5, 0);
        var save = MakeButton("保存", false); save.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(save, 1); actions.Children.Add(copy); actions.Children.Add(save);
        copy.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; copy.IsEnabled = save.IsEnabled = false;
            try
            {
                var fullImage = await Task.Run(() => PhoneScreenshotPresenter.LoadImage(_path, false));
                if (IsVisible && !PhoneNotificationHub.Instance.Locked)
                {
                    ClipboardService.SetImage(fullImage);
                    _status.Text = "图片已复制，可以直接粘贴";
                }
            }
            catch (Exception e) { _status.Text = "复制失败：" + e.Message; }
            finally { _busy = false; copy.IsEnabled = save.IsEnabled = true; _expires = DateTime.UtcNow.AddSeconds(15); }
        };
        save.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; copy.IsEnabled = save.IsEnabled = false;
            try
            {
                var extension = Path.GetExtension(_path);
                var dialog = new SaveFileDialog { FileName = Path.GetFileName(_path), DefaultExt = extension,
                    Filter = $"截图图片 (*{extension})|*{extension}", AddExtension = true, OverwritePrompt = true };
                if (dialog.ShowDialog(this) == true)
                {
                    if (!string.Equals(Path.GetFullPath(_path), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase))
                        await Task.Run(() => File.Copy(_path, dialog.FileName, true));
                    _status.Text = "图片已保存";
                }
            }
            catch (Exception e) { _status.Text = "保存失败：" + e.Message; }
            finally { _busy = false; copy.IsEnabled = save.IsEnabled = true; _expires = DateTime.UtcNow.AddSeconds(15); }
        };
        stack.Children.Add(actions);
        var card = new Border { Background = ColorBrush("#F4F9FF"), BorderBrush = ColorBrush("#C4D9F4"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20), Padding = new Thickness(18), Child = stack };
        var outer = new StackPanel { Margin = new Thickness(8) };
        outer.Children.Add(card);
        outer.Children.Add(new System.Windows.Shapes.Polygon { Points = new PointCollection { new(0, 0), new(18, 0), new(9, 11) },
            Fill = ColorBrush("#C4D9F4"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -1, 32, 0) });
        Content = outer;
        SourceInitialized += (_, _) =>
        {
            // 气泡属于辅助窗，不进入本机截图和录屏。
            ScreenshotApp.Capture.NativeMethods.SetWindowDisplayAffinity(
                new WindowInteropHelper(this).Handle, ScreenshotApp.Capture.NativeMethods.WdaExcludeFromCapture);
        };
        Loaded += (_, _) => PositionNearPet();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            if (!_pet.IsVisible || PhoneNotificationHub.Instance.Locked) { Close(); return; }
            PositionNearPet();
            if (IsMouseOver || _busy) _expires = DateTime.UtcNow.AddSeconds(15);
            else if (DateTime.UtcNow >= _expires) Close();
        };
        Closed += (_, _) => _timer.Stop();
        _timer.Start();
    }

    private void PositionNearPet()
    {
        var area = Forms.Screen.FromHandle(new WindowInteropHelper(_pet).Handle).WorkingArea;
        var transform = PresentationSource.FromVisual(_pet)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(area.Left, area.Top));
        var bottomRight = transform.Transform(new Point(area.Right, area.Bottom));
        Left = Math.Clamp(_pet.Left + _pet.ActualWidth - ActualWidth, topLeft.X, Math.Max(topLeft.X, bottomRight.X - ActualWidth));
        Top = Math.Clamp(_pet.Top - ActualHeight + 8, topLeft.Y, Math.Max(topLeft.Y, bottomRight.Y - ActualHeight));
    }

    private static SolidColorBrush ColorBrush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    private static Button MakeButton(string label, bool primary)
    {
        var button = new Button { Content = label, Height = 40, FontSize = 14, Cursor = Cursors.Hand,
            Background = ColorBrush(primary ? "#4D77FF" : "#E8F0FC"), Foreground = primary ? Brushes.White : ColorBrush("#355875"),
            BorderThickness = new Thickness(0), Padding = new Thickness(12, 5, 12, 5) };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        button.MouseEnter += (_, _) => button.Opacity = 0.86;
        button.MouseLeave += (_, _) => button.Opacity = 1;
        return button;
    }
}
