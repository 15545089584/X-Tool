using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace ScreenshotApp.HardwareMonitoring;

public enum HardwareSensorAuthorizationState
{
    NotAuthorized,
    Authorized,
    RepairRequired,
    Unavailable
}

public sealed record HardwareSensorAuthorizationStatus(
    HardwareSensorAuthorizationState State,
    string Message);

public sealed record HardwareSensorAuthorizationResult(bool Success, bool Cancelled, string Message);

/// <summary>管理一次授权的硬件传感器代理，不让主程序长期持有管理员权限。</summary>
public static class HardwareSensorAuthorizationService
{
    private const string AgentExecutableName = "XTool.HardwareSensorAgent.exe";
    private const string ManifestFileName = "bundle-manifest-v2.json";
    private static readonly string[] BundleFileNames =
    [
        AgentExecutableName,
        "XTool.HardwareSensorAgent.dll",
        "XTool.HardwareSensorAgent.deps.json",
        "XTool.HardwareSensorAgent.runtimeconfig.json",
        "LibreHardwareMonitorLib.dll",
        "BlackSharp.Core.dll",
        "DiskInfoToolkit.dll",
        "HidSharp.dll",
        "libMonoPosixHelper.dll",
        "Mono.Posix.NETStandard.dll",
        "MonoPosixHelper.dll",
        "RAMSPDToolkit-NDD.dll",
        "System.CodeDom.dll",
        "System.IO.Ports.dll",
        "System.Management.dll",
        "System.Threading.AccessControl.dll"
    ];

    private static readonly HashSet<string> RequiredBundleFiles = new(BundleFileNames, StringComparer.OrdinalIgnoreCase);

    public static string CurrentUserSid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("无法读取当前 Windows 用户 SID。");

    public static string BundleDirectory => Path.Combine(AppContext.BaseDirectory, "tools", "hardware-sensors-v2");

