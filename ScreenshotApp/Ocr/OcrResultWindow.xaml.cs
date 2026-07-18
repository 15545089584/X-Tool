using System.Windows;
using System.Windows.Input;

namespace ScreenshotApp.Ocr;

public partial class OcrResultWindow : Window
{
    internal OcrResultWindow(OcrResult result)
    {
        InitializeComponent();
        ResultTextBox.Text = result.Text;
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
