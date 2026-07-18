using System.Windows;
using System.Windows.Controls;
using ScreenshotApp.History;

namespace ScreenshotApp.ClipboardUi;

/// <summary>用于快速选择最近剪贴板内容的小型浮窗。</summary>
public partial class ClipboardPickerWindow : Window
{
    public ClipboardPickerWindow(IEnumerable<ScreenshotHistoryItem> items)
    {
        InitializeComponent();
        ClipboardItemsList.ItemsSource = items;
        PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == System.Windows.Input.Key.Escape)
            {
                Close();
            }
        };
    }

    public event EventHandler<ScreenshotHistoryItem>? ItemSelected;

    private void ClipboardItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipboardItemsList.SelectedItem is ScreenshotHistoryItem item)
        {
            ItemSelected?.Invoke(this, item);
        }
    }
}
