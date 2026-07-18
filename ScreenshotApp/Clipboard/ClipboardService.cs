using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.ClipboardUi;

/// <summary>统一写入本应用产生的剪贴板内容，并附加来源标记避免重复归入外部复制。</summary>
internal static class ClipboardService
{
    internal const string InternalFormat = "X-Tool.InternalClipboard";

    internal static void SetText(string content)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, content);
        data.SetData(InternalFormat, true);
        System.Windows.Clipboard.SetDataObject(data, true);
    }

    internal static void SetImage(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);
        data.SetData(InternalFormat, true);
        System.Windows.Clipboard.SetDataObject(data, true);
    }
}
