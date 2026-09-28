using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ScreenshotApp.SystemTools;

internal enum SoftwareResidualKind
{
    Directory,
    Registry
}

internal sealed class SoftwareResidualCandidate : INotifyPropertyChanged
{
    private bool _isSelected;

    public required string StableId { get; init; }
    public required SoftwareResidualKind Kind { get; init; }
    public required string Location { get; init; }
    public required string Reason { get; init; }
    public RegistryHive? RegistryHive { get; init; }
    public RegistryView? RegistryView { get; init; }
    public string RegistrySubKey { get; init; } = string.Empty;
    public long? EstimatedBytes { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public string KindText => Kind == SoftwareResidualKind.Directory ? "文件夹" : "注册表";
    public string SizeText => Kind == SoftwareResidualKind.Directory ? InstalledSoftwareEntry.FormatBytes(EstimatedBytes) : "—";
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed record SoftwareResidualDeleteResult(int Deleted, IReadOnlyList<string> Failures);

/// <summary>只发现与软件名称、发布者或原卸载项明确对应的候选；所有候选默认不选中。</summary>
internal static class SoftwareResidualService
{
    internal static IReadOnlyList<SoftwareResidualCandidate> FindCandidates(
        InstalledSoftwareEntry entry,
        bool includeInstallDirectory,
        CancellationToken cancellationToken)
    {
        var candidates = new List<SoftwareResidualCandidate>();
        if (includeInstallDirectory &&
            InstalledSoftwareService.IsSpecificApplicationDirectory(entry.InstallLocation, out _) &&
            Directory.Exists(entry.InstallLocation))
        {
            candidates.Add(DirectoryCandidate(entry.InstallLocation, "软件登记的原安装目录", cancellationToken));
        }

        var appIdentity = InstalledSoftwareService.NormalizeIdentity(RemoveVersionSuffix(entry.Name));
        var publisherIdentity = InstalledSoftwareService.NormalizeIdentity(entry.Publisher);
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddExactMatchingDirectories(candidates, root, appIdentity, "名称与软件完全对应的应用数据目录", cancellationToken);
            if (string.IsNullOrWhiteSpace(publisherIdentity)) continue;
            try
            {
                foreach (var publisherDirectory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!InstalledSoftwareService.NormalizeIdentity(Path.GetFileName(publisherDirectory)).Equals(publisherIdentity, StringComparison.OrdinalIgnoreCase)) continue;
                    AddExactMatchingDirectories(candidates, publisherDirectory, appIdentity, "发布者目录内与软件名称完全对应的数据目录", cancellationToken);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        if (includeInstallDirectory && entry.RegistryHive.HasValue && entry.RegistryView.HasValue && !string.IsNullOrWhiteSpace(entry.RegistrySubKey))
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(entry.RegistryHive.Value, entry.RegistryView.Value);
                using var key = baseKey.OpenSubKey(entry.RegistrySubKey, writable: false);
                if (key is not null)
                {
                    candidates.Add(new SoftwareResidualCandidate
                    {
                        StableId = $"registry:{entry.RegistryHive}:{entry.RegistryView}:{entry.RegistrySubKey}",
                        Kind = SoftwareResidualKind.Registry,
                        Location = $"{entry.RegistryHive}\\{entry.RegistrySubKey}",
                        Reason = "卸载完成后仍存在的原卸载登记项",
                        RegistryHive = entry.RegistryHive,
                        RegistryView = entry.RegistryView,
                        RegistrySubKey = entry.RegistrySubKey
                    });
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        AddProductRegistryCandidates(candidates, entry, appIdentity, publisherIdentity, cancellationToken);
        return candidates
            .GroupBy(candidate => candidate.StableId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.Kind)
            .ThenBy(candidate => candidate.Location, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    internal static SoftwareResidualDeleteResult DeleteSelected(
        IReadOnlyList<SoftwareResidualCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        var failures = new List<string>();
        foreach (var candidate in candidates.Where(item => item.IsSelected))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (candidate.Kind == SoftwareResidualKind.Directory)
                {
                    if (!InstalledSoftwareService.IsSpecificApplicationDirectory(candidate.Location, out var validationError))
                        throw new InvalidOperationException(validationError);
                    if (Directory.Exists(candidate.Location))
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                            candidate.Location,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                    }
                }
                else
                {
                    if (!candidate.RegistryHive.HasValue || !candidate.RegistryView.HasValue || string.IsNullOrWhiteSpace(candidate.RegistrySubKey))
                        throw new InvalidOperationException("注册表候选缺少定位信息。");
                    using var baseKey = RegistryKey.OpenBaseKey(candidate.RegistryHive.Value, candidate.RegistryView.Value);
                    baseKey.DeleteSubKeyTree(candidate.RegistrySubKey, throwOnMissingSubKey: false);
                }
                deleted++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException or OperationCanceledException)
            {
                failures.Add($"{candidate.Location}：{exception.Message}");
            }
        }
        return new SoftwareResidualDeleteResult(deleted, failures);
    }

    private static void AddExactMatchingDirectories(
        List<SoftwareResidualCandidate> output,
        string root,
        string appIdentity,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appIdentity)) return;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!InstalledSoftwareService.NormalizeIdentity(Path.GetFileName(directory)).Equals(appIdentity, StringComparison.OrdinalIgnoreCase)) continue;
                output.Add(DirectoryCandidate(directory, reason, cancellationToken));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private static void AddProductRegistryCandidates(
        List<SoftwareResidualCandidate> output,
        InstalledSoftwareEntry entry,
        string appIdentity,
        string publisherIdentity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appIdentity)) return;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var software = baseKey.OpenSubKey("Software", writable: false);
                if (software is null) continue;
                foreach (var topName in software.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var topIdentity = InstalledSoftwareService.NormalizeIdentity(topName);
                    if (topIdentity.Equals(appIdentity, StringComparison.OrdinalIgnoreCase))
                    {
                        AddRegistryCandidate(output, hive, view, $@"Software\{topName}", "注册表项名称与软件完全对应");
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(publisherIdentity) || !topIdentity.Equals(publisherIdentity, StringComparison.OrdinalIgnoreCase)) continue;
                    using var publisherKey = software.OpenSubKey(topName, writable: false);
                    if (publisherKey is null) continue;
                    foreach (var productName in publisherKey.GetSubKeyNames())
                    {
                        if (InstalledSoftwareService.NormalizeIdentity(productName).Equals(appIdentity, StringComparison.OrdinalIgnoreCase))
                            AddRegistryCandidate(output, hive, view, $@"Software\{topName}\{productName}", "发布者项下与软件名称完全对应的注册表项");
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
    }

    private static void AddRegistryCandidate(
        List<SoftwareResidualCandidate> output,
        RegistryHive hive,
        RegistryView view,
        string subKey,
        string reason)
    {
        output.Add(new SoftwareResidualCandidate
        {
            StableId = $"registry:{hive}:{view}:{subKey}",
            Kind = SoftwareResidualKind.Registry,
            Location = $"{hive}\\{subKey} ({(view == RegistryView.Registry32 ? "32 位" : "64 位")})",
            Reason = reason,
            RegistryHive = hive,
            RegistryView = view,
            RegistrySubKey = subKey
        });
    }

    private static SoftwareResidualCandidate DirectoryCandidate(string path, string reason, CancellationToken cancellationToken) => new()
    {
        StableId = $"directory:{Path.GetFullPath(path)}",
        Kind = SoftwareResidualKind.Directory,
        Location = Path.GetFullPath(path),
        Reason = reason,
        EstimatedBytes = EstimateDirectorySize(path, cancellationToken)
    };

    private static long? EstimateDirectorySize(string path, CancellationToken cancellationToken)
    {
        long size = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { size = checked(size + new FileInfo(file).Length); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) pending.Push(directory);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
            }
            return size;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return size > 0 ? size : null;
        }
    }

    private static string RemoveVersionSuffix(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        while (parts.Length > 1 && parts[^1].Any(char.IsDigit) && parts[^1].All(character => char.IsDigit(character) || character is '.' or '-' or 'v' or 'V'))
            parts = parts[..^1];
        return string.Join(' ', parts);
    }
}
