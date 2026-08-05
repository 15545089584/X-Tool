using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ScreenshotApp.Capture;
using ScreenshotApp.History;

namespace ScreenshotApp.ClipboardUi;

/// <summary>用于快速选择最近剪贴板内容的小型浮窗。</summary>
public partial class ClipboardPickerWindow : Window
{
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private readonly IReadOnlyList<ScreenshotHistoryItem> _items;
    private HwndSource? _windowSource;

    public ClipboardPickerWindow(IEnumerable<ScreenshotHistoryItem> items, string initialFilter = "All")
    {
        InitializeComponent();
        _items = items.ToArray();
        if (initialFilter == "Image")
        {
            // 仅图片模式：只展示图片类历史，隐藏分类切换，标题同步说明用途。
            FilterBar.Visibility = Visibility.Collapsed;
            TitleText.Text = "选择剪贴板图片";
        }
        ApplyFilter(initialFilter);
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => _windowSource?.RemoveHook(WindowMessageHook);
        PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == System.Windows.Input.Key.Escape)
            {
                Close();
            }
        };
    }

    public event EventHandler<ScreenshotHistoryItem>? ItemSelected;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeWindowNonActivating(handle);
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            // 允许继续接收鼠标点击，但不让浮窗成为前台窗口，保留原输入框的插入光标。
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        return IntPtr.Zero;
    }

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
        var items = filter switch
        {
            "Image" => _items.Where(item => item.HasThumbnail),
            "Text" => _items.Where(item => item.Kind == HistoryEntryKind.ExternalClipboard && item.IsTextRecord),
            "Translation" => _items.Where(item => item.Kind == HistoryEntryKind.Translation),
            "TextExtraction" => _items.Where(item => item.Kind == HistoryEntryKind.TextExtraction),
            "External" => _items.Where(item => item.Kind == HistoryEntryKind.ExternalClipboard),
            _ => _items
        };
        ClipboardItemsPanel.ItemsSource = items
            .Take(18)
            .Select(item => new ClipboardPickerItem(item, GetPaletteKind(item, filter)))
            .ToArray();
    }

    private void ClipboardItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClipboardPickerItem pickerItem })
        {
            ItemSelected?.Invoke(this, pickerItem.Item);
        }
    }

    /// <summary>筛选页按当前语义配色；全部页则优先使用实际内容类型。</summary>
    private static string GetPaletteKind(ScreenshotHistoryItem item, string filter)
    {
        if (filter != "All")
        {
            return filter;
        }

        return item.Kind switch
        {
            HistoryEntryKind.Translation => "Translation",
            HistoryEntryKind.TextExtraction => "TextExtraction",
            _ when item.HasThumbnail => "Image",
            _ when item.IsTextRecord => "Text",
            _ => "External"
        };
    }
}

/// <summary>剪贴板浮窗使用的展示模型，保留原始记录并独立决定当前分类的视觉样式。</summary>
internal sealed record ClipboardPickerItem(ScreenshotHistoryItem Item, string PaletteKind)
{
    public string PreviewText => Item.PreviewText;

    public System.Windows.Media.Imaging.BitmapSource? Thumbnail => Item.Thumbnail;

    public bool HasThumbnail => Item.HasThumbnail;
}
