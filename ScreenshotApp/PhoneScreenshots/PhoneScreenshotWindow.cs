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
    private IDisposable? _petImageReminder;
    private DateTime _expires = DateTime.UtcNow.AddSeconds(10);

    internal PhoneScreenshotWindow(string path, BitmapSource thumbnail, ScreenshotApp.DesktopPet.DesktopPetWindow pet, bool photo = false)
    {
        _path = path; _pet = pet;
        Title = photo ? "X-Tool 手机照片" : "X-Tool 手机截图";
        Width = 340; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Resources = new ResourceDictionary { Source = new Uri("/XTool;component/DesktopPet/PetBubbleStyles.xaml", UriKind.Relative) };
        var stack = new StackPanel();
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = MakeButton("×", false);
        close.Width = 30; close.Height = 30; close.Padding = new Thickness(0);
        close.ToolTip = "关闭";
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭截图提醒");
        close.Content = new System.Windows.Shapes.Path {
            Data = Geometry.Parse("M 1,1 L 11,11 M 11,1 L 1,11"),
            Stroke = ColorBrush("#66819B"), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 12, Height = 12, Stretch = Stretch.Uniform
        };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
        heading.Children.Add(new TextBlock { Text = photo ? "手机照片" : "手机截图", FontSize = 18, FontWeight = FontWeights.SemiBold,
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
                    Close();
                }
            }
            catch (Exception e) { _status.Text = "复制失败：" + e.Message; }
            finally { _busy = false; copy.IsEnabled = save.IsEnabled = true; }
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
            finally { _busy = false; copy.IsEnabled = save.IsEnabled = true; }
        };
        stack.Children.Add(actions);
        var card = new Border { Background = (Brush)FindResource("PetBubbleBackground"), BorderBrush = (Brush)FindResource("PetBubbleBorder"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(18), Child = stack };
        var outer = new StackPanel { Margin = new Thickness(8) };
        outer.Children.Add(card);
        outer.Children.Add(new System.Windows.Shapes.Polygon { Points = new PointCollection { new(0, 0), new(18, 0), new(9, 11) },
            Fill = (Brush)FindResource("PetBubbleBackground"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -1, 32, 0) });
        Content = outer;
        SourceInitialized += (_, _) =>
        {
            // 气泡属于辅助窗，不进入本机截图和录屏。
            ScreenshotApp.Capture.NativeMethods.SetWindowDisplayAffinity(
                new WindowInteropHelper(this).Handle, ScreenshotApp.Capture.NativeMethods.WdaExcludeFromCapture);
        };
        Loaded += (_, _) => {
            _expires = DateTime.UtcNow.AddSeconds(10); PositionNearPet();
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            if (!_pet.IsVisible || PhoneNotificationHub.Instance.Locked) { Close(); return; }
            PositionNearPet();
            // 从显示开始计时，悬停和复制不续期；保存对话框打开时等待操作结束。
            if (!_busy && DateTime.UtcNow >= _expires) Close();
        };
        Closed += (_, _) => { _timer.Stop(); _petImageReminder?.Dispose(); _petImageReminder = null; };
        _timer.Start();
        // 构造新气泡时先取得状态租约，再关闭旧气泡，连续图片不会短暂回到状态 1。
        _petImageReminder = pet.BeginPhoneImageReminder();
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
    private Button MakeButton(string label, bool primary)
    {
        // 直接给文字设置前景色，避免应用级 TextBlock 样式覆盖按钮继承颜色。
        var text = new TextBlock { Text = label, Foreground = primary ? Brushes.White : ColorBrush("#355875"),
            FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = text, Height = 40, FontSize = 14, Cursor = Cursors.Hand,
            Background = primary ? (Brush)FindResource("MailGradient") : ColorBrush("#CFFFFFFF"), Foreground = primary ? Brushes.White : ColorBrush("#355875"),
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
