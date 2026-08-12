using System.Globalization;
using System.Security.Principal;
using System.Text.RegularExpressions;
using ScreenshotApp.HardwareMonitoring;

namespace XTool.HardwareSensorAgent;

internal enum AgentMode
{
    Serve,
    Install,
    Uninstall
}

internal sealed record AgentArguments(
    AgentMode Mode,
    string? UserSid = null,
    string? SourceDirectory = null,
    string? ManifestPath = null,
    string? RequestingUserSid = null,
    int ProtocolVersion = 0)
{
    private static readonly Regex SidPattern = new("^S-1-[0-9-]{6,180}$", RegexOptions.CultureInvariant);
    private static readonly Regex NoncePattern = new("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex PipePattern = new("^[A-Za-z0-9._-]{1,200}$", RegexOptions.CultureInvariant);

    public static AgentArguments Parse(string[] args)
    {
        if (args.Length >= 1 && string.Equals(args[0], "--serve", StringComparison.Ordinal))
        {
            Dictionary<string, string> values = ParsePairs(args, 1, ["--protocol", "--sid"]);
            string sid = values["--sid"];
            ValidateSid(sid);
            int protocol = ParseProtocol(values["--protocol"]);

            return new AgentArguments(AgentMode.Serve, UserSid: sid, ProtocolVersion: protocol);
        }

        if (args.Length >= 1 && string.Equals(args[0], "--install", StringComparison.Ordinal))
        {
            Dictionary<string, string> values = ParsePairs(args, 1, ["--source", "--manifest", "--requesting-sid"]);
            ValidateSid(values["--requesting-sid"]);
            return new AgentArguments(
                AgentMode.Install,
                SourceDirectory: values["--source"],
                ManifestPath: values["--manifest"],
                RequestingUserSid: values["--requesting-sid"]);
        }

        if (args.Length >= 1 && string.Equals(args[0], "--uninstall", StringComparison.Ordinal))
        {
            Dictionary<string, string> values = ParsePairs(args, 1, ["--requesting-sid"]);
            ValidateSid(values["--requesting-sid"]);
            return new AgentArguments(
                AgentMode.Uninstall,
                RequestingUserSid: values["--requesting-sid"]);
        }

        throw new ArgumentException("代理启动模式无效。");
    }

    public static ValidatedLaunchRequest ValidateLaunchRequest(HardwareAgentLaunchRequest request, string expectedUserSid)
    {
        if (request.ProtocolVersion != HardwareSensorProtocol.Version
            || !string.Equals(request.Type, "launch", StringComparison.Ordinal))
        {
            throw new InvalidDataException("启动请求协议版本或类型无效。");
        }

        if (request.ParentProcessId <= 0 || request.ParentProcessId == Environment.ProcessId)
        {
            throw new InvalidDataException("启动请求中的主程序 PID 无效。");
        }

        if (request.ParentProcessStartTimeUtcTicks <= 0)
        {
            throw new InvalidDataException("启动请求中的主程序启动时间无效。");
        }

        string requestingSid = request.RequestingUserSid;
        if (!SidPattern.IsMatch(requestingSid)
            || !string.Equals(requestingSid, expectedUserSid, StringComparison.Ordinal))
        {
            throw new InvalidDataException("启动请求的 Windows 用户与计划任务不匹配。");
        }

        string parentExecutablePath = Path.GetFullPath(request.ParentExecutablePath);
        if (!string.Equals(Path.GetFileName(parentExecutablePath), "XTool.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("启动请求中的主程序路径无效。");
        }

        string nonce = request.Nonce.ToLowerInvariant();
        if (!NoncePattern.IsMatch(nonce))
        {
            throw new InvalidDataException("启动请求中的随机数必须是 128 位十六进制字符串。");
        }

        string pipeName = request.PipeName;
        if (!PipePattern.IsMatch(pipeName))
        {
            throw new InvalidDataException("启动请求中的管道名称无效。");
        }

        string expectedPipe = $"{HardwareSensorProtocol.PipePrefix}.{HardwareSensorProtocol.GetUserKey(expectedUserSid)}.{nonce}";
        if (!string.Equals(pipeName, expectedPipe, StringComparison.Ordinal))
        {
            throw new InvalidDataException("启动请求中的管道与当前用户或随机数不匹配。");
        }

        TimeSpan age = DateTimeOffset.UtcNow - request.TimestampUtc;
        if (age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromSeconds(30))
        {
            throw new InvalidDataException("启动请求已过期或系统时间异常。");
        }

        return new ValidatedLaunchRequest(
            request.ParentProcessId,
            request.ParentProcessStartTimeUtcTicks,
            parentExecutablePath,
            nonce,
            pipeName);
    }

    public static string GetCurrentUserSid()
    {
        return WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法读取当前用户 SID。");
    }

    private static Dictionary<string, string> ParsePairs(
        IReadOnlyList<string> args,
        int offset,
        IReadOnlyCollection<string> allowedKeys)
    {
        if ((args.Count - offset) != allowedKeys.Count * 2)
        {
            throw new ArgumentException("代理参数数量无效。");
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = offset; index < args.Count; index += 2)
        {
            string key = args[index];
            if (!allowedKeys.Contains(key) || !values.TryAdd(key, args[index + 1]))
            {
                throw new ArgumentException("代理参数名称无效或重复。");
            }
        }

        if (allowedKeys.Any(key => !values.ContainsKey(key)))
        {
            throw new ArgumentException("代理缺少必要参数。");
        }

        return values;
    }

    private static void ValidateSid(string sid)
    {
        if (!SidPattern.IsMatch(sid))
        {
            throw new ArgumentException("当前用户 SID 格式无效。");
        }
    }

    private static int ParseProtocol(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int protocol)
            || protocol != HardwareSensorProtocol.Version)
        {
            throw new ArgumentException("传感器代理协议版本无效。");
        }

        return protocol;
    }
}

internal sealed record ValidatedLaunchRequest(
    int ParentProcessId,
    long ParentProcessStartTimeUtcTicks,
    string ParentExecutablePath,
    string Nonce,
    string PipeName);
