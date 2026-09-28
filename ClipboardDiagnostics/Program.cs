using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace XToolClipboardDiagnostics;

/// <summary>不改写剪贴板内容，仅记录无法打开剪贴板时的持锁窗口。</summary>
internal static class Program
{
    private const int DefaultIntervalMilliseconds = 5;
    private static volatile bool _isRunning = true;

    private static int Main(string[] args)
    {
        var intervalMilliseconds = ParseInterval(args);
        var startedAt = DateTime.Now;
        var logPath = Path.Combine(
            AppContext.BaseDirectory,
            $"ClipboardLockDiagnostic_{startedAt:yyyyMMdd_HHmmss}.log");

        using var logWriter = new StreamWriter(logPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        {
            AutoFlush = true
        };

        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            _isRunning = false;
        };

        WriteLine(logWriter, $"X-Tool 剪贴板锁诊断器已启动，采样间隔 {intervalMilliseconds} ms。");
        WriteLine(logWriter, "本程序只被动读取 GetOpenClipboardWindow，不会打开、清空、写入或读取剪贴板内容。");
        WriteLine(logWriter, "请保持本窗口运行，在 X-Tool 中复现一次粘贴失败；完成后按 Ctrl+C 退出。\n");

        ClipboardLockSample? activeLock = null;
        while (_isRunning)
        {
            var ownerWindow = GetOpenClipboardWindow();
            if (ownerWindow == IntPtr.Zero)
            {
                if (activeLock is not null)
                {
                    var duration = DateTime.Now - activeLock.DetectedAt;
                    WriteLine(logWriter, $"释放 {DateTime.Now:HH:mm:ss.fff} · 持锁约 {duration.TotalMilliseconds:F0} ms · {activeLock.Description}");
                    activeLock = null;
                }
            }
            else
            {
                var sample = ReadLockSample(ownerWindow);
                if (activeLock is null || !string.Equals(activeLock.Identity, sample.Identity, StringComparison.Ordinal))
                {
                    WriteLine(logWriter, $"占用 {sample.DetectedAt:HH:mm:ss.fff} · {sample.Description}");
                    activeLock = sample;
                }
            }

            Thread.Sleep(intervalMilliseconds);
        }

        WriteLine(logWriter, $"\n诊断结束，日志位置：{logPath}");
        return 0;
    }

    private static int ParseInterval(string[] args)
    {
        var intervalIndex = Array.FindIndex(args, argument => string.Equals(argument, "--interval", StringComparison.OrdinalIgnoreCase));
        if (intervalIndex >= 0 && intervalIndex + 1 < args.Length && int.TryParse(args[intervalIndex + 1], out var interval))
        {
            return Math.Clamp(interval, 5, 1000);
        }

        return DefaultIntervalMilliseconds;
    }

    private static ClipboardLockSample ReadLockSample(IntPtr ownerWindow)
    {
        _ = GetWindowThreadProcessId(ownerWindow, out var processId);
        var processName = processId == 0 ? "未知进程" : $"PID {processId}";
        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            processName = process.ProcessName;
        }
        catch
        {
            // 无权限读取系统进程名称时保留 PID。
        }

        var titleLength = GetWindowTextLength(ownerWindow);
        var titleBuffer = new StringBuilder(Math.Max(1, titleLength + 1));
        _ = GetWindowText(ownerWindow, titleBuffer, titleBuffer.Capacity);
        var title = titleBuffer.ToString().Trim();
        var description = string.IsNullOrWhiteSpace(title)
            ? $"{processName} · PID {processId} · HWND 0x{ownerWindow.ToInt64():X}"
            : $"{processName} · PID {processId} · 窗口「{title}」· HWND 0x{ownerWindow.ToInt64():X}";
        return new ClipboardLockSample($"{processId}:{ownerWindow}", description, DateTime.Now);
    }

    private static void WriteLine(StreamWriter logWriter, string message)
    {
        Console.WriteLine(message);
        logWriter.WriteLine(message);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxCount);

    private sealed record ClipboardLockSample(string Identity, string Description, DateTime DetectedAt);
}
