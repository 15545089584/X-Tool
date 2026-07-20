using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ScreenshotApp.SystemTools;

/// <summary>系统工具的数据读取与受控操作，默认只读，写操作由页面二次确认后调用。</summary>
public static class SystemToolsService
{
    private static readonly Regex NetstatLine = new(@"^\s*(TCP|UDP)\s+(?<local>\S+)\s+(?<remote>\S+)(?:\s+(?<state>\S+))?\s+(?<pid>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ServiceNameLine = new(@"^SERVICE_NAME:\s*(?<name>.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex DisplayNameLine = new(@"^DISPLAY_NAME:\s*(?<name>.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ServiceStateLine = new(@"^\s*STATE\s*:\s*\d+\s+(?<state>.+)$", RegexOptions.Compiled | RegexOptions.Multiline);

    public static IReadOnlyList<PortEntry> GetPorts()
    {
        var entries = new List<PortEntry>();
        foreach (var line in RunCommand("netstat.exe", "-ano").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = NetstatLine.Match(line);
            if (!match.Success || !int.TryParse(match.Groups["pid"].Value, out var pid) || pid == 0)
            {
                continue;
            }

            var protocol = match.Groups[1].Value.ToUpperInvariant();
            var state = match.Groups["state"].Success ? match.Groups["state"].Value : "监听";
            var (name, path) = GetProcessDetails(pid);
            entries.Add(new PortEntry(protocol, match.Groups["local"].Value, match.Groups["remote"].Value, state, pid, name, path));
        }

        return entries
            .OrderBy(item => ExtractPort(item.LocalAddress))
            .ThenBy(item => item.Protocol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<ProcessEntry> GetProcesses()
    {
        var firstSample = new Dictionary<int, TimeSpan>();
        foreach (var process in Process.GetProcesses())
        {
            try { firstSample[process.Id] = process.TotalProcessorTime; }
            catch { }
            finally { process.Dispose(); }
        }

        Thread.Sleep(220);
        var elapsedMilliseconds = 220d * Math.Max(1, Environment.ProcessorCount);
        var entries = new List<ProcessEntry>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var totalProcessorTime = process.TotalProcessorTime;
                var cpu = firstSample.TryGetValue(process.Id, out var initial)
                    ? Math.Max(0, (totalProcessorTime - initial).TotalMilliseconds / elapsedMilliseconds * 100d)
                    : 0;
                string path = string.Empty;
                DateTime? startedAt = null;
                try { path = process.MainModule?.FileName ?? string.Empty; } catch { }
                try { startedAt = process.StartTime; } catch { }
                var memoryBytes = process.WorkingSet64;
                entries.Add(new ProcessEntry(process.ProcessName, process.Id, cpu, memoryBytes, path, startedAt));
            }
            catch { }
            finally { process.Dispose(); }
        }

        return entries.OrderByDescending(item => item.MemoryBytes).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<ServiceEntry> GetServices()
    {
        var blocks = Regex.Split(RunCommand("sc.exe", "query type= service state= all").Replace("\r\n", "\n"), @"\n\s*\n");
        var entries = new List<ServiceEntry>();
        foreach (var block in blocks)
        {
            var name = ServiceNameLine.Match(block).Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var displayName = DisplayNameLine.Match(block).Groups["name"].Value.Trim();
            var state = ServiceStateLine.Match(block).Groups["state"].Value.Trim();
            entries.Add(new ServiceEntry(name, string.IsNullOrWhiteSpace(displayName) ? name : displayName, NormalizeServiceState(state)));
        }

        return entries.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<EnvironmentVariableEntry> GetEnvironmentVariables(EnvironmentVariableTarget target)
    {
        var variables = Environment.GetEnvironmentVariables(target);
        return variables.Keys.Cast<object>()
            .Select(key => new EnvironmentVariableEntry(key.ToString() ?? string.Empty, variables[key]?.ToString() ?? string.Empty, target))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool TryEndProcess(int processId, out string? error)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static bool TryOpenProcessDirectory(string path, out string? error)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "未能读取该进程的可执行文件路径";
                return false;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static bool TryControlService(string serviceName, bool start, out string? error)
    {
        var output = RunCommand("sc.exe", $"{(start ? "start" : "stop")} \"{serviceName}\"");
        if (output.Contains("FAILED", StringComparison.OrdinalIgnoreCase) || output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) || output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            error = output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) || output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                ? "权限不足，请以管理员身份启动 X-Tool 后重试"
                : output.Trim();
            return false;
        }

        error = null;
        return true;
    }

    public static bool TrySaveEnvironmentVariable(string name, string value, EnvironmentVariableTarget target, out string? error)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '=', '\0' }) >= 0)
            {
                error = "变量名不能为空，且不能包含 =";
                return false;
            }

            Environment.SetEnvironmentVariable(name.Trim(), string.IsNullOrWhiteSpace(value) ? null : value, target);
            BroadcastEnvironmentChanged();
            error = null;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "权限不足：系统环境变量需要管理员权限";
            return false;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string RunCommand(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process is null) return string.Empty;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(8000);
        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    private static (string Name, string Path) GetProcessDetails(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            string path = string.Empty;
            try { path = process.MainModule?.FileName ?? string.Empty; } catch { }
            return (process.ProcessName, path);
        }
        catch
        {
            return ("系统或已退出的进程", string.Empty);
        }
    }

    private static int ExtractPort(string address)
    {
        var separator = address.LastIndexOf(':');
        return separator >= 0 && int.TryParse(address[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : int.MaxValue;
    }

    private static string NormalizeServiceState(string state) => state.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) || state.Contains("运行", StringComparison.OrdinalIgnoreCase) ? "运行中" : "已停止";

    private static void BroadcastEnvironmentChanged() => SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 3000, out _);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}

public sealed record PortEntry(string Protocol, string LocalAddress, string RemoteAddress, string State, int ProcessId, string ProcessName, string ProcessPath);

public sealed record ProcessEntry(string Name, int ProcessId, double CpuPercent, long MemoryBytes, string Path, DateTime? StartedAt)
{
    public string CpuText => $"{CpuPercent:F1}%";
    public string MemoryText => $"{MemoryBytes / 1024d / 1024d:F1} MB";
    public string StartedAtText => StartedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—";
}

public sealed record ServiceEntry(string Name, string DisplayName, string Status);

public sealed record EnvironmentVariableEntry(string Name, string Value, EnvironmentVariableTarget Target);
