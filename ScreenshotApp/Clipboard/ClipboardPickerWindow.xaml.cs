using System.Windows;
using System.Windows.Controls;
using ScreenshotApp.History;

namespace ScreenshotApp.ClipboardUi;

/// <summary>用于快速选择最近剪贴板内容的小型浮窗。</summary>
public partial class ClipboardPickerWindow : Window
{
    private readonly IReadOnlyList<ScreenshotHistoryItem> _items;

    public ClipboardPickerWindow(IEnumerable<ScreenshotHistoryItem> items)
    {
        InitializeComponent();
        _items = items.ToArray();
        ApplyFilter("All");
        PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == System.Windows.Input.Key.Escape)
            {
                Close();
            }
        };
    }

    public event EventHandler<ScreenshotHistoryItem>? ItemSelected;

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string filter })
        {
            ApplyFilter(filter);
        }
    }

    private void ApplyFilter(string filter)
    {
        ClipboardItemsList.SelectedItem = null;
        ClipboardItemsList.ItemsSource = filter switch
        {
            "Image" => _items.Where(item => item.Kind is HistoryEntryKind.Screenshot or HistoryEntryKind.LongScreenshot),
            "Text" => _items.Where(item => item.Kind == HistoryEntryKind.ExternalClipboard && item.IsTextRecord),
            "Translation" => _items.Where(item => item.Kind == HistoryEntryKind.Translation),
            "TextExtraction" => _items.Where(item => item.Kind == HistoryEntryKind.TextExtraction),
            "External" => _items.Where(item => item.Kind == HistoryEntryKind.ExternalClipboard),
            _ => _items
        };
    }

    private void ClipboardItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipboardItemsList.SelectedItem is ScreenshotHistoryItem item)
        {
            ItemSelected?.Invoke(this, item);
        }
    }
}
