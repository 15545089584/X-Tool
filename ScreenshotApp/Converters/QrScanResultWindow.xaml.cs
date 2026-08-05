using System.Windows;
using System.Windows.Media;
using ScreenshotApp.ClipboardUi;

namespace ScreenshotApp.Converters;

/// <summary>截图选区的二维码识别结果窗口：单个结果以完整大卡片展示，多结果时纵向排列。</summary>
public partial class QrScanResultWindow : Window
{
    private const double PlacementGap = 14;
    private readonly Rect? _selectionScreenBounds;

    public QrScanResultWindow(IReadOnlyList<string> results, Rect? selectionScreenBounds = null)
    {
        InitializeComponent();
        _selectionScreenBounds = selectionScreenBounds;

        ResultList.ItemsSource = results
            .Select(content => QrScanItem.Create(content))
            .ToList();
        EmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = results.Count > 0
            ? $"识别到 {results.Count} 个二维码，结果仅在本地处理"
            : "可重新框选包含二维码的区域后再试";
        SourceInitialized += (_, _) => PositionNearSelection();
        PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == System.Windows.Input.Key.Escape)
            {
                Close();
            }
        };
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QrScanItem item })
        {
            try
            {
                ClipboardService.SetText(item.Content);
                SummaryText.Text = "内容已复制到剪贴板";
            }
            catch
            {
                SummaryText.Text = "复制失败，请重试";
            }
        }
    }

    private void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QrScanItem item })
        {
            if (MessageBox.Show(this,
                    "二维码内容指向外部链接：" + item.Content + Environment.NewLine + Environment.NewLine + "确定在默认浏览器打开？",
                    "打开链接",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            try
            {
                QrCodeService.OpenUrl(item.Content);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开失败：" + ex.Message, "打开链接", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void PositionNearSelection()
    {
        if (_selectionScreenBounds is not Rect selection || selection.IsEmpty)
        {
            return;
        }

        var windowSize = new Size(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        var workArea = SystemParameters.WorkArea;
        var candidates = new[]
        {
            new Rect(selection.Right + PlacementGap, selection.Top, windowSize.Width, windowSize.Height),
            new Rect(selection.Left - windowSize.Width - PlacementGap, selection.Top, windowSize.Width, windowSize.Height),
            new Rect(selection.Left, selection.Bottom + PlacementGap, windowSize.Width, windowSize.Height),
            new Rect(selection.Left, selection.Top - windowSize.Height - PlacementGap, windowSize.Width, windowSize.Height)
        };

        var positioned = candidates
            .Select(ClampToWorkArea)
            .FirstOrDefault(candidate => !candidate.IntersectsWith(selection));
        if (positioned.IsEmpty)
        {
            positioned = new Rect(workArea.Right - windowSize.Width, workArea.Bottom - windowSize.Height, windowSize.Width, windowSize.Height);
        }
        Left = positioned.Left;
        Top = positioned.Top;
    }

    private Rect ClampToWorkArea(Rect rect)
    {
        var workArea = SystemParameters.WorkArea;
        return new Rect(
            Math.Clamp(rect.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - rect.Width)),
            Math.Clamp(rect.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - rect.Height)),
            rect.Width,
            rect.Height);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>识别结果展示模型：预计算分类徽章配色与按钮可见性，模板直接绑定，不依赖延迟填充。</summary>
internal sealed record QrScanItem(
    string Content,
    string CategoryText,
    string BadgeBackground,
    string BadgeForeground,
    Visibility OpenVisibility)
{
    public static QrScanItem Create(string content)
    {
        var category = QrCodeService.Classify(content);
        var (bg, fg) = BadgeColors(QrCodeService.CategoryText(category));
        return new QrScanItem(
            content,
            QrCodeService.CategoryText(category),
            ToHex(bg),
            ToHex(fg),
            category == QrContentCategory.Url ? Visibility.Visible : Visibility.Collapsed);
    }

    private static (Color Background, Color Foreground) BadgeColors(string category) => category switch
    {
        "网址" => (Color.FromRgb(220, 235, 255), Color.FromRgb(47, 107, 196)),
        "邮件" => (Color.FromRgb(237, 231, 255), Color.FromRgb(106, 84, 200)),
        "电话/短信" => (Color.FromRgb(223, 245, 242), Color.FromRgb(46, 138, 126)),
        "WiFi" => (Color.FromRgb(228, 246, 232), Color.FromRgb(46, 125, 79)),
        "名片" => (Color.FromRgb(255, 240, 228), Color.FromRgb(192, 106, 45)),
        "日历事件" => (Color.FromRgb(253, 234, 241), Color.FromRgb(183, 78, 124)),
        _ => (Color.FromRgb(232, 241, 250), Color.FromRgb(74, 107, 140))
    };

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
