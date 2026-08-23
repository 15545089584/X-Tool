using Microsoft.Win32;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Windows.Media;

namespace ScreenshotApp.SystemTools;

/// <summary>
/// 汇总 Windows 主流与高级自动启动位置。各来源失败时相互隔离，不因单项权限不足丢弃其他结果。
/// </summary>
internal static class StartupManagementService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string PolicyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "System");
    private static readonly string DisabledStorePath = Path.Combine(StateDirectory, "startup-disabled.json");
    private static readonly string DisabledFilesDirectory = Path.Combine(StateDirectory, "StartupBackup");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly AsyncLocal<Dictionary<string, FileIdentity>?> IdentityCache = new();
    private static readonly AsyncLocal<HashSet<string>?> RunningProcessNames = new();

    internal static StartupDiscoveryResult Discover(IProgress<StartupScanProgress>? progress, CancellationToken cancellationToken)
    {
        var entries = new List<StartupEntry>();
        var warnings = new List<string>();
        IdentityCache.Value = new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase);
        RunningProcessNames.Value = CaptureRunningProcessNames();
        var providers = new (string Name, Action<List<StartupEntry>, CancellationToken> Scan)[]
        {
            ("注册表登录项", ScanRegistryRunEntries),
            ("启动文件夹", ScanStartupFolders),
            ("计划任务", ScanScheduledTasks),
            ("系统服务", ScanServices),
            ("启动驱动", ScanDrivers),
            ("高级加载点", ScanAdvancedRegistryLocations),
            ("WMI 永久订阅", ScanWmiSubscriptions),
            ("X-Tool 可恢复项目", ScanDisabledEntries)
        };

        try
        {
            foreach (var provider in providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new StartupScanProgress(provider.Name, entries.Count));
                try
                {
                    provider.Scan(entries, cancellationToken);
                }
                catch (Exception exception) when (IsExpectedDiscoveryException(exception))
                {
                    warnings.Add($"{provider.Name}：{FriendlyException(exception)}");
                }
            }

            var normalized = entries
                .GroupBy(entry => entry.StableId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(entry => entry.IsEnabled).First())
                .OrderBy(entry => entry.CategoryOrder)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            progress?.Report(new StartupScanProgress("正在整理结果", normalized.Length));
            return new StartupDiscoveryResult(normalized, warnings, DateTime.Now);
        }
        finally
        {
            IdentityCache.Value = null;
            RunningProcessNames.Value = null;
        }
    }

    internal static Task<StartupToggleResult> ToggleAsync(StartupEntry entry, bool enable, CancellationToken cancellationToken) =>
        Task.Run(() => Toggle(entry, enable, cancellationToken), cancellationToken);

    private static StartupToggleResult Toggle(StartupEntry entry, bool enable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!entry.CanToggle || entry.ToggleDescriptor is null)
        {
            return new StartupToggleResult(false, entry.ToggleHint);
        }

        try
        {
            return entry.ToggleDescriptor.Kind switch
            {
                StartupToggleKind.RegistryRun => ToggleRegistryRun(entry, entry.ToggleDescriptor, enable),
                StartupToggleKind.StartupFolder => ToggleStartupFolder(entry, entry.ToggleDescriptor, enable),
                StartupToggleKind.ScheduledTask => ToggleScheduledTask(entry.ToggleDescriptor.TaskPath, enable),
                _ => new StartupToggleResult(false, "该来源保持只读，不能由 X-Tool 修改。")
            };
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException or COMException or InvalidOperationException)
        {
            return new StartupToggleResult(false, FriendlyException(exception));
        }
    }

    private static void ScanRegistryRunEntries(List<StartupEntry> output, CancellationToken cancellationToken)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadRegistryCommandValues(output, hive, view, RunKeyPath, "Run", isRunOnce: false, cancellationToken);
            ReadRegistryCommandValues(output, hive, view, RunOnceKeyPath, "RunOnce", isRunOnce: true, cancellationToken);
            ReadRegistryCommandValues(output, hive, view, PolicyRunKeyPath, "策略登录脚本", isRunOnce: false, cancellationToken, policyEntry: true);
        }
    }

    private static void ReadRegistryCommandValues(
        List<StartupEntry> output,
        RegistryHive hive,
        RegistryView view,
        string keyPath,
        string sourceName,
        bool isRunOnce,
        CancellationToken cancellationToken,
        bool policyEntry = false)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(keyPath, writable: false);
        if (key is null) return;

        foreach (var valueName in key.GetValueNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var valueKind = key.GetValueKind(valueName);
            if (valueKind is not RegistryValueKind.String and not RegistryValueKind.ExpandString) continue;
            var command = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command)) continue;
            var scope = hive == RegistryHive.CurrentUser ? "当前用户" : "所有用户";
            var sourceLocation = $"{HiveName(hive)}\\{keyPath} ({ViewName(view)})";
            var executablePath = ResolveExecutablePath(command);
            var identity = ReadIdentity(executablePath);
            var canToggle = hive == RegistryHive.CurrentUser && !isRunOnce && !policyEntry;
            var descriptor = canToggle
                ? new StartupToggleDescriptor(StartupToggleKind.RegistryRun, hive, view, keyPath, valueName, command, null, null)
                : null;
            output.Add(CreateEntry(
                stableId: hive == RegistryHive.CurrentUser
                    ? $"registry:{hive}:{keyPath}:{valueName}"
                    : $"registry:{hive}:{view}:{keyPath}:{valueName}",
                name: string.IsNullOrWhiteSpace(valueName) ? Path.GetFileNameWithoutExtension(executablePath) : valueName,
                category: StartupCategory.LoginApplication,
                source: sourceName,
                scope: scope,
                trigger: isRunOnce ? "下次登录一次" : "用户登录",
                command: command,
                executablePath: executablePath,
                sourceLocation: sourceLocation,
                account: scope,
                isEnabled: true,
                isRunning: IsExecutableRunning(executablePath),
                canToggle: canToggle,
                toggleHint: isRunOnce ? "RunOnce 通常用于完成安装或更新，X-Tool 只读展示。" : policyEntry ? "策略配置的登录项应由对应策略管理。" : canToggle ? "禁用时会保存原始注册表值，可随时恢复。" : "所有用户启动项需要管理员权限，首版保持只读。",
                identity: identity,
                descriptor: descriptor));
        }
    }

    private static void ScanStartupFolders(List<StartupEntry> output, CancellationToken cancellationToken)
    {
        var folders = new[]
        {
            (Path: Environment.GetFolderPath(Environment.SpecialFolder.Startup), Scope: "当前用户", CanToggle: true),
            (Path: Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Scope: "所有用户", CanToggle: false)
        };
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path)) continue;
            foreach (var path in Directory.EnumerateFiles(folder.Path, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = ResolveShortcutTarget(path);
                var executablePath = string.IsNullOrWhiteSpace(targetPath) ? path : targetPath;
                var identity = ReadIdentity(executablePath);
                var descriptor = folder.CanToggle
                    ? new StartupToggleDescriptor(StartupToggleKind.StartupFolder, RegistryHive.CurrentUser, RegistryView.Default, null, null, null, path, null)
                    : null;
                output.Add(CreateEntry(
                    stableId: $"startup-folder:{folder.Scope}:{path}",
                    name: Path.GetFileNameWithoutExtension(path),
                    category: StartupCategory.LoginApplication,
                    source: "启动文件夹",
                    scope: folder.Scope,
                    trigger: "用户登录",
                    command: path,
                    executablePath: executablePath,
                    sourceLocation: folder.Path,
                    account: folder.Scope,
                    isEnabled: true,
                    isRunning: IsExecutableRunning(executablePath),
                    canToggle: folder.CanToggle,
                    toggleHint: folder.CanToggle ? "禁用时会移动到 X-Tool 恢复目录，不会删除原文件。" : "公共启动文件夹需要管理员权限，首版保持只读。",
                    identity: identity,
                    descriptor: descriptor));
            }
        }
    }

    private static void ScanScheduledTasks(List<StartupEntry> output, CancellationToken cancellationToken)
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service");
        if (serviceType is null) return;
        dynamic? service = null;
        dynamic? root = null;
        try
        {
            service = Activator.CreateInstance(serviceType);
            if (service is null) return;
            service.Connect();
            root = service.GetFolder("\\");
            ScanTaskFolder(root, output, cancellationToken);
        }
        finally
        {
            ReleaseCom(root);
            ReleaseCom(service);
        }
    }

    private static void ScanTaskFolder(dynamic folder, List<StartupEntry> output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic? tasks = null;
        dynamic? folders = null;
        try
        {
            tasks = folder.GetTasks(1);
            foreach (var taskObject in SnapshotComCollection(tasks))
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic? task = null;
                dynamic? definition = null;
                dynamic? triggers = null;
                dynamic? actions = null;
                dynamic? principal = null;
                dynamic? registration = null;
                try
                {
                    task = taskObject;
                    definition = task.Definition;
                    triggers = definition.Triggers;
                    var triggerTexts = ReadTaskTriggers((object)triggers);
                    if (triggerTexts.Count == 0) continue;
                    actions = definition.Actions;
                    var action = ReadTaskAction((object)actions);
                    principal = definition.Principal;
                    registration = definition.RegistrationInfo;
                    var taskPath = Convert.ToString(task.Path) ?? string.Empty;
                    var author = Convert.ToString(registration.Author) ?? string.Empty;
                    var account = Convert.ToString(principal.UserId) ?? string.Empty;
                    var executablePath = ResolveExecutablePath(action.Command);
                    var identity = ReadIdentity(executablePath, author);
                    var isMicrosoftTask = taskPath.StartsWith("\\Microsoft\\Windows\\", StringComparison.OrdinalIgnoreCase) || identity.IsMicrosoft;
                    var isEnabled = Convert.ToBoolean(task.Enabled);
                    var state = Convert.ToInt32(task.State);
                    var canToggle = !isMicrosoftTask;
                    output.Add(CreateEntry(
                        stableId: $"task:{taskPath}",
                        name: Convert.ToString(task.Name) ?? taskPath,
                        category: StartupCategory.BackgroundTask,
                        source: "计划任务",
                        scope: string.IsNullOrWhiteSpace(account) ? "系统任务" : account,
                        trigger: string.Join("、", triggerTexts),
                        command: action.DisplayText,
                        executablePath: executablePath,
                        sourceLocation: taskPath,
                        account: string.IsNullOrWhiteSpace(account) ? "由任务定义决定" : account,
                        isEnabled: isEnabled,
                        isRunning: state == 4,
                        canToggle: canToggle,
                        toggleHint: canToggle ? "通过 Windows 任务计划程序启用或禁用，任务定义不会被删除。" : "Microsoft 系统任务保持只读，避免破坏维护和安全功能。",
                        identity: identity,
                        descriptor: canToggle ? new StartupToggleDescriptor(StartupToggleKind.ScheduledTask, RegistryHive.CurrentUser, RegistryView.Default, null, null, null, null, taskPath) : null));
                }
                catch (COMException)
                {
                    // 单个受保护或已损坏任务不影响其他任务。
                }
                finally
                {
                    ReleaseCom(registration);
                    ReleaseCom(principal);
                    ReleaseCom(actions);
                    ReleaseCom(triggers);
                    ReleaseCom(definition);
                    ReleaseCom(task);
                }
            }

            folders = folder.GetFolders(0);
            foreach (var folderObject in SnapshotComCollection(folders))
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic? child = null;
                try
                {
                    child = folderObject;
                    ScanTaskFolder(child, output, cancellationToken);
                }
                finally
                {
                    ReleaseCom(child);
                }
            }
        }
        finally
        {
            ReleaseCom(folders);
            ReleaseCom(tasks);
        }
    }

    private static IReadOnlyList<string> ReadTaskTriggers(object triggers)
    {
        var results = new List<string>();
        foreach (var triggerObject in SnapshotComCollection(triggers))
        {
            dynamic? trigger = null;
            try
            {
                trigger = triggerObject;
                var type = Convert.ToInt32(trigger.Type);
                var text = type switch
                {
                    8 => "系统启动",
                    9 => "用户登录",
                    11 => "会话状态变化",
                    0 => "系统事件",
                    6 => "系统空闲",
                    7 => "任务注册",
                    _ => string.Empty
                };
                if (!string.IsNullOrWhiteSpace(text)) results.Add(text);
            }
            finally
            {
                ReleaseCom(trigger);
            }
        }
        return results.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static TaskAction ReadTaskAction(object actions)
    {
        var snapshot = SnapshotComCollection(actions);
        if (snapshot.Count == 0) return new TaskAction(string.Empty, "未读取到任务动作");
        dynamic? action = null;
        try
        {
            action = snapshot[0];
            var type = Convert.ToInt32(action.Type);
            if (type == 0)
            {
                var path = Convert.ToString(action.Path) ?? string.Empty;
                var arguments = Convert.ToString(action.Arguments) ?? string.Empty;
                var command = string.IsNullOrWhiteSpace(arguments) ? path : $"{path} {arguments}";
                return new TaskAction(path, command);
            }
            if (type == 5)
            {
                var classId = Convert.ToString(action.ClassId) ?? string.Empty;
                return new TaskAction(string.Empty, $"COM Handler {classId}".Trim());
            }
            return new TaskAction(string.Empty, $"任务动作类型 {type}");
        }
        finally
        {
            ReleaseCom(action);
        }
    }

    private static void ScanServices(List<StartupEntry> output, CancellationToken cancellationToken)
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name,DisplayName,PathName,StartMode,State,StartName,DelayedAutoStart FROM Win32_Service");
        using var results = searcher.Get();
        using var servicesRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", writable: false);
        foreach (ManagementObject service in results)
        {
            using (service)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Convert.ToString(service["Name"]) ?? string.Empty;
                var startMode = Convert.ToString(service["StartMode"]) ?? string.Empty;
                using var serviceKey = servicesRoot?.OpenSubKey(name, writable: false);
                using var triggerKey = serviceKey?.OpenSubKey("TriggerInfo", writable: false);
                var hasTrigger = triggerKey is not null;
                if (!string.Equals(startMode, "Auto", StringComparison.OrdinalIgnoreCase) && !hasTrigger) continue;
                var delayed = Convert.ToBoolean(service["DelayedAutoStart"] ?? false);
                var command = Convert.ToString(service["PathName"]) ?? string.Empty;
                var executablePath = ResolveExecutablePath(command);
                var identity = ReadIdentity(executablePath);
                var displayName = Convert.ToString(service["DisplayName"]) ?? name;
                var state = Convert.ToString(service["State"]) ?? string.Empty;
                output.Add(CreateEntry(
                    stableId: $"service:{name}",
                    name: displayName,
                    category: StartupCategory.Service,
                    source: "Windows 服务",
                    scope: "系统",
                    trigger: string.Equals(startMode, "Auto", StringComparison.OrdinalIgnoreCase) ? delayed ? "延迟自动启动" : "系统启动" : "服务触发器",
                    command: command,
                    executablePath: executablePath,
                    sourceLocation: $"HKLM\\SYSTEM\\CurrentControlSet\\Services\\{name}",
                    account: Convert.ToString(service["StartName"]) ?? "服务账户",
                    isEnabled: !string.Equals(startMode, "Disabled", StringComparison.OrdinalIgnoreCase),
                    isRunning: string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase),
                    canToggle: false,
                    toggleHint: "服务依赖会影响系统稳定性；当前页只读展示，可前往服务管理页进一步检查。",
                    identity: identity,
                    descriptor: null));
            }
        }
    }

    private static void ScanDrivers(List<StartupEntry> output, CancellationToken cancellationToken)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", writable: false);
        if (root is null) return;
        foreach (var name in root.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var key = root.OpenSubKey(name, writable: false);
            if (key is null) continue;
            var type = ConvertToInt32(key.GetValue("Type"), -1);
            var start = ConvertToInt32(key.GetValue("Start"), -1);
            var isDriver = (type & 0x1) != 0 || (type & 0x2) != 0;
            if (!isDriver || start is < 0 or > 2) continue;
            var rawPath = Convert.ToString(key.GetValue("ImagePath", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)) ?? string.Empty;
            var executablePath = NormalizeDriverPath(rawPath);
            var identity = ReadIdentity(executablePath);
            var displayName = Convert.ToString(key.GetValue("DisplayName")) ?? name;
            output.Add(CreateEntry(
                stableId: $"driver:{name}",
                name: displayName.StartsWith('@') ? name : displayName,
                category: StartupCategory.Driver,
                source: (type & 0x2) != 0 ? "文件系统驱动" : "内核驱动",
                scope: "系统",
                trigger: start switch { 0 => "Boot：引导加载", 1 => "System：内核初始化", _ => "Automatic：系统启动" },
                command: rawPath,
                executablePath: executablePath,
                sourceLocation: $"HKLM\\SYSTEM\\CurrentControlSet\\Services\\{name}",
                account: "内核",
                isEnabled: true,
                isRunning: false,
                canToggle: false,
                toggleHint: "启动驱动直接影响系统引导，X-Tool 只读展示。",
                identity: identity,
                descriptor: null));
        }
    }

    private static void ScanAdvancedRegistryLocations(List<StartupEntry> output, CancellationToken cancellationToken)
    {
        var locations = new[]
        {
            new AdvancedRegistryLocation(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Control\Session Manager", "BootExecute", "引导执行", "系统引导"),
            new AdvancedRegistryLocation(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Shell", "Winlogon Shell", "用户登录"),
            new AdvancedRegistryLocation(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Userinit", "Winlogon Userinit", "用户登录"),
            new AdvancedRegistryLocation(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", "AppInit_DLLs", "AppInit DLL", "进程加载"),
            new AdvancedRegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows NT\CurrentVersion\Windows", "Load", "用户 Load", "用户登录"),
            new AdvancedRegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows NT\CurrentVersion\Windows", "Run", "用户 Windows Run", "用户登录")
        };

        foreach (var location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
            using var key = baseKey.OpenSubKey(location.KeyPath, writable: false);
            var raw = key?.GetValue(location.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var commands = raw switch
            {
                string[] values => values,
                string text when !string.IsNullOrWhiteSpace(text) => new[] { text },
                _ => Array.Empty<string>()
            };
            for (var index = 0; index < commands.Length; index++)
            {
                var command = commands[index];
                if (string.IsNullOrWhiteSpace(command)) continue;
                var executablePath = ResolveExecutablePath(command);
                var identity = ReadIdentity(executablePath);
                output.Add(CreateEntry(
                    stableId: $"advanced:{location.Hive}:{location.KeyPath}:{location.ValueName}:{index}",
                    name: commands.Length == 1 ? location.DisplayName : $"{location.DisplayName} {index + 1}",
                    category: StartupCategory.Advanced,
                    source: location.DisplayName,
                    scope: location.Hive == RegistryHive.CurrentUser ? "当前用户" : "系统",
                    trigger: location.Trigger,
                    command: command,
                    executablePath: executablePath,
                    sourceLocation: $"{HiveName(location.Hive)}\\{location.KeyPath}\\{location.ValueName}",
                    account: location.Hive == RegistryHive.CurrentUser ? "当前用户" : "系统",
                    isEnabled: true,
                    isRunning: IsExecutableRunning(executablePath),
                    canToggle: false,
                    toggleHint: "高级加载点可能影响登录或系统引导，X-Tool 只读展示。",
                    identity: identity,
                    descriptor: null));
            }
        }
    }

    private static void ScanWmiSubscriptions(List<StartupEntry> output, CancellationToken cancellationToken)
    {
        var queries = new[]
        {
            (ClassName: "CommandLineEventConsumer", CommandProperty: "CommandLineTemplate"),
            (ClassName: "ActiveScriptEventConsumer", CommandProperty: "ScriptFileName")
        };
        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\subscription"), new ObjectQuery($"SELECT * FROM {query.ClassName}"));
            using var results = searcher.Get();
            foreach (ManagementObject consumer in results)
            {
                using (consumer)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Convert.ToString(consumer["Name"]) ?? query.ClassName;
                    var command = Convert.ToString(consumer[query.CommandProperty]) ?? string.Empty;
                    var executablePath = ResolveExecutablePath(command);
                    var identity = ReadIdentity(executablePath);
                    output.Add(CreateEntry(
                        stableId: $"wmi:{query.ClassName}:{name}",
                        name: name,
                        category: StartupCategory.Advanced,
                        source: "WMI 永久事件订阅",
                        scope: "系统",
                        trigger: "WMI 事件",
                        command: command,
                        executablePath: executablePath,
                        sourceLocation: $@"root\subscription\{query.ClassName}",
                        account: "由 WMI 订阅定义",
                        isEnabled: true,
                        isRunning: false,
                        canToggle: false,
                        toggleHint: "WMI 永久订阅属于高级系统配置，当前页只读展示。",
                        identity: identity,
                        descriptor: null));
                }
            }
        }
    }

    private static void ScanDisabledEntries(List<StartupEntry> output, CancellationToken cancellationToken)
    {
        foreach (var disabled in LoadDisabledStore())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executablePath = disabled.Kind == StartupToggleKind.StartupFolder
                ? ResolveShortcutTarget(disabled.BackupFilePath ?? string.Empty)
                : ResolveExecutablePath(disabled.Command ?? string.Empty);
            var identity = ReadIdentity(executablePath);
            var descriptor = new StartupToggleDescriptor(disabled.Kind, disabled.Hive, disabled.View,
                disabled.RegistryKeyPath, disabled.ValueName, disabled.Command, disabled.OriginalFilePath, null);
            output.Add(CreateEntry(
                stableId: disabled.StableId,
                name: disabled.DisplayName,
                category: StartupCategory.LoginApplication,
                source: disabled.Kind == StartupToggleKind.RegistryRun ? "Run（X-Tool 已禁用）" : "启动文件夹（X-Tool 已禁用）",
                scope: "当前用户",
                trigger: "用户登录",
                command: disabled.Command ?? disabled.OriginalFilePath ?? string.Empty,
                executablePath: executablePath,
                sourceLocation: disabled.SourceLocation,
                account: "当前用户",
                isEnabled: false,
                isRunning: IsExecutableRunning(executablePath),
                canToggle: true,
                toggleHint: "X-Tool 已保存原始配置，可恢复到原位置。",
                identity: identity,
                descriptor: descriptor));
        }
    }

    private static StartupEntry CreateEntry(
        string stableId,
        string name,
        StartupCategory category,
        string source,
        string scope,
        string trigger,
        string command,
        string executablePath,
        string sourceLocation,
        string account,
        bool isEnabled,
        bool isRunning,
        bool canToggle,
        string toggleHint,
        FileIdentity identity,
        StartupToggleDescriptor? descriptor)
    {
        var missingTarget = !string.IsNullOrWhiteSpace(executablePath) && Path.IsPathFullyQualified(executablePath) && !File.Exists(executablePath);
        var unresolvedCommand = string.IsNullOrWhiteSpace(executablePath) && !string.IsNullOrWhiteSpace(command);
        var attention = missingTarget
            ? "目标文件不存在或当前无权访问"
            : unresolvedCommand
                ? "未能解析独立可执行文件；它可能是 shell 命令、脚本或已经失效的配置"
                : string.Empty;
        var publisher = FirstNonEmpty(identity.SignedPublisher, identity.CompanyName, "未读取到发布者");
        var displayName = BuildDisplayName(name, executablePath, identity.FileDescription, unresolvedCommand);
        return new StartupEntry(
            stableId,
            displayName,
            category,
            source,
            scope,
            trigger,
            string.IsNullOrWhiteSpace(command) ? "未提供命令" : command,
            executablePath,
            sourceLocation,
            account,
            publisher,
            string.IsNullOrWhiteSpace(identity.SignedPublisher) ? "未读取到有效签名信息" : "已读取签名发布者",
            identity.IsMicrosoft,
            isEnabled,
            isRunning,
            canToggle,
            toggleHint,
            !string.IsNullOrWhiteSpace(attention),
            attention,
            category is StartupCategory.LoginApplication or StartupCategory.BackgroundTask
                ? StartupIconProvider.GetIcon(executablePath)
                : null,
            descriptor);
    }

    private static StartupToggleResult ToggleRegistryRun(StartupEntry entry, StartupToggleDescriptor descriptor, bool enable)
    {
        var records = LoadDisabledStore().ToList();
        if (enable)
        {
            var record = records.FirstOrDefault(item => string.Equals(item.StableId, entry.StableId, StringComparison.OrdinalIgnoreCase));
            if (record is null || string.IsNullOrWhiteSpace(record.RegistryKeyPath) || string.IsNullOrWhiteSpace(record.ValueName))
                return new StartupToggleResult(false, "没有找到可恢复的注册表快照。");
            using var baseKey = RegistryKey.OpenBaseKey(record.Hive, record.View);
            using var key = baseKey.CreateSubKey(record.RegistryKeyPath, writable: true);
            if (key is null) return new StartupToggleResult(false, "无法打开原注册表位置。");
            key.SetValue(record.ValueName, record.Command ?? string.Empty, record.RegistryValueKind ?? RegistryValueKind.String);
            records.Remove(record);
            SaveDisabledStore(records);
            return new StartupToggleResult(true, "启动项已恢复；将在下次登录时生效。");
        }

        if (descriptor.RegistryKeyPath is null || descriptor.ValueName is null)
            return new StartupToggleResult(false, "启动项来源信息不完整。");
        using (var baseKey = RegistryKey.OpenBaseKey(descriptor.Hive, descriptor.View))
        using (var key = baseKey.OpenSubKey(descriptor.RegistryKeyPath, writable: true))
        {
            if (key is null) return new StartupToggleResult(false, "启动项注册表位置已不存在。");
            var current = key.GetValue(descriptor.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
            if (current is null) return new StartupToggleResult(false, "启动项已被其他程序移除，请刷新列表。");
            var valueKind = key.GetValueKind(descriptor.ValueName);
            records.RemoveAll(item => string.Equals(item.StableId, entry.StableId, StringComparison.OrdinalIgnoreCase));
            records.Add(new DisabledStartupRecord(entry.StableId, StartupToggleKind.RegistryRun, entry.Name,
                entry.SourceLocation, descriptor.Hive, descriptor.View, descriptor.RegistryKeyPath,
                descriptor.ValueName, current, null, null, valueKind, DateTime.Now));
            SaveDisabledStore(records);
            try
            {
                key.DeleteValue(descriptor.ValueName, throwOnMissingValue: true);
            }
            catch
            {
                records.RemoveAll(item => string.Equals(item.StableId, entry.StableId, StringComparison.OrdinalIgnoreCase));
                TrySaveDisabledStore(records);
                throw;
            }
        }
        return new StartupToggleResult(true, "启动项已禁用并保存恢复快照。");
    }

    private static StartupToggleResult ToggleStartupFolder(StartupEntry entry, StartupToggleDescriptor descriptor, bool enable)
    {
        var records = LoadDisabledStore().ToList();
        if (enable)
        {
            var record = records.FirstOrDefault(item => string.Equals(item.StableId, entry.StableId, StringComparison.OrdinalIgnoreCase));
            if (record is null || string.IsNullOrWhiteSpace(record.OriginalFilePath) || string.IsNullOrWhiteSpace(record.BackupFilePath))
                return new StartupToggleResult(false, "没有找到可恢复的启动文件快照。");
            if (!File.Exists(record.BackupFilePath)) return new StartupToggleResult(false, "恢复文件已不存在，未修改启动目录。");
            if (File.Exists(record.OriginalFilePath)) return new StartupToggleResult(false, "原位置已存在同名文件，为避免覆盖未执行恢复。");
            Directory.CreateDirectory(Path.GetDirectoryName(record.OriginalFilePath)!);
            File.Move(record.BackupFilePath, record.OriginalFilePath);
            records.Remove(record);
            try
            {
                SaveDisabledStore(records);
            }
            catch
            {
                if (!File.Exists(record.BackupFilePath) && File.Exists(record.OriginalFilePath))
                    File.Move(record.OriginalFilePath, record.BackupFilePath);
                records.Add(record);
                TrySaveDisabledStore(records);
                throw;
            }
            return new StartupToggleResult(true, "启动文件已恢复到原目录。");
        }

        var originalPath = descriptor.OriginalFilePath;
        if (string.IsNullOrWhiteSpace(originalPath) || !File.Exists(originalPath))
            return new StartupToggleResult(false, "启动文件已不存在，请刷新列表。");
        Directory.CreateDirectory(DisabledFilesDirectory);
        var backupPath = Path.Combine(DisabledFilesDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(originalPath)}");
        records.RemoveAll(item => string.Equals(item.StableId, entry.StableId, StringComparison.OrdinalIgnoreCase));
        records.Add(new DisabledStartupRecord(entry.StableId, StartupToggleKind.StartupFolder, entry.Name,
            entry.SourceLocation, RegistryHive.CurrentUser, RegistryView.Default, null, null, null,
            originalPath, backupPath, null, DateTime.Now));
        SaveDisabledStore(records);
        try
        {
            File.Move(originalPath, backupPath);
        }
        catch
        {
            records.RemoveAll(item => string.Equals(item.StableId, entry.StableId, StringComparison.OrdinalIgnoreCase));
            TrySaveDisabledStore(records);
            throw;
        }
        return new StartupToggleResult(true, "启动文件已移入 X-Tool 恢复目录。");
    }

    private static StartupToggleResult ToggleScheduledTask(string? taskPath, bool enable)
    {
        if (string.IsNullOrWhiteSpace(taskPath)) return new StartupToggleResult(false, "计划任务路径无效。");
        var serviceType = Type.GetTypeFromProgID("Schedule.Service");
        if (serviceType is null) return new StartupToggleResult(false, "当前系统无法连接任务计划程序。");
        dynamic? service = null;
        dynamic? folder = null;
        dynamic? task = null;
        try
        {
            service = Activator.CreateInstance(serviceType);
            if (service is null) return new StartupToggleResult(false, "当前系统无法连接任务计划程序。");
            service.Connect();
            var separator = taskPath.LastIndexOf('\\');
            var folderPath = separator <= 0 ? "\\" : taskPath[..separator];
            var taskName = taskPath[(separator + 1)..];
            folder = service.GetFolder(folderPath);
            task = folder.GetTask(taskName);
            task.Enabled = enable;
            return new StartupToggleResult(true, enable ? "计划任务已启用。" : "计划任务已禁用；任务定义仍保留在系统中。");
        }
        finally
        {
            ReleaseCom(task);
            ReleaseCom(folder);
            ReleaseCom(service);
        }
    }

    private static IReadOnlyList<DisabledStartupRecord> LoadDisabledStore()
    {
        try
        {
            if (!File.Exists(DisabledStorePath)) return Array.Empty<DisabledStartupRecord>();
            var content = File.ReadAllText(DisabledStorePath);
            return JsonSerializer.Deserialize<List<DisabledStartupRecord>>(content) ?? new List<DisabledStartupRecord>();
        }
        catch
        {
            return Array.Empty<DisabledStartupRecord>();
        }
    }

    private static void SaveDisabledStore(IReadOnlyList<DisabledStartupRecord> records)
    {
        Directory.CreateDirectory(StateDirectory);
        var temporaryPath = DisabledStorePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records, JsonOptions));
        File.Move(temporaryPath, DisabledStorePath, overwrite: true);
    }

    private static void TrySaveDisabledStore(IReadOnlyList<DisabledStartupRecord> records)
    {
        try { SaveDisabledStore(records); }
        catch
        {
            // 回滚记录是尽力而为；保留原异常供界面明确提示。
        }
    }

    private static FileIdentity ReadIdentity(string path, string fallbackPublisher = "")
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new FileIdentity(string.Empty, fallbackPublisher, string.Empty, IsMicrosoftPublisher(fallbackPublisher));
        var cacheKey = $"{path}\u001f{fallbackPublisher}";
        if (IdentityCache.Value?.TryGetValue(cacheKey, out var cached) == true) return cached;
        var company = string.Empty;
        var fileDescription = string.Empty;
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(path);
            company = versionInfo.CompanyName?.Trim() ?? string.Empty;
            fileDescription = FirstNonEmpty(versionInfo.FileDescription ?? string.Empty, versionInfo.ProductName ?? string.Empty);
        }
        catch
        {
            // 文件版本信息是装饰能力，读取失败不应中断启动项清点。
        }
        var signedPublisher = string.Empty;
        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            using var certificate2 = new X509Certificate2(certificate);
            signedPublisher = certificate2.GetNameInfo(X509NameType.SimpleName, false)?.Trim() ?? string.Empty;
        }
        catch
        {
            // 未签名文件保持空发布者，不把它直接判定为恶意。
        }
        var publisher = FirstNonEmpty(signedPublisher, company, fallbackPublisher);
        var identity = new FileIdentity(signedPublisher, company, fileDescription, IsMicrosoftPublisher(publisher) || IsWindowsPath(path));
        var identityCache = IdentityCache.Value;
        if (identityCache is not null) identityCache[cacheKey] = identity;
        return identity;
    }

    private static string BuildDisplayName(string rawName, string executablePath, string fileDescription, bool unresolvedCommand)
    {
        var name = string.IsNullOrWhiteSpace(rawName) ? string.Empty : rawName.Trim();
        if (unresolvedCommand)
        {
            var identifier = string.IsNullOrWhiteSpace(name) ? "未命名配置" : name;
            return $"未识别项 · {identifier}";
        }

        if (!string.IsNullOrWhiteSpace(fileDescription) && LooksLikeTechnicalIdentifier(name))
            return fileDescription.Trim();
        if (!string.IsNullOrWhiteSpace(name)) return name;
        if (!string.IsNullOrWhiteSpace(fileDescription)) return fileDescription.Trim();
        return string.IsNullOrWhiteSpace(executablePath) ? "未命名启动项" : Path.GetFileNameWithoutExtension(executablePath);
    }

    private static bool LooksLikeTechnicalIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (Guid.TryParse(name.Trim('{', '}'), out _)) return true;
        if (name.StartsWith("AF_", StringComparison.OrdinalIgnoreCase)) return true;
        var lettersOrDigits = name.Count(char.IsLetterOrDigit);
        var separators = name.Count(character => character is '_' or '-' or '{' or '}');
        return name.Length >= 20 && separators >= 3 && lettersOrDigits + separators == name.Length;
    }

    private static string ResolveExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        if (File.Exists(expanded)) return Path.GetFullPath(expanded);
        var argument = FirstCommandLineArgument(expanded).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(argument)) return string.Empty;
        if (argument.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            argument = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), argument[12..]);
        if (argument.StartsWith("System32\\", StringComparison.OrdinalIgnoreCase))
            argument = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), argument);
        if (!Path.IsPathFullyQualified(argument))
        {
            var systemCandidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), argument);
            if (File.Exists(systemCandidate)) argument = systemCandidate;
            else
            {
                var extensions = string.IsNullOrWhiteSpace(Path.GetExtension(argument))
                    ? new[] { string.Empty, ".exe", ".com", ".cmd", ".bat" }
                    : new[] { string.Empty };
                var resolved = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .SelectMany(directory => extensions.Select(extension => Path.Combine(directory.Trim('"'), argument + extension)))
                    .FirstOrDefault(File.Exists);
                if (string.IsNullOrWhiteSpace(resolved)) return string.Empty;
                argument = resolved;
            }
        }
        try { return Path.GetFullPath(argument); }
        catch { return argument; }
    }

    private static string FirstCommandLineArgument(string command)
    {
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero || count <= 0) return command;
        try
        {
            return Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer)) ?? command;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    private static string ResolveShortcutTarget(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return path;
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
            var target = shortcut?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null)?.ToString();
            return string.IsNullOrWhiteSpace(target) ? path : Environment.ExpandEnvironmentVariables(target);
        }
        catch
        {
            return path;
        }
        finally
        {
            ReleaseCom(shortcut);
            ReleaseCom(shell);
        }
    }

    private static string NormalizeDriverPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return string.Empty;
        var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
        if (expanded.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), expanded[12..]);
        else if (expanded.StartsWith("System32\\", StringComparison.OrdinalIgnoreCase))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), expanded);
        try { return Path.GetFullPath(expanded); }
        catch { return expanded; }
    }

    private static bool IsExecutableRunning(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name)) return false;
        try { return RunningProcessNames.Value?.Contains(name) ?? Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }

    private static HashSet<string> CaptureRunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return names; }
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(process.ProcessName)) names.Add(process.ProcessName);
                }
                catch
                {
                    // 进程可能在枚举期间退出，忽略单项即可。
                }
            }
        }
        return names;
    }

    private static bool IsWindowsPath(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !string.IsNullOrWhiteSpace(windows) && path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMicrosoftPublisher(string publisher) => publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
        || publisher.Contains("微软", StringComparison.OrdinalIgnoreCase);

    private static int ConvertToInt32(object? value, int fallback)
    {
        try { return Convert.ToInt32(value); }
        catch { return fallback; }
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    private static string HiveName(RegistryHive hive) => hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
    private static string ViewName(RegistryView view) => view == RegistryView.Registry32 ? "32 位" : "64 位";

    private static bool IsExpectedDiscoveryException(Exception exception) => exception is UnauthorizedAccessException
        or IOException
        or System.Security.SecurityException
        or ManagementException
        or COMException
        or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
        or InvalidOperationException;

    private static string FriendlyException(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "当前权限不足，已跳过受保护内容",
        ManagementException => "WMI 查询不可用或权限不足",
        COMException => "Windows 组件返回读取失败",
        _ => exception.Message
    };

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); }
            catch { }
        }
    }

    private static IReadOnlyList<object> SnapshotComCollection(object? collection)
    {
        if (collection is null) return Array.Empty<object>();
        if (collection is IEnumerable enumerable)
        {
            var results = new List<object>();
            foreach (var item in enumerable)
            {
                if (item is not null) results.Add(item);
            }
            return results;
        }

        dynamic dynamicCollection = collection;
        var count = Convert.ToInt32(dynamicCollection.Count);
        var fallback = new List<object>(count);
        for (var index = 1; index <= count; index++)
        {
            var item = dynamicCollection.Item(index);
            if (item is not null) fallback.Add(item);
        }
        return fallback;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private sealed record AdvancedRegistryLocation(RegistryHive Hive, RegistryView View, string KeyPath, string ValueName, string DisplayName, string Trigger);
    private sealed record FileIdentity(string SignedPublisher, string CompanyName, string FileDescription, bool IsMicrosoft);
    private sealed record TaskAction(string Command, string DisplayText);
}

