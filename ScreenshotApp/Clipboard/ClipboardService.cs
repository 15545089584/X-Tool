using System.Windows;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenshotApp.Capture;

namespace ScreenshotApp.ClipboardUi;

/// <summary>统一写入本应用产生的剪贴板内容，并附加来源标记避免重复归入外部复制。</summary>
internal static class ClipboardService
{
    internal const string InternalFormat = "X-Tool.InternalClipboard";

    internal static void SetText(string content)
    {
        var data = new DataObject();
        data.SetText(content, TextDataFormat.UnicodeText);
        data.SetData(DataFormats.Text, content);
        data.SetData(DataFormats.StringFormat, content);
        data.SetData(InternalFormat, true);
        SetDataObjectWithRetry(data);
    }

    internal static void SetImage(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);
        data.SetData(InternalFormat, true);
        SetDataObjectWithRetry(data);
    }

    private static void SetDataObjectWithRetry(DataObject data)
    {
        // 浏览器、输入法等程序会在短时间内占用系统剪贴板；短暂重试避免瞬时占用直接失败。
        string? capturedOwner = null;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(data, true);
                return;
            }
            catch (COMException exception)
            {
                var currentOwner = NativeMethods.GetOpenClipboardOwnerDescription();
                if (currentOwner != "占用窗口已释放，无法识别")
                {
                    capturedOwner = currentOwner;
                }

                if (attempt == 15)
                {
                    throw new ClipboardLockedException(exception, capturedOwner ?? currentOwner);
                }

                Thread.Sleep(45);
            }
        }
    }
}

/// <summary>用于保留剪贴板被锁定瞬间的占用窗口信息。</summary>
internal sealed class ClipboardLockedException : Exception
{
    internal ClipboardLockedException(COMException innerException, string ownerDescription)
        : base(innerException.Message, innerException)
    {
        OwnerDescription = ownerDescription;
    }

    internal string OwnerDescription { get; }
}
