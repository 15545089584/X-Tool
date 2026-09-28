using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media;

namespace ScreenshotApp.SystemTools;

internal enum InstalledSoftwareKind
{
    Desktop,
    StorePackage
}

internal enum SoftwareMetadataConfidence
{
    Unknown,
    Reported,
    Inferred,
    Measured
}

internal sealed class InstalledSoftwareEntry : INotifyPropertyChanged
{
    private long? _measuredSizeBytes;
    private DateTimeOffset? _lastUsedAt;
    private string _usageEvidence = "未启用本地使用记录";
    private string? _measurementStatus;
    private bool _measurementAttempted;

    public required string StableId { get; init; }
    public required string Name { get; init; }
    public string Publisher { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public InstalledSoftwareKind Kind { get; init; }
    public string Scope { get; init; } = "当前用户";
    public string InstallLocation { get; init; } = string.Empty;
    public string DisplayIconPath { get; init; } = string.Empty;
    public string UninstallCommand { get; init; } = string.Empty;
    public string QuietUninstallCommand { get; init; } = string.Empty;
    public string? ProductCode { get; init; }
    public string? PackageFullName { get; init; }
    public DateTimeOffset? InstallOrUpdateAt { get; init; }
    public string InstallDateEvidence { get; init; } = "安装程序未提供时间";
    public long? EstimatedSizeBytes { get; init; }
    public string SizeEvidence { get; init; } = "安装程序未提供空间信息";
    public bool IsSystemComponent { get; init; }
    public bool CanUninstall { get; init; }
    public bool IsWindowsInstaller { get; init; }
    public RegistryHive? RegistryHive { get; init; }
    public RegistryView? RegistryView { get; init; }
    public string RegistrySubKey { get; init; } = string.Empty;
    public ImageSource? Icon { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string KindText => Kind == InstalledSoftwareKind.StorePackage ? "Store / MSIX" : "桌面程序";
    public string PublisherText => string.IsNullOrWhiteSpace(Publisher) ? "发布者未知" : Publisher;
    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "版本未知" : Version;
    public string InstallDateText => InstallOrUpdateAt?.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "时间未知";
    public string InstallLocationText => string.IsNullOrWhiteSpace(InstallLocation) ? "未登记安装位置" : InstallLocation;
    public string SizeText => FormatBytes(_measuredSizeBytes ?? EstimatedSizeBytes);
    public string SizeDetailText => _measuredSizeBytes.HasValue
        ? "已扫描安装目录"
        : _measurementStatus ?? DefaultSizeDetailText();
    public bool HasKnownSize => _measuredSizeBytes.HasValue || EstimatedSizeBytes.HasValue;
    public bool ShouldAutoMeasureSize => !HasKnownSize && !_measurementAttempted &&
                                         InstalledSoftwareService.IsSpecificApplicationDirectory(InstallLocation, out _) &&
                                         Directory.Exists(InstallLocation);
    public string LastUsedText => _lastUsedAt.HasValue ? RelativeTime(_lastUsedAt.Value) : _usageEvidence;
    public string LastUsedDateText => _lastUsedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "无可用记录";
    public string UninstallStateText => CanUninstall ? "可调用官方卸载程序" : "该项目保持只读";
    public string SearchText => $"{Name} {Publisher} {Version} {KindText} {Scope} {InstallLocation}";
    public long SortableSize => _measuredSizeBytes ?? EstimatedSizeBytes ?? -1;
    public DateTimeOffset SortableInstallDate => InstallOrUpdateAt ?? DateTimeOffset.MinValue;
    public DateTimeOffset SortableLastUsed => _lastUsedAt ?? DateTimeOffset.MinValue;

    internal void SetMeasuredSize(long bytes)
    {
        _measurementAttempted = true;
        _measurementStatus = null;
        _measuredSizeBytes = Math.Max(0, bytes);
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeDetailText));
        OnPropertyChanged(nameof(HasKnownSize));
        OnPropertyChanged(nameof(ShouldAutoMeasureSize));
        OnPropertyChanged(nameof(SortableSize));
    }