internal enum StartupCategory
{
    LoginApplication,
    BackgroundTask,
    Service,
    Driver,
    Advanced
}

internal enum StartupToggleKind
{
    None,
    RegistryRun,
    StartupFolder,
    ScheduledTask
}

internal sealed record StartupToggleDescriptor(
    StartupToggleKind Kind,
    RegistryHive Hive,
    RegistryView View,
    string? RegistryKeyPath,
    string? ValueName,
    string? Command,
    string? OriginalFilePath,
    string? TaskPath);

internal sealed record DisabledStartupRecord(
    string StableId,
    StartupToggleKind Kind,
    string DisplayName,
    string SourceLocation,
    RegistryHive Hive,
    RegistryView View,
    string? RegistryKeyPath,
    string? ValueName,
    string? Command,
    string? OriginalFilePath,
    string? BackupFilePath,
    RegistryValueKind? RegistryValueKind,
    DateTime DisabledAt);

internal sealed record StartupEntry(
    string StableId,
    string Name,
    StartupCategory Category,
    string Source,
    string Scope,
    string Trigger,
    string Command,
    string ExecutablePath,
    string SourceLocation,
    string Account,
    string Publisher,
    string SignatureStatus,
    bool IsMicrosoft,
    bool IsEnabled,
    bool IsRunning,
    bool CanToggle,
    string ToggleHint,
    bool RequiresAttention,
    string AttentionText,
    ImageSource? Icon,
    StartupToggleDescriptor? ToggleDescriptor)
{
    public int CategoryOrder => Category switch
    {
        StartupCategory.LoginApplication => 0,
        StartupCategory.BackgroundTask => 1,
        StartupCategory.Service => 2,
        StartupCategory.Driver => 3,
        _ => 4
    };
    public string CategoryText => Category switch
    {
        StartupCategory.LoginApplication => "登录应用",
        StartupCategory.BackgroundTask => "后台任务",
        StartupCategory.Service => "系统服务",
        StartupCategory.Driver => "启动驱动",
        _ => "高级加载点"
    };
    public string CategoryAccent => Category switch
    {
        StartupCategory.LoginApplication => "#4D7CFE",
        StartupCategory.BackgroundTask => "#A66CE5",
        StartupCategory.Service => "#16B99B",
        StartupCategory.Driver => "#4D9CB5",
        _ => "#F3A847"
    };
    public string CategoryBackground => Category switch
    {
        StartupCategory.LoginApplication => "#DDE8FF",
        StartupCategory.BackgroundTask => "#EDE2FA",
        StartupCategory.Service => "#E0F5EF",
        StartupCategory.Driver => "#E0EFF3",
        _ => "#FFF0D9"
    };
    public string StatusText => IsEnabled ? IsRunning ? "已启用 · 运行中" : "已启用" : "已禁用";
    public string StatusAccent => IsEnabled ? IsRunning ? "#16A582" : "#4D7CFE" : "#8A9CAF";
    public string StatusBackground => IsEnabled ? IsRunning ? "#E2F6EF" : "#E4ECFF" : "#E8EEF3";
    public string PublisherText => IsMicrosoft ? $"{Publisher} · Microsoft" : Publisher;
    public string ToggleActionText => CanToggle ? IsEnabled ? "关闭" : "开启" : IsEnabled ? "已开启 · 只读" : "已关闭 · 只读";
    public string RunningAccent => IsRunning ? "#16B99B" : "#E16670";
    public string RunningBackground => IsRunning ? "#E0F5EF" : "#FCE7EA";
    public string RunningStateText => IsRunning ? "正在运行" : "当前未运行";
    public string FallbackIconText => RequiresAttention ? "?" : Category switch
    {
        StartupCategory.LoginApplication => "▶",
        StartupCategory.BackgroundTask => "◆",
        StartupCategory.Service => "⚙",
        StartupCategory.Driver => "▣",
        _ => "◇"
    };
    public string ExecutablePathText => string.IsNullOrWhiteSpace(ExecutablePath) ? "未解析到独立可执行文件" : ExecutablePath;
    public string SearchText => $"{Name} {CategoryText} {Source} {Scope} {Trigger} {Command} {ExecutablePath} {Publisher} {SourceLocation} {Account}";
}

internal sealed record StartupDiscoveryResult(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Warnings, DateTime CompletedAt);
internal sealed record StartupScanProgress(string SourceText, int ItemsFound);
internal sealed record StartupToggleResult(bool Success, string Message);
