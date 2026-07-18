using System.Windows;
using System.Windows.Media.Imaging;
using System.IO;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.ClipboardUi;

/// <summary>统一写入本应用产生的剪贴板内容，并附加来源标记避免重复归入外部复制。</summary>
internal static class ClipboardService
{
    internal const string InternalFormat = "X-Tool.InternalClipboard";

    internal static void SetText(string content)
    {
        var data = new Forms.DataObject();
        data.SetText(content, Forms.TextDataFormat.UnicodeText);
        data.SetData(Forms.DataFormats.Text, true, content);
        data.SetData(InternalFormat, true);
        SetDataObjectWithShortRetry(data);
    }

    internal static void SetImage(BitmapSource image)
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
    }

    private static void SetDataObjectWithShortRetry(Forms.DataObject data)
    {
        // WinForms 原生提供受控重试，仍写入同一个 Windows 系统剪贴板。
        Forms.Clipboard.SetDataObject(data, true, 3, 25);
    }
}
