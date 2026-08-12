using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenshotApp.HardwareMonitoring;

namespace XTool.HardwareSensorAgent;

internal static class AgentInstallationManager
{
    private const string InstalledManifestName = "agent-manifest.json";
    private const int MaximumManifestBytes = 128 * 1024;
    private static readonly string InstallationRoot = Path.GetFullPath(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "X-Tool", "Agents", "HardwareSensors"));
    private static readonly Regex VersionPattern = new("^[0-9A-Za-z._-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedBundleFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "XTool.HardwareSensorAgent.exe",
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
    };
    private static readonly string[] RequiredBundleFiles = AllowedBundleFiles.ToArray();

    public static int Install(
        string sourceDirectory,
        string manifestPath,
        string requestingUserSid)
    {
        EnsureAdministratorAndMatchingUser(requestingUserSid);
        string source = ValidateSourceDirectory(sourceDirectory);
        HardwareAgentBundleManifest manifest = ReadAndValidateManifest(manifestPath, requestingUserSid);
        string version = ValidateVersion(manifest.AgentVersion);

        Directory.CreateDirectory(InstallationRoot);
        EnsureNoReparsePoints(InstallationRoot, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

        string target = Path.Combine(InstallationRoot, version);
        string staging = Path.Combine(InstallationRoot, $".install-{version}-{Guid.NewGuid():N}");
        string backup = Path.Combine(InstallationRoot, $".backup-{version}-{Guid.NewGuid():N}");
        string taskPath = HardwareSensorProtocol.GetScheduledTaskPath(requestingUserSid);
        bool targetMovedToBackup = false;
        bool stagingPromoted = false;
        bool taskRegistrationAttempted = false;

        Directory.CreateDirectory(staging);
        try
        {
            CopyAndVerifyBundle(source, staging, manifest);
            VerifyAgentAssemblyVersion(staging, version);
            WriteInstalledManifest(staging, manifest);

            // 全部新文件完成校验后才停止旧实例，缩短升级不可用窗口。
            EndTask(taskPath, ignoreErrors: true);
            if (Directory.Exists(target))
            {
                EnsureNoReparsePoints(target, InstallationRoot);
                Directory.Move(target, backup);
                targetMovedToBackup = true;
            }

            Directory.Move(staging, target);
            stagingPromoted = true;
            taskRegistrationAttempted = true;
            RegisterTask(
                Path.Combine(target, "XTool.HardwareSensorAgent.exe"),
                requestingUserSid);

            if (targetMovedToBackup && Directory.Exists(backup))
            {
                TryDeleteOwnedDirectory(backup);
            }

            return 0;
        }
        catch
        {
            // 注册失败时保留旧任务定义，并尽力恢复同版本旧目录，避免任务指向缺失文件。
            if (taskRegistrationAttempted)
            {
                DeleteTask(taskPath, ignoreMissing: true);
            }
            if (stagingPromoted && !taskRegistrationAttempted && Directory.Exists(target))
            {
                TryDeleteOwnedDirectory(target);
            }

            if (targetMovedToBackup && Directory.Exists(backup))
            {
                if (Directory.Exists(target))
                {
                    TryDeleteOwnedDirectory(target);
                }
                Directory.Move(backup, target);
                RegisterTask(Path.Combine(target, "XTool.HardwareSensorAgent.exe"), requestingUserSid);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                TryDeleteOwnedDirectory(staging);
            }
        }
    }

    public static int Uninstall(string requestingUserSid)
    {
        EnsureAdministratorAndMatchingUser(requestingUserSid);
        string taskPath = HardwareSensorProtocol.GetScheduledTaskPath(requestingUserSid);

        // 先删除当前用户的提权入口。代理目录可能仍被其他 Windows 用户的独立任务引用，
        // 因此取消单个用户授权时保留受保护的休眠组件，由产品卸载程序统一清理。
        DeleteTask(taskPath, ignoreMissing: true);

        return 0;
    }

    private static HardwareAgentBundleManifest ReadAndValidateManifest(string manifestPath, string requestingUserSid)
    {
        string path = Path.GetFullPath(manifestPath);
        FileInfo info = new(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0
            || info.Length <= 0 || info.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("传感器代理安装清单不存在、过大或路径不安全。");
        }

        byte[] payload;
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            payload = new byte[checked((int)stream.Length)];
            stream.ReadExactly(payload);
        }

        HardwareAgentBundleManifest manifest = JsonSerializer.Deserialize<HardwareAgentBundleManifest>(
            payload,
            HardwareSensorProtocol.JsonOptions)
            ?? throw new InvalidDataException("传感器代理安装清单格式无效。");
        if (manifest.ProtocolVersion != HardwareSensorProtocol.Version
            || !string.Equals(manifest.RequestingUserSid, requestingUserSid, StringComparison.Ordinal))
        {
            throw new InvalidDataException("传感器代理安装清单的协议或 Windows 用户不匹配。");
        }

        _ = ValidateVersion(manifest.AgentVersion);
        if (manifest.Files.Count <= 0 || manifest.Files.Count > AllowedBundleFiles.Count)
        {
            throw new InvalidDataException("传感器代理安装清单文件数量无效。");
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (HardwareAgentBundleFile file in manifest.Files)
        {
            if (!AllowedBundleFiles.Contains(file.Name)
                || !string.Equals(file.Name, Path.GetFileName(file.Name), StringComparison.Ordinal)
                || file.Length <= 0
                || !Sha256Pattern.IsMatch(file.Sha256)
                || !names.Add(file.Name))
            {
                throw new InvalidDataException($"传感器代理安装清单包含无效文件：{file.Name}");
            }
        }

        if (RequiredBundleFiles.Any(required => !names.Contains(required)))
        {
            throw new InvalidDataException("传感器代理安装清单缺少必要文件。");
        }

        return manifest;
    }

    private static void CopyAndVerifyBundle(
        string source,
        string staging,
        HardwareAgentBundleManifest manifest)
    {
        foreach (HardwareAgentBundleFile expected in manifest.Files)
        {
            string sourcePath = Path.Combine(source, expected.Name);
            FileInfo info = new(sourcePath);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"传感器代理安装包缺少安全文件：{expected.Name}");
            }

            string destinationPath = Path.Combine(staging, expected.Name);
            using FileStream input = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != expected.Length)
            {
                throw new InvalidDataException($"传感器代理文件长度校验失败：{expected.Name}");
            }

            string sourceHash = Convert.ToHexString(SHA256.HashData(input));
            if (!string.Equals(sourceHash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"传感器代理文件哈希校验失败：{expected.Name}");
            }

            input.Position = 0;
            using (FileStream output = new(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            using FileStream installed = new(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string installedHash = Convert.ToHexString(SHA256.HashData(installed));
            if (installed.Length != expected.Length
                || !string.Equals(installedHash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"传感器代理落盘校验失败：{expected.Name}");
            }
        }
    }

    private static void VerifyAgentAssemblyVersion(string staging, string expectedVersion)
    {
        Version? actual = AssemblyName.GetAssemblyName(
            Path.Combine(staging, "XTool.HardwareSensorAgent.dll")).Version;
        if (actual is null || !string.Equals(actual.ToString(), expectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"传感器代理版本与安装清单不匹配：清单 {expectedVersion}，程序集 {actual}。");
        }
    }

    private static void WriteInstalledManifest(string staging, HardwareAgentBundleManifest manifest)
    {
        string path = Path.Combine(staging, InstalledManifestName);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, HardwareSensorProtocol.JsonOptions);
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    private static string ValidateSourceDirectory(string path)
    {
        string source = Path.GetFullPath(path);
        string expected = Path.GetFullPath(AppContext.BaseDirectory);
        if (!string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), expected.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("传感器代理安装源必须是当前已授权的发布目录。");
        }
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("传感器代理安装源目录不存在。");
        }

        EnsureNoReparsePoints(source, Path.GetPathRoot(source)!);
        return source;
    }

    private static string ValidateVersion(string version)
    {
        if (!VersionPattern.IsMatch(version))
        {
            throw new InvalidDataException("传感器代理版本号不能用作安全目录名。");
        }

        return version;
    }

    private static void RegisterTask(string executablePath, string currentUserSid)
    {
        Type schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new PlatformNotSupportedException("当前系统不支持任务计划程序接口。");
        dynamic? scheduler = null;
        dynamic? root = null;
        dynamic? folder = null;
        dynamic? definition = null;
        dynamic? action = null;
        dynamic? registeredTask = null;
        try
        {
            scheduler = Activator.CreateInstance(schedulerType)
                ?? throw new InvalidOperationException("无法创建任务计划程序对象。");
            scheduler.Connect();
            root = scheduler.GetFolder("\\");
            try
            {
                folder = root.GetFolder("X-Tool");
            }
            catch (Exception exception) when (exception is COMException or FileNotFoundException)
            {
                folder = root.CreateFolder("X-Tool");
            }

            definition = scheduler.NewTask(0);
            definition.RegistrationInfo.Description = "X-Tool 按需只读硬件传感器代理";
            definition.Settings.Enabled = true;
            definition.Settings.Hidden = true;
            definition.Settings.AllowDemandStart = true;
            definition.Settings.StartWhenAvailable = false;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.WakeToRun = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
            definition.Principal.RunLevel = 1; // TASK_RUNLEVEL_HIGHEST
            definition.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
            definition.Principal.UserId = currentUserSid;
            action = definition.Actions.Create(0); // TASK_ACTION_EXEC
            action.Path = executablePath;
            action.Arguments = $"--serve --protocol {HardwareSensorProtocol.Version} --sid {currentUserSid}";
            action.WorkingDirectory = Path.GetDirectoryName(executablePath);

            registeredTask = folder.RegisterTaskDefinition(
                HardwareSensorProtocol.GetScheduledTaskName(currentUserSid),
                definition,
                6, // TASK_CREATE_OR_UPDATE
                null,
                null,
                3, // TASK_LOGON_INTERACTIVE_TOKEN
                null);
        }
        finally
        {
            ReleaseComObject(registeredTask);
            ReleaseComObject(action);
            ReleaseComObject(definition);
            ReleaseComObject(folder);
            ReleaseComObject(root);
            ReleaseComObject(scheduler);
        }
    }

    private static void EndTask(string taskPath, bool ignoreErrors)
    {
        RunSchtasks(["/End", "/TN", taskPath], [0, 1], ignoreErrors);
    }

    private static void DeleteTask(string taskPath, bool ignoreMissing)
    {
        EndTask(taskPath, ignoreErrors: true);
        RunSchtasks(["/Delete", "/TN", taskPath, "/F"], [0, 1], ignoreMissing);
    }

    private static void RunSchtasks(
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<int> acceptableExitCodes,
        bool ignoreErrors)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动任务计划程序管理工具。");
        process.WaitForExit(15_000);
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            if (!ignoreErrors)
            {
                throw new TimeoutException("任务计划程序操作超时。");
            }
        }
        else if (!acceptableExitCodes.Contains(process.ExitCode) && !ignoreErrors)
        {
            throw new InvalidOperationException($"任务计划程序操作失败，退出码 {process.ExitCode}。");
        }
    }

    private static void EnsureAdministratorAndMatchingUser(string requestingUserSid)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("安装或卸载传感器代理需要管理员权限。");
        }

        string elevatedSid = identity.User?.Value
            ?? throw new UnauthorizedAccessException("无法读取管理员进程的 Windows 用户。");
        if (!string.Equals(elevatedSid, requestingUserSid, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "不能使用其他管理员账号授权自动硬件监控，请使用当前 Windows 用户的管理员令牌。");
        }
    }

    private static void TryDeleteOwnedDirectory(string path, bool throwOnFailure = false)
    {
        try
        {
            EnsureNoReparsePoints(path, InstallationRoot);
            Directory.Delete(path, recursive: true);
        }
        catch when (!throwOnFailure)
        {
            // 已完成任务切换时，旧版本残留不再具备提权入口，可留待下次安装清理。
        }
    }

    private static void EnsureNoReparsePoints(string path, string stopAt)
    {
        string current = Path.GetFullPath(path);
        string boundary = Path.GetFullPath(stopAt);
        string currentComparison = NormalizePathForComparison(current);
        string boundaryComparison = NormalizePathForComparison(boundary);
        string boundaryPrefix = boundaryComparison.EndsWith(Path.DirectorySeparatorChar)
            ? boundaryComparison
            : boundaryComparison + Path.DirectorySeparatorChar;
        if (!currentComparison.Equals(boundaryComparison, StringComparison.OrdinalIgnoreCase)
            && !currentComparison.StartsWith(boundaryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("路径不在允许的目录边界内。");
        }

        while (true)
        {
            if (Directory.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException($"路径中不允许存在重解析点：{current}");
            }

            if (NormalizePathForComparison(current).Equals(boundaryComparison, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = Directory.GetParent(current)?.FullName
                ?? throw new UnauthorizedAccessException("路径不在允许的目录边界内。");
        }
    }

    private static string NormalizePathForComparison(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
