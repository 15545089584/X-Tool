using System.Windows;
using System.Windows.Input;

namespace ScreenshotApp.Ocr;

public partial class OcrResultWindow : Window
{
    internal OcrResultWindow(OcrResult result)
    {
        InitializeComponent();
        ResultTextBox.Text = result.Text;
        SummaryText.Text = result.Blocks.Count == 0
            ? $"没有识别到文字 · 用时 {result.Elapsed.TotalMilliseconds:F0} ms"
            : $"识别到 {result.Blocks.Count} 个文本区域 · 平均置信度 {result.AverageConfidence:P0} · {result.Elapsed.TotalMilliseconds:F0} ms";
        Loaded += (_, _) =>
        {
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
}
