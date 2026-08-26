using System.Windows;
using System.Windows.Media.Imaging;
using System.IO;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.ClipboardUi;

/// <summary>统一写入本应用产生的剪贴板内容，并附加来源标记避免重复归入外部复制。</summary>
internal static class ClipboardService
{
    internal const string InternalFormat = "X-Tool.InternalClipboard";
    internal const string SensitiveFormat = "X-Tool.SensitiveClipboard";
    private const string ExcludeFromHistoryAndCloudFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private static int _sensitiveClipboardGeneration;

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

    /// <summary>复制密码或密钥时排除 X-Tool 历史、Windows 剪贴板历史与云同步，并按时清除。</summary>
    internal static void SetSensitiveText(string content, TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        var data = new Forms.DataObject();
        data.SetText(content, Forms.TextDataFormat.UnicodeText);
        data.SetData(Forms.DataFormats.Text, true, content);
        data.SetData(InternalFormat, true);
        data.SetData(SensitiveFormat, true);
        // Windows 识别这个已注册格式后，会同时排除本地历史和云剪贴板同步。
        data.SetData(ExcludeFromHistoryAndCloudFormat, true);
        SetDataObjectWithShortRetry(data);

        var generation = Interlocked.Increment(ref _sensitiveClipboardGeneration);
        _ = ClearSensitiveTextAfterAsync(content, lifetime ?? TimeSpan.FromSeconds(30), generation);
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

    private static async Task ClearSensitiveTextAfterAsync(string expected, TimeSpan delay, int generation)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        if (generation != Volatile.Read(ref _sensitiveClipboardGeneration))
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (Forms.Clipboard.ContainsData(SensitiveFormat) &&
                    string.Equals(Forms.Clipboard.GetText(Forms.TextDataFormat.UnicodeText), expected, StringComparison.Ordinal))
                {
                    Forms.Clipboard.Clear();
                }
            }
            catch
            {
                // 剪贴板被其他程序短暂占用时不重试，避免覆盖用户后来复制的内容。
            }
        });
    }
}
