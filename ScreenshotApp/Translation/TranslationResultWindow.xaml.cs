using System.Windows;
using ScreenshotApp.ClipboardUi;
using System.Windows.Input;

namespace ScreenshotApp.Translation;

public partial class TranslationResultWindow : Window
{
    private const double PlacementGap = 16;
    private readonly Rect? _selectionScreenBounds;
    private readonly Func<string, Task<TranslationResult>>? _retranslateAsync;

    internal TranslationResultWindow(
        string sourceText,
        string? translatedText,
        string? unavailableReason,
        Func<string, Task<TranslationResult>>? retranslateAsync = null,
        Rect? selectionScreenBounds = null)
    {
        _selectionScreenBounds = selectionScreenBounds;
        _retranslateAsync = retranslateAsync;
        InitializeComponent();
        SourceTextBox.Text = sourceText;
        TranslationTextBox.Text = translatedText ?? unavailableReason ?? "未生成译文";
        var translated = !string.IsNullOrWhiteSpace(translatedText);
        TranslationTextBox.IsReadOnly = !translated;
        CopyButton.IsEnabled = translated;
        RetranslateButton.Visibility = _retranslateAsync is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusText.Text = translated
            ? $"原文可编辑；修改后可重新翻译"
            : "请安装离线模型包后再试";
        PositionNearSelection();
        Loaded += (_, _) => PositionNearSelection();
    }

    /// <summary>复制成功后为 true，截图窗口据此自动退出截图模式。</summary>
    internal bool ExitCaptureRequested { get; private set; }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TranslationTextBox.Text))
        {
            return;
        }

        try
        {
            ClipboardService.SetText(TranslationTextBox.Text);
            ExitCaptureRequested = true;
            Close();
        }
        catch
        {
            StatusText.Text = "剪贴板忙，请再次单击";
        }
    }

    private async void RetranslateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_retranslateAsync is null || string.IsNullOrWhiteSpace(SourceTextBox.Text))
        {
            return;
        }

        RetranslateButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        StatusText.Text = "正在重新翻译…";
        try
        {
            var result = await _retranslateAsync(SourceTextBox.Text);
            SourceTextBox.Text = result.SourceText;
            TranslationTextBox.Text = result.TranslatedText;
            TranslationTextBox.IsReadOnly = false;
            CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(result.TranslatedText);
            StatusText.Text = $"共 {result.TranslatedText.Count(character => !char.IsWhiteSpace(character)):N0} 个中文字符";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"重新翻译失败：{exception.Message}";
        }
        finally
        {
            RetranslateButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void PositionNearSelection()
    {
        if (_selectionScreenBounds is not Rect selection || selection.IsEmpty)
        {
            return;
        }

        var size = new Size(
            ActualWidth > 0 ? ActualWidth : Width,
            ActualHeight > 0 ? ActualHeight : Height);
        var workArea = SystemParameters.WorkArea;
        var candidates = new[]
        {
            new Rect(selection.Right + PlacementGap, selection.Top, size.Width, size.Height),
            new Rect(selection.Left - size.Width - PlacementGap, selection.Top, size.Width, size.Height),
            new Rect(selection.Left, selection.Bottom + PlacementGap, size.Width, size.Height),
            new Rect(selection.Left, selection.Top - size.Height - PlacementGap, size.Width, size.Height)
        };
        var bounds = candidates
            .Select(candidate => ClampToWorkArea(candidate, workArea))
            .OrderBy(candidate => GetIntersectionArea(candidate, selection))
            .First();
        Left = bounds.Left;
        Top = bounds.Top;
    }

    private static Rect ClampToWorkArea(Rect bounds, Rect workArea)
    {
        var maxLeft = Math.Max(workArea.Left, workArea.Right - bounds.Width);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - bounds.Height);
        return new Rect(
            Math.Clamp(bounds.Left, workArea.Left, maxLeft),
            Math.Clamp(bounds.Top, workArea.Top, maxTop),
            bounds.Width,
            bounds.Height);
    }

    private static double GetIntersectionArea(Rect first, Rect second)
    {
        var intersection = Rect.Intersect(first, second);
        return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
    }
}
