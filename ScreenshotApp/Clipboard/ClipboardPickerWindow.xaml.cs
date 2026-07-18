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

    public ClipboardPickerWindow(IEnumerable<ScreenshotHistoryItem> items)
    {
        InitializeComponent();
        _items = items.ToArray();
        ApplyFilter("All");
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
        ClipboardItemsList.SelectedItem = null;
        var items = filter switch
        {
            "Image" => _items.Where(item => item.HasThumbnail),
            "Text" => _items.Where(item => item.Kind == HistoryEntryKind.ExternalClipboard && item.IsTextRecord),
            "Translation" => _items.Where(item => item.Kind == HistoryEntryKind.Translation),
            "TextExtraction" => _items.Where(item => item.Kind == HistoryEntryKind.TextExtraction),
            "External" => _items.Where(item => item.Kind == HistoryEntryKind.ExternalClipboard),
            _ => _items
        };
        ClipboardItemsList.ItemsSource = items.Take(18).ToArray();
    }

    private void ClipboardItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScreenshotHistoryItem item })
        {
            ItemSelected?.Invoke(this, item);
        }
    }
}
