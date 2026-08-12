using System.ComponentModel;
using System.Diagnostics;

namespace ScreenshotApp.NetworkWorkbench;

/// <summary>
/// 管理按用户明确授权创建的 ETW 辅助计划任务。主程序本身仍保持普通权限。
/// </summary>
internal static class NetworkEtwAutoStartService
{
    private const string TaskName = "XTool.NetworkEtwAgent";
    public const string PersistentPipeName = "XTool.NetworkEtw.Persistent.v1";

    public static bool IsRegistered()
    {
        try
        {
            using var process = StartSchtasks($"/Query /TN \"{TaskName}\"");
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    public static async Task<(bool Success, string Message)> RegisterAsync()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return (false, "无法定位 X-Tool 可执行文件。");
        var taskCommand = $"\"{executable}\" --network-etw-helper {PersistentPipeName}";
        var arguments = $"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /F /TR \"{taskCommand.Replace("\"", "\\\"")}\"";
        return await RunElevatedSchtasksAsync(arguments, "已授权网络流量自动获取；以后启动 X-Tool 时会自动连接独立 ETW 辅助进程。");
    }

    public static async Task<(bool Success, string Message)> UnregisterAsync()
    {
        // 主程序会先关闭已连接的命名管道；删除任务只需一次 UAC。
        return await RunElevatedSchtasksAsync($"/Delete /TN \"{TaskName}\" /F", "已取消网络流量自动获取授权。");
    }

    public static async Task<(bool Success, string Message)> RunTaskAsync()
    {
        if (!IsRegistered()) return (false, "尚未注册网络流量自动获取任务。");
        if (IsRunning()) return (true, "独立 ETW 辅助进程已在等待连接。");
        var result = await RunSchtasksAsync($"/Run /TN \"{TaskName}\"");
        return result.Success
            ? (true, "已启动独立 ETW 辅助进程。")
            : (false, $"启动 ETW 辅助任务失败：{result.Message}");
    }

    private static async Task<(bool Success, string Message)> RunElevatedSchtasksAsync(string arguments, string successMessage)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null) return (false, "未能启动 Windows 任务计划程序。");
            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? (true, successMessage)
                : (false, $"Windows 任务计划程序返回错误码 {process.ExitCode}。");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return (false, "用户取消管理员授权。");
        }
        catch (Exception exception)
        {
            return (false, $"任务计划操作失败：{exception.Message}");
        }
    }

    private static async Task<(bool Success, string Message)> RunSchtasksAsync(string arguments)
    {
        try
        {
            using var process = StartSchtasks(arguments);
            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? (true, string.Empty)
                : (false, $"退出码 {process.ExitCode}");
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    private static bool IsRunning()
    {
        try
        {
            using var process = StartSchtasks($"/Query /TN \"{TaskName}\" /FO LIST");
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return process.ExitCode == 0 &&
                (output.Contains("Running", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("正在运行", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("运行中", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static Process StartSchtasks(string arguments)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("无法启动 schtasks.exe。");
    }
}