    public static string ManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "X-Tool",
        "HardwareSensors",
        ManifestFileName);

    public static string InstalledRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "X-Tool",
        "Agents",
        "HardwareSensors");

    public static string InstalledAgentPath => Path.Combine(InstalledRoot, GetBundleVersion(), AgentExecutableName);

    public static string TaskName => HardwareSensorProtocol.GetScheduledTaskPath(CurrentUserSid);

    public static async Task<HardwareSensorAuthorizationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var taskXml = await QueryTaskXmlAsync(cancellationToken).ConfigureAwait(false);
            if (taskXml is null)
            {
                return Directory.Exists(InstalledRoot)
                    ? new(HardwareSensorAuthorizationState.RepairRequired, "检测到代理文件残留，需要重新授权修复")
                    : new(HardwareSensorAuthorizationState.NotAuthorized, "尚未授权硬件实时监控");
            }

            if (!File.Exists(InstalledAgentPath))
            {
                return new(HardwareSensorAuthorizationState.RepairRequired, "计划任务存在，但受保护的代理文件缺失");
            }

            if (!ValidateTaskDefinition(taskXml, out string taskError))
            {
                return new(HardwareSensorAuthorizationState.RepairRequired, taskError);
            }

            HardwareAgentBundleManifest sourceManifest = await EnsureBundleManifestAsync(cancellationToken).ConfigureAwait(false);
            if (!await ValidateInstalledFilesAsync(sourceManifest, cancellationToken).ConfigureAwait(false))
            {
                return new(HardwareSensorAuthorizationState.RepairRequired, "代理文件与当前版本不一致，需要重新授权修复");
            }

            return new(HardwareSensorAuthorizationState.Authorized, "已授权：启动 X-Tool 时自动连接独立传感器代理");
        }
        catch (FileNotFoundException exception)
        {
            return new(HardwareSensorAuthorizationState.Unavailable, exception.Message);
        }
        catch (Exception exception)
        {
            return new(HardwareSensorAuthorizationState.Unavailable, $"暂时无法验证授权状态：{exception.GetBaseException().Message}");
        }
    }

    public static async Task<HardwareSensorAuthorizationResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureBundleManifestAsync(cancellationToken).ConfigureAwait(false);
            string installer = Path.Combine(BundleDirectory, AgentExecutableName);
            var exitCode = await RunElevatedAsync(
                installer,
                [
                    "--install",
                    "--source", BundleDirectory,
                    "--manifest", ManifestPath,
                    "--requesting-sid", CurrentUserSid
                ],
                cancellationToken).ConfigureAwait(false);
            if (exitCode is null)
            {
                return new(false, true, "已取消硬件传感器授权，原有设置未改变");
            }
            if (exitCode != 0)
            {
                return new(false, false, "硬件传感器授权失败；未安装不完整的代理任务");
            }

            HardwareSensorAuthorizationStatus status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return status.State == HardwareSensorAuthorizationState.Authorized
                ? new(true, false, "硬件实时监控已授权，之后启动 X-Tool 会自动连接")
                : new(false, false, status.Message);
        }
        catch (Exception exception)
        {
            return new(false, false, $"硬件传感器授权失败：{exception.GetBaseException().Message}");
        }
    }

    public static async Task<HardwareSensorAuthorizationResult> UninstallAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string installer = Path.Combine(BundleDirectory, AgentExecutableName);
            if (!File.Exists(installer))
            {
                return new(false, false, "当前安装包缺少硬件传感器代理，无法安全取消授权");
            }

            var exitCode = await RunElevatedAsync(
                installer,
                ["--uninstall", "--requesting-sid", CurrentUserSid],
                cancellationToken).ConfigureAwait(false);
            if (exitCode is null)
            {
                return new(false, true, "已取消操作，原有硬件监控授权保持不变");
            }
            return exitCode == 0
                ? new(true, false, "已取消硬件实时监控授权")
                : new(false, false, "取消授权失败，请检查任务计划程序是否可用");
        }
        catch (Exception exception)
        {
            return new(false, false, $"取消授权失败：{exception.GetBaseException().Message}");
        }
    }

    public static async Task RunTaskAsync(CancellationToken cancellationToken)
    {
        var result = await RunSchtasksAsync(["/Run", "/TN", TaskName], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("无法启动已授权的硬件传感器任务。");
        }
    }

    public static async Task EndTaskAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RunSchtasksAsync(["/End", "/TN", TaskName], cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 管道关闭后代理会自行退出；结束任务只是收尾兜底。
        }
    }

    public static async Task<HardwareAgentBundleManifest> EnsureBundleManifestAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(BundleDirectory))
        {
            throw new FileNotFoundException("硬件传感器代理安装包不存在，请修复或重新安装 X-Tool。");
        }

        var files = new List<HardwareAgentBundleFile>();
        foreach (string fileName in BundleFileNames)
        {
            string path = Path.Combine(BundleDirectory, fileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"硬件传感器代理缺少必要文件：{fileName}");
            }

            FileInfo info = new(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException($"硬件传感器安装包包含不安全的重解析点：{fileName}");
            }
            string hash = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            files.Add(new(fileName, info.Length, hash));
        }
        if (files.Count != RequiredBundleFiles.Count)
        {
            throw new InvalidDataException("硬件传感器代理安装包文件不完整。");
        }

        string version = GetBundleVersion();
        var manifest = new HardwareAgentBundleManifest(HardwareSensorProtocol.Version, version, CurrentUserSid, files);
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        string temporary = $"{ManifestPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, HardwareSensorProtocol.JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporary, ManifestPath, true);
        return manifest;
    }

    private static async Task<bool> ValidateInstalledFilesAsync(
        HardwareAgentBundleManifest manifest,
        CancellationToken cancellationToken)
    {
        string versionDirectory = Path.Combine(InstalledRoot, SanitizeVersion(manifest.AgentVersion));
        foreach (HardwareAgentBundleFile file in manifest.Files)
        {
            string path = Path.Combine(versionDirectory, file.Name);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Length)
            {
                return false;
            }
            if (!string.Equals(await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string installedManifestPath = Path.Combine(versionDirectory, "agent-manifest.json");
        if (!File.Exists(installedManifestPath)) return false;
        HardwareAgentBundleManifest? installedManifest = JsonSerializer.Deserialize<HardwareAgentBundleManifest>(
            await File.ReadAllTextAsync(installedManifestPath, cancellationToken).ConfigureAwait(false),
            HardwareSensorProtocol.JsonOptions);
        return installedManifest == manifest
               || installedManifest is not null
               && installedManifest.ProtocolVersion == manifest.ProtocolVersion
               && installedManifest.AgentVersion == manifest.AgentVersion
               && installedManifest.RequestingUserSid == manifest.RequestingUserSid
               && installedManifest.Files.SequenceEqual(manifest.Files);
    }

    private static bool ValidateTaskDefinition(string xml, out string error)
    {
        try
        {
            XDocument document = XDocument.Parse(xml);
            XElement root = document.Root ?? throw new InvalidDataException();
            IEnumerable<XElement> Elements(string localName) => root.Descendants().Where(item => item.Name.LocalName == localName);
            string? command = Elements("Command").SingleOrDefault()?.Value;
            string? arguments = Elements("Arguments").SingleOrDefault()?.Value;
            string? workingDirectory = Elements("WorkingDirectory").SingleOrDefault()?.Value;
            string? userId = Elements("UserId").SingleOrDefault()?.Value;
            string? logonType = Elements("LogonType").SingleOrDefault()?.Value;
            string? runLevel = Elements("RunLevel").SingleOrDefault()?.Value;
            string? instances = Elements("MultipleInstancesPolicy").SingleOrDefault()?.Value;
            bool hasTriggers = Elements("Triggers").Any(item => item.Elements().Any());

            bool valid = Elements("Exec").Count() == 1
                         && !hasTriggers
                         && string.Equals(Path.GetFullPath(command ?? string.Empty), Path.GetFullPath(InstalledAgentPath), StringComparison.OrdinalIgnoreCase)
                         && string.Equals(Path.GetFullPath(workingDirectory ?? string.Empty), Path.GetDirectoryName(Path.GetFullPath(InstalledAgentPath)), StringComparison.OrdinalIgnoreCase)
                         && string.Equals(arguments?.Trim(), $"--serve --protocol {HardwareSensorProtocol.Version} --sid {CurrentUserSid}", StringComparison.Ordinal)
                         && string.Equals(userId, CurrentUserSid, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(logonType, "InteractiveToken", StringComparison.OrdinalIgnoreCase)
                         && string.Equals(runLevel, "HighestAvailable", StringComparison.OrdinalIgnoreCase)
                         && string.Equals(instances, "IgnoreNew", StringComparison.OrdinalIgnoreCase);
            error = valid ? string.Empty : "硬件传感器任务定义已变化，需要重新授权修复";
            return valid;
        }
        catch
        {
            error = "无法验证硬件传感器任务定义，需要重新授权修复";
            return false;
        }
    }

    private static async Task<string?> QueryTaskXmlAsync(CancellationToken cancellationToken)
    {
        var result = await RunSchtasksAsync(["/Query", "/TN", TaskName, "/XML"], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output) ? result.Output : null;
    }

    private static async Task<int?> RunElevatedAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = BundleDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动管理员授权程序。");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return null;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunSchtasksAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动任务计划程序工具。");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string text = await output.ConfigureAwait(false);
        _ = await error.ConfigureAwait(false);
        return (process.ExitCode, text);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SanitizeVersion(string version)
    {
        string cleaned = new(version.Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "0.0.0.0" : cleaned;
    }

    private static string GetBundleVersion()
    {
        string path = Path.Combine(BundleDirectory, "XTool.HardwareSensorAgent.dll");
        if (!File.Exists(path)) return "0.0.0.0";
        return SanitizeVersion(AssemblyName.GetAssemblyName(path).Version?.ToString() ?? "0.0.0.0");
    }
}
