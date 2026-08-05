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
        using var pngStream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(pngStream);
        pngStream.Position = 0;

        using var bitmap = new System.Drawing.Bitmap(pngStream);
        var data = new Forms.DataObject();
        data.SetImage(new System.Drawing.Bitmap(bitmap));
        data.SetData(InternalFormat, true);
        SetDataObjectWithShortRetry(data);
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
}
