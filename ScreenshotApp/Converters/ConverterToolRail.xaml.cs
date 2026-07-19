using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenshotApp.Converters;

public sealed class ConverterToolRequestedEventArgs : EventArgs
{
    public ConverterToolRequestedEventArgs(string tool) => Tool = tool;

    public string Tool { get; }
}

public partial class ConverterToolRail : UserControl
{
    public static readonly DependencyProperty SelectedToolProperty = DependencyProperty.Register(
        nameof(SelectedTool), typeof(string), typeof(ConverterToolRail), new PropertyMetadata(string.Empty, OnSelectedToolChanged));

    public ConverterToolRail()
    {
        InitializeComponent();
        UpdateSelection();
    }

    public string SelectedTool
    {
        get => (string)GetValue(SelectedToolProperty);
        set => SetValue(SelectedToolProperty, value);
    }

    public event EventHandler<ConverterToolRequestedEventArgs>? ToolRequested;

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tool })
        {
            ToolRequested?.Invoke(this, new ConverterToolRequestedEventArgs(tool));
        }
    }

    private static void OnSelectedToolChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is ConverterToolRail rail)
        {
            rail.UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        if (ImageButton is null)
        {
            return;
        }

        SetButtonState(ImageButton, SelectedTool == "Image", Color.FromRgb(126, 168, 247));
        SetButtonState(AudioButton, SelectedTool == "Audio", Color.FromRgb(76, 184, 137));
        SetButtonState(VideoButton, SelectedTool == "Video", Color.FromRgb(138, 104, 218));
    }

    private static void SetButtonState(Button button, bool selected, Color accent)
    {
        button.BorderThickness = selected ? new Thickness(1.5) : new Thickness(1);
        button.BorderBrush = new SolidColorBrush(selected ? accent : Color.FromRgb(182, 213, 232));
        button.Background = new SolidColorBrush(selected ? Color.FromArgb(224, 239, 246, 255) : Color.FromArgb(154, 255, 255, 255));
    }
}
