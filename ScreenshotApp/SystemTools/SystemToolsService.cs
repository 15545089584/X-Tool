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
    private static readonly object ProcessSampleLock = new();
    private static Dictionary<int, ProcessSample> _previousProcessSamples = new();

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
        var samples = ReadProcessSamples();
        if (samples.Count == 0) return Array.Empty<ProcessEntry>();

        // 首次打开页面没有历史采样，短暂补采样一次，避免 CPU、磁盘列全部显示为 0。
        lock (ProcessSampleLock)
        {
            if (_previousProcessSamples.Count != 0) return CreateProcessEntries(samples);
            _previousProcessSamples = samples.ToDictionary(item => item.ProcessId);
        }

        Thread.Sleep(650);
        samples = ReadProcessSamples();
        return CreateProcessEntries(samples);
    }

    /// <summary>读取一次累计性能计数；速率由相邻两次采样的增量计算。</summary>
    private static IReadOnlyList<ProcessSample> ReadProcessSamples()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var samples = new List<ProcessSample>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                string path = string.Empty;
                DateTime? startedAt = null;
                try { path = process.MainModule?.FileName ?? string.Empty; } catch { }
                try { startedAt = process.StartTime; } catch { }
                var io = TryGetProcessIoCounters(process, out var counters) ? counters : default;
                samples.Add(new ProcessSample(process.ProcessName, process.Id, process.TotalProcessorTime, process.WorkingSet64, path, startedAt, io.ReadTransferCount, io.WriteTransferCount, io.OtherTransferCount, timestamp));
            }
            catch { }
            finally { process.Dispose(); }
        }

        return samples;
    }

    private static IReadOnlyList<ProcessEntry> CreateProcessEntries(IReadOnlyList<ProcessSample> samples)
    {
        var entries = new List<ProcessEntry>();
        lock (ProcessSampleLock)
        {
            foreach (var sample in samples)
            {
                _previousProcessSamples.TryGetValue(sample.ProcessId, out var previous);
                var elapsedSeconds = previous is null ? 0d : (sample.Timestamp - previous.Timestamp) / (double)Stopwatch.Frequency;
                var cpu = elapsedSeconds > 0 ? Math.Max(0, (sample.TotalProcessorTime - previous!.TotalProcessorTime).TotalSeconds / elapsedSeconds / Math.Max(1, Environment.ProcessorCount) * 100d) : 0;
                var disk = elapsedSeconds > 0 ? CalculateRate(sample.ReadTransferCount, previous!.ReadTransferCount, elapsedSeconds) + CalculateRate(sample.WriteTransferCount, previous.WriteTransferCount, elapsedSeconds) : 0;
                // Windows 进程级 API 未单独公开所有协议的网络计数；其它 I/O 字节是可用的近似值，并保留在界面提示中说明。
                var network = elapsedSeconds > 0 ? CalculateRate(sample.OtherTransferCount, previous!.OtherTransferCount, elapsedSeconds) * 8d : 0;
                entries.Add(new ProcessEntry(sample.Name, sample.ProcessId, cpu, sample.MemoryBytes, disk, network, sample.Path, sample.StartedAt));
            }

            _previousProcessSamples = samples.ToDictionary(item => item.ProcessId);
        }

        return entries.OrderByDescending(item => item.MemoryBytes).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static double CalculateRate(ulong current, ulong previous, double elapsedSeconds)
        => current >= previous && elapsedSeconds > 0 ? (current - previous) / elapsedSeconds : 0;

    private static bool TryGetProcessIoCounters(Process process, out IoCounters counters)
    {
        try { return GetProcessIoCounters(process.Handle, out counters); }
        catch { counters = default; return false; }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IoCounters lpIoCounters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary>进程一次性能采样的累计计数，用于下次刷新计算速率。</summary>
    private sealed record ProcessSample(string Name, int ProcessId, TimeSpan TotalProcessorTime, long MemoryBytes, string Path, DateTime? StartedAt, ulong ReadTransferCount, ulong WriteTransferCount, ulong OtherTransferCount, long Timestamp);
}

public sealed record PortEntry(string Protocol, string LocalAddress, string RemoteAddress, string State, int ProcessId, string ProcessName, string ProcessPath, bool IsPidHighlighted = false)
{
    /// <summary>从本地地址提取端口，IPv4、IPv6 与通配地址均适用。</summary>
    public int Port
    {
        get
        {
            var separator = LocalAddress.LastIndexOf(':');
            return separator >= 0 && int.TryParse(LocalAddress[(separator + 1)..], out var port) ? port : 0;
        }
    }

    /// <summary>标记 IPv6 地址，供端口列表的显示筛选使用。</summary>
    public bool IsIpv6 => LocalAddress.StartsWith("[", StringComparison.Ordinal);

    /// <summary>以系统目录和核心 PID 识别系统进程，避免把应用自身的端口一并隐藏。</summary>
    public bool IsSystemProcess
    {
        get
        {
            if (ProcessId is > 0 and <= 4) return true;
            if (string.IsNullOrWhiteSpace(ProcessPath)) return false;
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return !string.IsNullOrWhiteSpace(windowsDirectory) && ProcessPath.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed record ProcessEntry(string Name, int ProcessId, double CpuPercent, long MemoryBytes, double DiskBytesPerSecond, double NetworkBitsPerSecond, string Path, DateTime? StartedAt)
{
    public string CpuText => $"{CpuPercent:F1}%";
    public string MemoryText => $"{MemoryBytes / 1024d / 1024d:F1} MB";
    public bool IsActive => CpuPercent >= 0.1 || DiskBytesPerSecond >= 10_000 || NetworkBitsPerSecond >= 10_000;
    public bool IsSystemProcess
    {
        get
        {
            if (ProcessId is > 0 and <= 4) return true;
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return !string.IsNullOrWhiteSpace(windowsDirectory) && !string.IsNullOrWhiteSpace(Path) && Path.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase);
        }
    }
    public string StartedAtText => StartedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—";
}

public sealed record ServiceEntry(string Name, string DisplayName, string Status);

public sealed record EnvironmentVariableEntry(string Name, string Value, EnvironmentVariableTarget Target);
