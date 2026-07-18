using System.Windows;
using System.Windows.Input;

namespace ScreenshotApp.Ocr;

public partial class OcrResultWindow : Window
{
    private const double PlacementGap = 16;
    private readonly Rect? _selectionScreenBounds;

    internal OcrResultWindow(OcrResult result, Rect? selectionScreenBounds = null)
    {
        _selectionScreenBounds = selectionScreenBounds;
        InitializeComponent();
        ResultTextBox.Text = result.Text;
        var characterCount = result.Text.Count(character => !char.IsWhiteSpace(character));
        CopyStatusText.Text = characterCount == 0
            ? "未识别到可复制的文字"
            : $"共识别 {characterCount:N0} 个字符 · 可编辑后复制";
        PositionNearSelection();
        Loaded += (_, _) =>
        {
            PositionNearSelection();
            ResultTextBox.Focus();
            ResultTextBox.SelectAll();
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CopyAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultTextBox.Text))
        {
            CopyStatusText.Text = "当前没有可复制的文字";
            return;
        }

        try
        {
            Clipboard.SetText(ResultTextBox.Text);
            CopyStatusText.Text = "文字已复制到剪贴板";
        }
        catch
        {
            CopyStatusText.Text = "剪贴板忙，请再次单击";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void PositionNearSelection()
    {
        if (_selectionScreenBounds is not Rect selection || selection.IsEmpty)
        {
            return;
        }

        var windowSize = new Size(
            ActualWidth > 0 ? ActualWidth : Width,
            ActualHeight > 0 ? ActualHeight : Height);
        var workArea = SystemParameters.WorkArea;
        var candidates = new[]
        {
            new Rect(selection.Right + PlacementGap, selection.Top, windowSize.Width, windowSize.Height),
            new Rect(selection.Left - windowSize.Width - PlacementGap, selection.Top, windowSize.Width, windowSize.Height),
            new Rect(selection.Left, selection.Bottom + PlacementGap, windowSize.Width, windowSize.Height),
            new Rect(selection.Left, selection.Top - windowSize.Height - PlacementGap, windowSize.Width, windowSize.Height)
        };

        var positioned = candidates
            .Select(candidate => ClampToWorkArea(candidate, workArea))
            .FirstOrDefault(candidate => !candidate.IntersectsWith(selection));

        if (positioned.IsEmpty)
        {
            positioned = candidates
                .Select(candidate => ClampToWorkArea(candidate, workArea))
                .Append(new Rect(workArea.Left, workArea.Top, windowSize.Width, windowSize.Height))
                .Append(new Rect(workArea.Right - windowSize.Width, workArea.Top, windowSize.Width, windowSize.Height))
                .Append(new Rect(workArea.Left, workArea.Bottom - windowSize.Height, windowSize.Width, windowSize.Height))
                .Append(new Rect(workArea.Right - windowSize.Width, workArea.Bottom - windowSize.Height, windowSize.Width, windowSize.Height))
                .Select(candidate => ClampToWorkArea(candidate, workArea))
                .OrderBy(candidate => GetIntersectionArea(candidate, selection))
                .First();
        }

        Left = positioned.Left;
        Top = positioned.Top;
    }

    private static Rect ClampToWorkArea(Rect bounds, Rect workArea)
    {
        var maxLeft = Math.Max(workArea.Left, workArea.Right - bounds.Width);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - bounds.Height);
        var left = Math.Clamp(bounds.Left, workArea.Left, maxLeft);
        var top = Math.Clamp(bounds.Top, workArea.Top, maxTop);
        return new Rect(left, top, bounds.Width, bounds.Height);
    }

    private static double GetIntersectionArea(Rect first, Rect second)
    {
        var intersection = Rect.Intersect(first, second);
        return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
    }
}
