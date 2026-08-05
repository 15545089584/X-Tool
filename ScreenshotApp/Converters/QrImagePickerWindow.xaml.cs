using System.Windows;
using System.Windows.Controls;
using ScreenshotApp.History;

namespace ScreenshotApp.Converters;

/// <summary>独立的居中图片选择弹窗：从剪贴板历史中挑选图片用于识别二维码。</summary>
public partial class QrImagePickerWindow : Window
{
    public QrImagePickerWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == System.Windows.Input.Key.Escape)
            {
                Close();
            }
        };
    }

    public event EventHandler<ScreenshotHistoryItem>? ItemSelected;

    public void SetItems(IReadOnlyList<ScreenshotHistoryItem> items)
    {
        var images = items.Where(item => item.HasThumbnail).ToList();
        ImageItemsPanel.ItemsSource = images;
        if (images.Count == 0)
        {
            LoadingText.Text = "暂无可用的图片记录";
            LoadingText.Visibility = Visibility.Visible;
        }
        else
        {
            LoadingText.Visibility = Visibility.Collapsed;
        }
    }

    private void ImageItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ScreenshotHistoryItem item })
        {
            ItemSelected?.Invoke(this, item);
            Close();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
