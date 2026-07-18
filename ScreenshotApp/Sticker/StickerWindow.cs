using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ScreenshotApp.Capture;
using ScreenshotApp.Settings;

namespace ScreenshotApp.Sticker;

/// <summary>承载截图贴图的独立桌面窗口，不参与后续的截图和录像。</summary>
internal sealed class StickerWindow : Window
{
    private readonly Int32Rect _screenBounds;
    private readonly TextBlock _pinIcon;

    internal StickerWindow(BitmapSource bitmap, Int32Rect screenBounds)
    {
        _screenBounds = screenBounds;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = AppPreferences.Load().StickerTopmost;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        };

        var imageFrame = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 248, 251, 253)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 216, 231, 246)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(3),
            Child = image,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(38, 59, 86),
                BlurRadius = 20,
                ShadowDepth = 5,
                Opacity = 0.34
            }
        };

        var closeButton = CreateActionButton("×", "关闭贴图");
        closeButton.Click += (_, _) => Close();

        _pinIcon = new TextBlock
        {
            Text = "📌",
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Opacity = Topmost ? 1 : 0.48
        };
        var pinButton = CreateActionButton(_pinIcon, "置顶");
        pinButton.Click += (_, _) => ToggleTopmost();

        var actionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 9, 9, 0)
        };
        actionPanel.Children.Add(pinButton);
        actionPanel.Children.Add(closeButton);

        var root = new Grid();
        root.Children.Add(imageFrame);
        root.Children.Add(actionPanel);
        root.PreviewMouseLeftButtonDown += Root_PreviewMouseLeftButtonDown;
        Content = root;

        SourceInitialized += OnSourceInitialized;
    }

    private static Button CreateActionButton(string text, string toolTip)
    {
        return CreateActionButton(new TextBlock
        {
            Text = text,
            FontSize = 18,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }, toolTip);
    }

    private static Button CreateActionButton(UIElement content, string toolTip)
    {
        var button = new Button
        {
            Width = 28,
            Height = 28,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(0),
            Content = content,
            ToolTip = toolTip,
            Cursor = Cursors.Hand,
            Foreground = new SolidColorBrush(Color.FromRgb(43, 57, 74)),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Template = CreateActionButtonTemplate()
        };
        return button;
    }

    private static ControlTemplate CreateActionButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Background";
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(170, 205, 218, 233)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(226, 237, 251)), "Background"));
        template.Triggers.Add(hover);
        return template;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _ = NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        _ = NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            _screenBounds.X,
            _screenBounds.Y,
            _screenBounds.Width,
            _screenBounds.Height,
            NativeMethods.SwpNoActivate);
    }

    private void Root_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标状态在窗口切换期间可能变化，此时保持当前贴图位置即可。
        }
    }

    private void ToggleTopmost()
    {
        Topmost = !Topmost;
        _pinIcon.Opacity = Topmost ? 1 : 0.48;
    }

    private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T result)
            {
                return result;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
