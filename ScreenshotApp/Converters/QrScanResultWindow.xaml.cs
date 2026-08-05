using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScreenshotApp.ClipboardUi;

namespace ScreenshotApp.Converters;

/// <summary>截图选区的二维码识别结果侧栏窗口，显示识别内容并提供复制与打开链接操作。</summary>
public partial class QrScanResultWindow : Window
{
    private const double PlacementGap = 14;
    private readonly IReadOnlyList<string> _results;
    private readonly Rect? _selectionScreenBounds;

    public QrScanResultWindow(IReadOnlyList<string> results, Rect? selectionScreenBounds = null)
    {
        InitializeComponent();
        _results = results;
        _selectionScreenBounds = selectionScreenBounds;

        foreach (var content in results)
        {
            ResultList.Items.Add(content);
        }
        EmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = results.Count > 0
            ? $"识别到 {results.Count} 个二维码，结果仅在本地处理"
            : "可重新框选包含二维码的区域后再试";
        RefreshRows();
        SourceInitialized += (_, _) => PositionNearSelection();
        PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == System.Windows.Input.Key.Escape)
            {
                Close();
            }
        };
    }

    private void RefreshRows()
    {
        for (var i = 0; i < ResultList.Items.Count; i++)
        {
            if (ResultList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter presenter ||
                ResultList.Items[i] is not string content)
            {
                continue;
            }
            try
            {
                var badgeHost = presenter.ContentTemplate.FindName("BadgeHost", presenter) as Border;
                var badgeText = presenter.ContentTemplate.FindName("BadgeText", presenter) as TextBlock;
                var contentText = presenter.ContentTemplate.FindName("ContentText", presenter) as TextBlock;
                var openButton = presenter.ContentTemplate.FindName("OpenButton", presenter) as Button;
                var category = QrCodeService.Classify(content);
                if (badgeHost != null && badgeText != null)
                {
                    SetBadge(badgeHost, badgeText, QrCodeService.CategoryText(category));
                }
                if (contentText != null)
                {
                    contentText.Text = content;
                    contentText.ToolTip = content;
                }
                if (openButton != null)
                {
                    openButton.Visibility = category == QrContentCategory.Url ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (InvalidOperationException)
            {
                // 容器尚未应用模板时跳过。
            }
        }
    }

    private static void SetBadge(Border host, TextBlock text, string category)
    {
        var (bg, fg) = category switch
        {
            "网址" => (Color.FromRgb(220, 235, 255), Color.FromRgb(47, 107, 196)),
            "邮件" => (Color.FromRgb(237, 231, 255), Color.FromRgb(106, 84, 200)),
            "电话/短信" => (Color.FromRgb(223, 245, 242), Color.FromRgb(46, 138, 126)),
            "WiFi" => (Color.FromRgb(228, 246, 232), Color.FromRgb(46, 125, 79)),
            "名片" => (Color.FromRgb(255, 240, 228), Color.FromRgb(192, 106, 45)),
            "日历事件" => (Color.FromRgb(253, 234, 241), Color.FromRgb(183, 78, 124)),
            _ => (Color.FromRgb(232, 241, 250), Color.FromRgb(74, 107, 140))
        };
        host.Background = new SolidColorBrush(bg);
        text.Text = category;
        text.Foreground = new SolidColorBrush(fg);
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string content })
        {
            try
            {
                ClipboardService.SetText(content);
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
        if (sender is FrameworkElement { Tag: string content })
        {
            if (MessageBox.Show(this,
                    "二维码内容指向外部链接：" + content + Environment.NewLine + Environment.NewLine + "确定在默认浏览器打开？",
                    "打开链接",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            try
            {
                QrCodeService.OpenUrl(content);
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
