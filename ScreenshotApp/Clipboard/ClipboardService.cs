using System.Windows;
using System.Windows.Media.Imaging;
using System.IO;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.ClipboardUi;

/// <summary>统一写入本应用产生的剪贴板内容，并附加来源标记避免重复归入外部复制。</summary>
internal static class ClipboardService
{
    internal const string InternalFormat = "X-Tool.InternalClipboard";

    /// <summary>用户主动“复制内容”时触发，由主窗口写入剪贴板历史，浮窗立即可见。</summary>
    internal static event Action<string>? TextRecordRequested;

    internal static event Action<BitmapSource>? ImageRecordRequested;

    internal static void SetText(string content, bool recordToHistory = false)
    {
        var data = new Forms.DataObject();
        data.SetText(content, Forms.TextDataFormat.UnicodeText);
        data.SetData(Forms.DataFormats.Text, true, content);
        data.SetData(InternalFormat, true);
        SetDataObjectWithShortRetry(data);
        if (recordToHistory)
        {
            TextRecordRequested?.Invoke(content);
        }
    }

    internal static void SetImage(BitmapSource image, bool recordToHistory = false)
    {
        // WPF 原生图片数据对象可直接提供 BitmapSource，避免先编码 PNG、
        // 再解码成 GDI 位图的双重转换，长截图写入剪贴板会明显更快。
        var data = new System.Windows.DataObject();
        data.SetImage(image);
        data.SetData(InternalFormat, true);
        SetWpfDataObjectWithShortRetry(data);
        if (recordToHistory)
        {
            ImageRecordRequested?.Invoke(image);
        }
    }

    private static void SetDataObjectWithShortRetry(Forms.DataObject data)
    {
        // WinForms 原生提供受控重试，仍写入同一个 Windows 系统剪贴板。
        Forms.Clipboard.SetDataObject(data, true, 3, 25);
    }

    private static void SetWpfDataObjectWithShortRetry(System.Windows.DataObject data)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(data, true);
                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
                if (attempt < 3)
                {
                    Thread.Sleep(25);
                }
            }
        }

        throw new InvalidOperationException("剪贴板暂时被其他程序占用。", lastError);
    }
}
