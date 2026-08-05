using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotApp.ClipboardUi;
using ZXing.QrCode.Internal;

namespace ScreenshotApp.Converters;

/// <summary>历史条目的二维码预览弹窗：优先显示当初保存的图片，否则按内容重新生成。</summary>
public partial class QrHistoryPreviewWindow : Window
{
    private readonly QrHistoryEntry _entry;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;

    public QrHistoryPreviewWindow(QrHistoryEntry entry)
    {
        InitializeComponent();
        _entry = entry;

        TitleText.Text = entry.Kind == "Generated" ? "生成的二维码" : "识别到的二维码";
        SetBadge(BadgeHost, BadgeText, entry.Category);
        TimeText.Text = entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");
        KindText.Text = entry.Kind == "Generated" ? "生成" : "识别";
        ContentText.Text = entry.Content;

        BitmapSource? source = null;
        if (!string.IsNullOrWhiteSpace(entry.FilePath) && File.Exists(entry.FilePath))
        {
            try
            {
                source = QrCodeService.DecodeBitmapFile(entry.FilePath);
            }
            catch
            {
                source = null;
            }
        }
        source ??= QrCodeService.Generate(entry.Content, 384, 2, ErrorCorrectionLevel.M, Colors.Black, Colors.White);
        QrImage.Source = source;
        _pixelWidth = source.PixelWidth;
        _pixelHeight = source.PixelHeight;
        InfoText.Text = $"{_pixelWidth} × {_pixelHeight} 像素 · 内容 {QrCodeService.FriendlySize(entry.Content)}";
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
            "批量" => (Color.FromRgb(238, 240, 243), Color.FromRgb(107, 118, 131)),
            _ => (Color.FromRgb(232, 241, 250), Color.FromRgb(74, 107, 140))
        };
        host.Background = new SolidColorBrush(bg);
        text.Text = category;
        text.Foreground = new SolidColorBrush(fg);
    }

    private void CopyContent_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ClipboardService.SetText(_entry.Content);
            CopyContentButton_Status();
        }
        catch
        {
            InfoText.Text = "复制失败，请重试";
        }
    }

    private void CopyContentButton_Status()
    {
        InfoText.Text = $"{_pixelWidth} × {_pixelHeight} 像素 · 内容已复制";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
