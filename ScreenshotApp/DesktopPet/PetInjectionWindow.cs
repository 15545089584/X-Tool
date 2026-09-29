using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenshotApp.Capture;

namespace ScreenshotApp.DesktopPet;

internal sealed class PetInjectionWindow : Window
{
    internal event Action<string>? InjectRequested;
    internal TextBox Editor { get; } = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14, Padding = new Thickness(12), MaxLength = 100000 };
    internal TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 12) };
    internal ProgressBar Progress { get; } = new() { Height = 6, Minimum = 0, Maximum = 100, Foreground = new SolidColorBrush(Color.FromRgb(77, 124, 254)) };
    internal PetInjectionWindow(bool editing)
    {
        Title = editing ? "注入 · X-Tool" : "注入进度 · X-Tool";
        Width = editing ? 430 : 280; Height = editing ? 340 : 94;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
        ShowInTaskbar = false; ShowActivated = editing;
        FontFamily = new FontFamily("Microsoft YaHei UI"); Foreground = new SolidColorBrush(Color.FromRgb(48, 74, 99));
        Resources = new ResourceDictionary { Source = new Uri("/XTool;component/DesktopPet/PetBubbleStyles.xaml", UriKind.Relative) };
        Foreground = (Brush)FindResource("MailInk");
        Progress.Foreground = (Brush)FindResource("MailGradient");
        Progress.Background = new SolidColorBrush(Color.FromRgb(221, 229, 240));
        var track = new FrameworkElementFactory(typeof(Grid), "PART_Track");
        var trackSurface = new FrameworkElementFactory(typeof(Border));
        trackSurface.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        trackSurface.SetValue(Border.BackgroundProperty, Progress.Background);
        track.AppendChild(trackSurface);
        var indicator = new FrameworkElementFactory(typeof(Border), "PART_Indicator");
        indicator.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        indicator.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        indicator.SetValue(Border.BackgroundProperty, Progress.Foreground);
        track.AppendChild(indicator);
        Progress.Template = new ControlTemplate(typeof(ProgressBar)) { VisualTree = track };
        var background = (Brush)FindResource("PetBubbleBackground");
        var grid = new Grid();
        Content = new Border { Background = background, BorderBrush = new SolidColorBrush(Color.FromRgb(221, 229, 240)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(18), Child = grid };
        if (!editing)
        {
            var stack = new StackPanel(); stack.Children.Add(Status); stack.Children.Add(Progress); grid.Children.Add(stack);
            SourceInitialized += (_, _) => NativeMethods.MakeWindowMouseTransparent(new WindowInteropHelper(this).Handle);
            return;
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new Border { Width = 4, Height = 22, CornerRadius = new CornerRadius(2), Background = (Brush)FindResource("MailGradient"), Margin = new Thickness(0, 0, 10, 0) });
        heading.Children.Add(new TextBlock { Text = "注入", FontSize = 18, FontWeight = FontWeights.SemiBold });
        grid.Children.Add(heading);
        Editor.Background = new SolidColorBrush(Color.FromArgb(207, 255, 255, 255));
        Editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Grid.SetRow(Editor, 1); grid.Children.Add(Editor);
        var buttons = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); buttons.ColumnDefinitions.Add(new ColumnDefinition());
        var close = Button("关闭", false); var inject = Button("注入", true); inject.IsEnabled = false;
        close.Click += (_, _) => Close();
        inject.Click += (_, _) => { var text = Editor.Text; Close(); InjectRequested?.Invoke(text); };
        Editor.TextChanged += (_, _) => inject.IsEnabled = Editor.Text.Length > 0;
        buttons.Children.Add(close); Grid.SetColumn(inject, 2); buttons.Children.Add(inject);
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        Loaded += (_, _) => Editor.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
    }
    private Button Button(string text, bool primary)
    {
        var button = new Button { Content = new TextBlock { Text = text, Foreground = primary ? Brushes.White : (Brush)FindResource("MailInk") }, Height = 34, Padding = new Thickness(12, 0, 12, 0), FocusVisualStyle = null,
            Style = (Style)FindResource(primary ? "MailPrimary" : typeof(Button)) };
        if (!primary) button.Background = new SolidColorBrush(Color.FromArgb(207, 255, 255, 255));
        return button;
    }
}