    internal void SetSizeMeasurementInProgress()
    {
        _measurementStatus = "正在扫描安装目录…";
        OnPropertyChanged(nameof(SizeDetailText));
    }

    internal void SetSizeMeasurementUnavailable(string reason)
    {
        _measurementAttempted = true;
        _measurementStatus = string.IsNullOrWhiteSpace(reason) ? "安装目录当前无法读取" : reason;
        OnPropertyChanged(nameof(SizeDetailText));
        OnPropertyChanged(nameof(ShouldAutoMeasureSize));
    }

    internal void SetUsage(DateTimeOffset? lastUsedAt, string evidence)
    {
        _lastUsedAt = lastUsedAt;
        _usageEvidence = evidence;
        OnPropertyChanged(nameof(LastUsedText));
        OnPropertyChanged(nameof(LastUsedDateText));
        OnPropertyChanged(nameof(SortableLastUsed));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private string DefaultSizeDetailText()
    {
        if (EstimatedSizeBytes.HasValue) return SizeEvidence;
        if (string.IsNullOrWhiteSpace(InstallLocation)) return "未登记安装位置，无法自动计算";
        if (!Directory.Exists(InstallLocation)) return "安装目录当前不可访问，无法自动计算";
        return SizeEvidence;
    }

    internal static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue || bytes.Value < 0) return "未知";
        var value = (double)bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static string RelativeTime(DateTimeOffset value)
    {
        var elapsed = DateTimeOffset.Now - value.ToLocalTime();
        if (elapsed < TimeSpan.Zero) return value.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (elapsed.TotalMinutes < 2) return "刚刚使用";
        if (elapsed.TotalHours < 1) return $"{Math.Max(2, (int)elapsed.TotalMinutes)} 分钟前";
        if (elapsed.TotalDays < 1) return $"{Math.Max(1, (int)elapsed.TotalHours)} 小时前";
        if (elapsed.TotalDays < 60) return $"{Math.Max(1, (int)elapsed.TotalDays)} 天前";
        return value.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

internal sealed record InstalledSoftwareDiscoveryResult(
    IReadOnlyList<InstalledSoftwareEntry> Entries,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CapturedAt);

internal sealed record SoftwareDirectorySizeResult(bool Success, long Bytes, int Files, string Message);

internal sealed record SoftwareUninstallLaunchResult(bool Success, Process? Process, string Message);

internal sealed record SoftwareDiscoveryProgress(string Stage, int Count);

/// <summary>汇总 Windows 已登记的桌面程序与当前用户打包应用，并只调用软件自身提供的卸载入口。</summary>
internal static class InstalledSoftwareService
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    internal static InstalledSoftwareDiscoveryResult Discover(
        IProgress<SoftwareDiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        var entries = new List<InstalledSoftwareEntry>();
        var warnings = new List<string>();
        progress?.Report(new SoftwareDiscoveryProgress("正在读取桌面程序", 0));

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in Environment.Is64BitOperatingSystem
                     ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                     : new[] { RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ReadDesktopEntries(entries, hive, view, cancellationToken);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                warnings.Add($"{HiveText(hive)} {ViewText(view)}：{exception.Message}");
            }
            progress?.Report(new SoftwareDiscoveryProgress("正在读取桌面程序", entries.Count));
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new SoftwareDiscoveryProgress("正在读取 Store 与 MSIX 应用", entries.Count));
        try
        {
            entries.AddRange(ReadPackagedEntries(cancellationToken));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or JsonException)
        {
            warnings.Add($"Store / MSIX：{exception.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Deduplicate(entries)
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ApplyUsage(normalized);
        progress?.Report(new SoftwareDiscoveryProgress("正在整理结果", normalized.Length));
        return new InstalledSoftwareDiscoveryResult(normalized, warnings, DateTimeOffset.Now);
    }

    private static void ReadDesktopEntries(
        List<InstalledSoftwareEntry> output,
        RegistryHive hive,
        RegistryView view,
        CancellationToken cancellationToken)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var root = baseKey.OpenSubKey(UninstallRoot, writable: false);
        if (root is null) return;

        foreach (var subKeyName in root.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var key = root.OpenSubKey(subKeyName, writable: false);
                if (key is null) continue;
                var name = StringValue(key, "DisplayName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var publisher = StringValue(key, "Publisher");
                var version = StringValue(key, "DisplayVersion");
                var installLocation = NormalizeDirectory(StringValue(key, "InstallLocation"));
                var uninstall = StringValue(key, "UninstallString");
                var quietUninstall = StringValue(key, "QuietUninstallString");
                var displayIcon = ResolveDisplayIcon(StringValue(key, "DisplayIcon"));
                var installDate = ParseInstallDate(StringValue(key, "InstallDate"));
                var estimatedSize = ReadEstimatedSize(key);
                var systemComponent = IntValue(key, "SystemComponent") == 1 ||
                                      !string.IsNullOrWhiteSpace(StringValue(key, "ParentKeyName")) ||
                                      StringValue(key, "ReleaseType").Contains("Update", StringComparison.OrdinalIgnoreCase);
                var noRemove = IntValue(key, "NoRemove") == 1;
                var windowsInstaller = IntValue(key, "WindowsInstaller") == 1;
                var productCode = Guid.TryParse(subKeyName, out _) ? subKeyName : null;
                var iconPath = !string.IsNullOrWhiteSpace(displayIcon)
                    ? displayIcon
                    : TryExecutableFromCommand(uninstall);

                output.Add(new InstalledSoftwareEntry
                {
                    StableId = $"desktop:{hive}:{view}:{subKeyName}",
                    Name = name.Trim(),
                    Publisher = publisher.Trim(),
                    Version = version.Trim(),
                    Kind = InstalledSoftwareKind.Desktop,
                    Scope = hive == RegistryHive.CurrentUser ? "当前用户" : "所有用户",
                    InstallLocation = installLocation,
                    DisplayIconPath = iconPath,
                    UninstallCommand = uninstall,
                    QuietUninstallCommand = quietUninstall,
                    ProductCode = productCode,
                    InstallOrUpdateAt = installDate,
                    InstallDateEvidence = installDate.HasValue ? "安装程序登记的安装或最近维护日期" : "安装程序未提供日期",
                    EstimatedSizeBytes = estimatedSize,
                    SizeEvidence = estimatedSize.HasValue ? "安装程序登记的估算大小" : "安装程序未提供空间信息",
                    IsSystemComponent = systemComponent,
                    CanUninstall = !noRemove && (!string.IsNullOrWhiteSpace(uninstall) || (windowsInstaller && productCode is not null)),
                    IsWindowsInstaller = windowsInstaller,
                    RegistryHive = hive,
                    RegistryView = view,
                    RegistrySubKey = $@"{UninstallRoot}\{subKeyName}",
                    Icon = SystemProgramIconProvider.GetIcon(iconPath)
                });
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // 单个损坏或受保护的卸载项不能阻断其余软件清单。
            }
        }
    }

    private static IReadOnlyList<InstalledSoftwareEntry> ReadPackagedEntries(CancellationToken cancellationToken)
    {
        const string script = "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); " +
                              "Get-AppxPackage | Select-Object Name,PackageFullName,PackageFamilyName,Publisher,Version,InstallLocation,InstallDate,IsFramework,NonRemovable | ConvertTo-Json -Compress -Depth 3";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(script);
        if (!process.Start()) throw new InvalidOperationException("无法启动 Windows PowerShell 读取打包应用。");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        });
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        process.WaitForExit();
        var json = outputTask.GetAwaiter().GetResult().Trim().TrimStart('\uFEFF');
        var error = errorTask.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "读取打包应用失败。" : error);
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<InstalledSoftwareEntry>();

        using var document = JsonDocument.Parse(json);
        var values = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        var result = new List<InstalledSoftwareEntry>();
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageFullName = JsonString(value, "PackageFullName");
            var name = JsonString(value, "Name");
            if (string.IsNullOrWhiteSpace(packageFullName) || string.IsNullOrWhiteSpace(name)) continue;
            var installLocation = NormalizeDirectory(JsonString(value, "InstallLocation"));
            var installedAt = ParseJsonDate(JsonString(value, "InstallDate"));
            var isFramework = JsonBool(value, "IsFramework");
            var nonRemovable = JsonBool(value, "NonRemovable");
            result.Add(new InstalledSoftwareEntry
            {
                StableId = $"package:{packageFullName}",
                Name = name,
                Publisher = FriendlyPublisher(JsonString(value, "Publisher")),
                Version = JsonString(value, "Version"),
                Kind = InstalledSoftwareKind.StorePackage,
                Scope = "当前用户",
                InstallLocation = installLocation,
                PackageFullName = packageFullName,
                InstallOrUpdateAt = installedAt,
                InstallDateEvidence = installedAt.HasValue ? "Windows 包管理器登记的安装或更新日期" : "Windows 包管理器未提供日期",
                SizeEvidence = "点击“计算占用”后按安装目录估算",
                IsSystemComponent = isFramework || nonRemovable,
                CanUninstall = !isFramework && !nonRemovable,
                Icon = FindPackagedIcon(installLocation)
            });
        }
        return result;
    }

    private static IReadOnlyList<InstalledSoftwareEntry> Deduplicate(IEnumerable<InstalledSoftwareEntry> entries)
    {
        return entries
            .GroupBy(DeduplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(CompletenessScore)
                .ThenBy(entry => entry.Kind)
                .First())
            .ToArray();
    }

    private static string DeduplicationKey(InstalledSoftwareEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ProductCode)) return $"product:{entry.ProductCode}";
        if (!string.IsNullOrWhiteSpace(entry.PackageFullName)) return $"package:{entry.PackageFullName}";
        return $"name:{NormalizeIdentity(entry.Name)}|{NormalizeIdentity(entry.Publisher)}|{NormalizeIdentity(entry.Version)}";
    }

    private static int CompletenessScore(InstalledSoftwareEntry entry) =>
        (entry.CanUninstall ? 8 : 0) +
        (!string.IsNullOrWhiteSpace(entry.InstallLocation) ? 4 : 0) +
        (entry.EstimatedSizeBytes.HasValue ? 2 : 0) +
        (entry.InstallOrUpdateAt.HasValue ? 2 : 0) +
        (!string.IsNullOrWhiteSpace(entry.Publisher) ? 1 : 0);

    internal static void ApplyUsage(IEnumerable<InstalledSoftwareEntry> entries)
    {
        var usage = SoftwareUsageTracker.GetSnapshot();
        var trackingEnabled = SoftwareUsageTracker.IsEnabled;
        var trackingStartedAt = SoftwareUsageTracker.TrackingStartedAt;
        foreach (var entry in entries)
        {
            var installRoot = NormalizeDirectory(entry.InstallLocation);
            var iconPath = NormalizeFile(entry.DisplayIconPath);
            var matches = usage.Where(item =>
                    (!string.IsNullOrWhiteSpace(installRoot) && IsPathInside(item.Path, installRoot)) ||
                    (!string.IsNullOrWhiteSpace(iconPath) && PathsEqual(item.Path, iconPath)))
                .ToArray();
            if (matches.Length > 0)
            {
                entry.SetUsage(matches.Max(item => item.LastSeenAt), "由 X-Tool 本地进程记录观察到");
            }
            else if (trackingEnabled)
            {
                var since = trackingStartedAt?.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "本次启用";
                entry.SetUsage(null, $"自 {since} 起未观察到");
            }
            else
            {
                entry.SetUsage(null, "未启用本地使用记录");
            }
        }
    }

    internal static SoftwareDirectorySizeResult MeasureInstallDirectory(
        InstalledSoftwareEntry entry,
        CancellationToken cancellationToken)
    {
        var path = NormalizeDirectory(entry.InstallLocation);
        if (!IsSpecificApplicationDirectory(path, out var validationError))
            return new SoftwareDirectorySizeResult(false, 0, 0, validationError);
        if (!Directory.Exists(path)) return new SoftwareDirectorySizeResult(false, 0, 0, "安装目录不存在或当前不可访问。");

        long bytes = 0;
        var files = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        bytes = checked(bytes + info.Length);
                        files++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) pending.Push(directory);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return new SoftwareDirectorySizeResult(true, bytes, files, $"已扫描 {files:N0} 个文件；共享组件与硬链接可能造成偏差。");
    }

    internal static SoftwareUninstallLaunchResult LaunchUninstaller(InstalledSoftwareEntry entry)
    {
        if (!entry.CanUninstall) return new SoftwareUninstallLaunchResult(false, null, "该项目没有可用的标准卸载入口。");
        try
        {
            if (entry.Kind == InstalledSoftwareKind.StorePackage)
                return LaunchPackageRemoval(entry);

            var command = entry.UninstallCommand;
            if (entry.IsWindowsInstaller && !string.IsNullOrWhiteSpace(entry.ProductCode))
                command = $"msiexec.exe /x {entry.ProductCode}";
            if (string.IsNullOrWhiteSpace(command))
                return new SoftwareUninstallLaunchResult(false, null, "卸载命令为空。请使用软件自身的卸载入口。");
            var parts = ParseCommandLine(command);
            if (parts.Count == 0) return new SoftwareUninstallLaunchResult(false, null, "无法解析软件登记的卸载命令。");
            if (Path.GetFileName(parts[0]).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(parts[0]).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                for (var index = 1; index < parts.Count; index++)
                {
                    if (parts[index].Equals("/I", StringComparison.OrdinalIgnoreCase) ||
                        parts[index].StartsWith("/I{", StringComparison.OrdinalIgnoreCase))
                        parts[index] = "/X" + parts[index][2..];
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.ExpandEnvironmentVariables(parts[0]),
                UseShellExecute = true,
                WorkingDirectory = Directory.Exists(entry.InstallLocation) ? entry.InstallLocation : Environment.CurrentDirectory
            };
            foreach (var argument in parts.Skip(1)) startInfo.ArgumentList.Add(Environment.ExpandEnvironmentVariables(argument));
            var process = Process.Start(startInfo);
            return process is null
                ? new SoftwareUninstallLaunchResult(false, null, "Windows 未能启动卸载程序。")
                : new SoftwareUninstallLaunchResult(true, process, "已启动软件提供的卸载程序。");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return new SoftwareUninstallLaunchResult(false, null, exception.Message);
        }
    }

    private static SoftwareUninstallLaunchResult LaunchPackageRemoval(InstalledSoftwareEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.PackageFullName) ||
            entry.PackageFullName.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '~')))
            return new SoftwareUninstallLaunchResult(false, null, "打包应用标识无效，已拒绝执行卸载。");
        var script = $"Remove-AppxPackage -Package '{entry.PackageFullName}' -ErrorAction Stop";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        var process = Process.Start(startInfo);
        return process is null
            ? new SoftwareUninstallLaunchResult(false, null, "Windows 未能启动打包应用卸载。")
            : new SoftwareUninstallLaunchResult(true, process, "已启动 Windows 打包应用卸载。 ");
    }

    internal static List<string> ParseCommandLine(string commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return result;
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero) return result;
        try
        {
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.ReadIntPtr(pointer, index * IntPtr.Size);
                result.Add(Marshal.PtrToStringUni(item) ?? string.Empty);
            }
        }
        finally
        {
            LocalFree(pointer);
        }
        return result;
    }

    internal static bool IsSpecificApplicationDirectory(string path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "该软件没有登记安装目录。";
            return false;
        }
        string fullPath;
        try { fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch
        {
            error = "安装目录格式无效。";
            return false;
        }
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullPath) ?? string.Empty),
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
        };
        if (blocked.Contains(fullPath))
        {
            error = "安装位置指向过于宽泛的系统目录，已拒绝扫描。";
            return false;
        }
        return true;
    }

    internal static string NormalizeIdentity(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static void ApplyUsage(InstalledSoftwareEntry[] entries) => ApplyUsage((IEnumerable<InstalledSoftwareEntry>)entries);

    private static string StringValue(RegistryKey key, string name) =>
        key.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? string.Empty;

    private static int IntValue(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            int number => number,
            long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            _ => int.TryParse(value?.ToString(), out var parsed) ? parsed : 0
        };
    }

    private static long? ReadEstimatedSize(RegistryKey key)
    {
        var value = key.GetValue("EstimatedSize");
        try
        {
            var kilobytes = value switch
            {
                int number => unchecked((uint)number),
                long number => number,
                _ => long.TryParse(value?.ToString(), out var parsed) ? parsed : -1
            };
            return kilobytes >= 0 ? checked(kilobytes * 1024L) : null;
        }
        catch (OverflowException) { return null; }
    }

    private static DateTimeOffset? ParseInstallDate(string value)
    {
        if (DateTime.TryParseExact(value.Trim(), ["yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd"], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var date))
            return new DateTimeOffset(date);
        return null;
    }

    private static DateTimeOffset? ParseJsonDate(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)) return date;
        if (value.StartsWith("/Date(", StringComparison.Ordinal) && value.EndsWith(")/", StringComparison.Ordinal))
        {
            var number = value[6..^2].Split('+', '-')[0];
            if (long.TryParse(number, out var milliseconds)) return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        return null;
    }

    private static string JsonString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return string.Empty;
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString();
    }

    private static bool JsonBool(JsonElement value, string property) =>
        value.TryGetProperty(property, out var element) &&
        (element.ValueKind == JsonValueKind.True || (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var parsed) && parsed));

    private static string FriendlyPublisher(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return string.Empty;
        foreach (var segment in publisher.Split(','))
        {
            var part = segment.Trim();
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return part[3..].Trim();
        }
        return publisher;
    }

    private static string ResolveDisplayIcon(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        var comma = expanded.LastIndexOf(',');
        if (comma > 2 && int.TryParse(expanded[(comma + 1)..].Trim(), out _)) expanded = expanded[..comma].Trim().Trim('"');
        return NormalizeFile(expanded);
    }

    private static string TryExecutableFromCommand(string command)
    {
        var parts = ParseCommandLine(Environment.ExpandEnvironmentVariables(command));
        if (parts.Count == 0) return string.Empty;
        return NormalizeFile(parts[0]);
    }

    private static string NormalizeDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))); }
        catch { return value.Trim().Trim('"'); }
    }

    private static string NormalizeFile(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'))); }
        catch { return value.Trim().Trim('"'); }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(NormalizeFile(first), NormalizeFile(second), StringComparison.OrdinalIgnoreCase);

    private static bool IsPathInside(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        var normalizedPath = NormalizeFile(path);
        var normalizedRoot = NormalizeDirectory(root) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static ImageSource? FindPackagedIcon(string installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation)) return null;
        try
        {
            var executable = Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            return SystemProgramIconProvider.GetIcon(executable ?? string.Empty);
        }
        catch { return null; }
    }

    private static string HiveText(RegistryHive hive) => hive == RegistryHive.CurrentUser ? "当前用户" : "所有用户";
    private static string ViewText(RegistryView view) => view == RegistryView.Registry32 ? "32 位" : "64 位";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
