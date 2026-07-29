using System.Diagnostics;
using System.IO;
using System.Security;

namespace ScreenshotApp.StorageAnalysis;

/// <summary>
/// 仅在用户明确触发时扫描指定卷，聚合一级目录并保留固定数量的大文件，
/// 不建立常驻索引，也不把全量文件路径保存在内存中。
/// </summary>
internal static class StorageAnalysisService
{
    internal const int DefaultLargeFileLimit = 100;

    internal static StorageAnalysisResult Analyze(
        StorageAnalysisTarget target,
        int largeFileLimit,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.RootPath) || !Directory.Exists(target.RootPath))
        {
            throw new DirectoryNotFoundException("所选本地卷已不可用，请返回存储概览后刷新。");
        }

        var normalizedRoot = Path.GetPathRoot(Path.GetFullPath(target.RootPath));
        if (string.IsNullOrWhiteSpace(normalizedRoot) || !Directory.Exists(normalizedRoot))
        {
            throw new IOException("无法读取所选本地卷的根目录。");
        }

        var state = new ScanState(Math.Clamp(largeFileLimit, 1, 500), progress);
        var usages = new List<MutableDirectoryUsage>();
        var rootFiles = new MutableDirectoryUsage("根目录文件", normalizedRoot, isRootFiles: true);

        try
        {
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(normalizedRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                state.SetCurrentPath(entryPath);
                try
                {
                    var attributes = File.GetAttributes(entryPath);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        state.RecordSkipped(rootFiles);
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var name = Path.GetFileName(entryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        var usage = new MutableDirectoryUsage(string.IsNullOrWhiteSpace(name) ? entryPath : name, entryPath, isRootFiles: false);
                        ScanDirectory(entryPath, usage, state, cancellationToken);
                        if (usage.LogicalBytes > 0 || usage.SkippedEntries > 0)
                        {
                            usages.Add(usage);
                        }
                    }
                    else
                    {
                        ScanFile(entryPath, rootFiles, state, cancellationToken);
                    }
                }
                catch (Exception exception) when (IsExpectedFileSystemException(exception))
                {
                    state.RecordSkipped(rootFiles);
                }
            }
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            state.RecordSkipped(rootFiles);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (rootFiles.LogicalBytes > 0 || rootFiles.SkippedEntries > 0)
        {
            usages.Add(rootFiles);
        }

        var totalScannedBytes = usages.Sum(item => item.LogicalBytes);
        var directoryUsages = usages
            .OrderByDescending(item => item.LogicalBytes)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => item.ToEntry(totalScannedBytes, index))
            .ToArray();
        var largeFiles = state.GetLargeFiles();
        var completedAt = DateTime.Now;
        state.Report(force: true);

        return new StorageAnalysisResult(
            target,
            directoryUsages,
            largeFiles,
            state.FilesScanned,
            state.DirectoriesScanned,
            state.SkippedEntries,
            totalScannedBytes,
            completedAt);
    }

    private static void ScanDirectory(
        string rootDirectory,
        MutableDirectoryUsage usage,
        ScanState state,
        CancellationToken cancellationToken)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootDirectory);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryPath = pendingDirectories.Pop();
            state.SetCurrentPath(directoryPath);

            try
            {
                var attributes = File.GetAttributes(directoryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    state.RecordSkipped(usage);
                    continue;
                }

                usage.DirectoriesScanned++;
                state.DirectoriesScanned++;
                foreach (var entryPath in Directory.EnumerateFileSystemEntries(directoryPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    state.SetCurrentPath(entryPath);

                    try
                    {
                        var entryAttributes = File.GetAttributes(entryPath);
                        if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                        {
                            state.RecordSkipped(usage);
                            continue;
                        }

                        if ((entryAttributes & FileAttributes.Directory) != 0)
                        {
                            pendingDirectories.Push(entryPath);
                        }
                        else
                        {
                            ScanFile(entryPath, usage, state, cancellationToken);
                        }
                    }
                    catch (Exception exception) when (IsExpectedFileSystemException(exception))
                    {
                        state.RecordSkipped(usage);
                    }
                }
            }
            catch (Exception exception) when (IsExpectedFileSystemException(exception))
            {
                state.RecordSkipped(usage);
            }
        }
    }

    private static void ScanFile(
        string filePath,
        MutableDirectoryUsage usage,
        ScanState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(filePath);
        var length = Math.Max(0, info.Length);
        usage.LogicalBytes += length;
        usage.FilesScanned++;
        state.FilesScanned++;
        state.AddLargeFile(filePath, length);
    }

    private static bool IsExpectedFileSystemException(Exception exception) => exception is UnauthorizedAccessException
        or IOException
        or SecurityException
        or NotSupportedException
        or ArgumentException;

    private sealed class MutableDirectoryUsage
    {
        private static readonly string[] AccentPalette = { "#4D7CFE", "#16B99B", "#A66CE5", "#F3A847", "#4D9CB5", "#E16670" };

        public MutableDirectoryUsage(string name, string fullPath, bool isRootFiles)
        {
            Name = name;
            FullPath = fullPath;
            IsRootFiles = isRootFiles;
        }

        public string Name { get; }
        public string FullPath { get; }
        public bool IsRootFiles { get; }
        public long LogicalBytes { get; set; }
        public long FilesScanned { get; set; }
        public long DirectoriesScanned { get; set; }
        public long SkippedEntries { get; set; }

        public StorageDirectoryUsage ToEntry(long totalScannedBytes, int index) => new(
            Name,
            FullPath,
            LogicalBytes,
            FilesScanned,
            DirectoriesScanned,
            SkippedEntries,
            totalScannedBytes <= 0 ? 0 : Math.Clamp(LogicalBytes / (double)totalScannedBytes, 0, 1),
            AccentPalette[index % AccentPalette.Length],
            IsRootFiles);
    }

    private sealed class ScanState
    {
        private readonly int _largeFileLimit;
        private readonly PriorityQueue<StorageLargeFile, long> _largeFiles = new();
        private readonly IProgress<StorageScanProgress>? _progress;
        private readonly Stopwatch _progressStopwatch = Stopwatch.StartNew();
        private string _currentPath = string.Empty;

        public ScanState(int largeFileLimit, IProgress<StorageScanProgress>? progress)
        {
            _largeFileLimit = largeFileLimit;
            _progress = progress;
        }

        public long FilesScanned { get; set; }
        public long DirectoriesScanned { get; set; }
        public long SkippedEntries { get; private set; }

        public void SetCurrentPath(string path)
        {
            _currentPath = path;
            Report(force: false);
        }

        public void RecordSkipped(MutableDirectoryUsage usage)
        {
            usage.SkippedEntries++;
            SkippedEntries++;
            Report(force: false);
        }

        public void AddLargeFile(string path, long size)
        {
            if (size <= 0)
            {
                Report(force: false);
                return;
            }

            var entry = new StorageLargeFile(path, size);
            if (_largeFiles.Count < _largeFileLimit)
            {
                _largeFiles.Enqueue(entry, size);
            }
            else if (_largeFiles.TryPeek(out _, out var smallestSize) && size > smallestSize)
            {
                _largeFiles.Dequeue();
                _largeFiles.Enqueue(entry, size);
            }

            Report(force: false);
        }

        public IReadOnlyList<StorageLargeFile> GetLargeFiles() => _largeFiles.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(item => item.Size)
            .ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        public void Report(bool force)
        {
            if (_progress is null || (!force && _progressStopwatch.Elapsed < TimeSpan.FromMilliseconds(220)))
            {
                return;
            }

            _progressStopwatch.Restart();
            _progress.Report(new StorageScanProgress(FilesScanned, DirectoriesScanned, SkippedEntries, _currentPath));
        }
    }
}

public sealed record StorageAnalysisTarget(string RootPath, string Title, long TotalBytes, long FreeBytes)
{
    public string CapacityText => $"{StorageAnalysisDisplay.FormatCapacity(TotalBytes)} 容量 · {StorageAnalysisDisplay.FormatCapacity(FreeBytes)} 可用";
}

public sealed record StorageScanProgress(long FilesScanned, long DirectoriesScanned, long SkippedEntries, string CurrentPath)
{
    public string SummaryText => $"已扫描 {FilesScanned:N0} 个文件 · {DirectoriesScanned:N0} 个目录 · 跳过 {SkippedEntries:N0} 项";
}

public sealed record StorageAnalysisResult(
    StorageAnalysisTarget Target,
    IReadOnlyList<StorageDirectoryUsage> DirectoryUsages,
    IReadOnlyList<StorageLargeFile> LargeFiles,
    long FilesScanned,
    long DirectoriesScanned,
    long SkippedEntries,
    long ScannedBytes,
    DateTime CompletedAt)
{
    public string ScannedBytesText => StorageAnalysisDisplay.FormatCapacity(ScannedBytes);
    public string SummaryText => $"已扫描 {FilesScanned:N0} 个文件 · {DirectoriesScanned:N0} 个目录 · 跳过 {SkippedEntries:N0} 项 · 完成于 {CompletedAt:HH:mm:ss}";
    public string NoticeText => SkippedEntries == 0
        ? "显示的是已扫描文件的逻辑大小；文件系统保留空间、硬链接、压缩和稀疏文件可能使其与卷已用空间不同。"
        : $"有 {SkippedEntries:N0} 项因权限、链接或扫描期间变动未计入；文件逻辑大小可能与卷已用空间不同。";
}

public sealed record StorageDirectoryUsage(
    string Name,
    string FullPath,
    long LogicalBytes,
    long FilesScanned,
    long DirectoriesScanned,
    long SkippedEntries,
    double UsageRatio,
    string Accent,
    bool IsRootFiles)
{
    public string SizeText => StorageAnalysisDisplay.FormatCapacity(LogicalBytes);
    public string DetailText => IsRootFiles
        ? $"根目录文件 · {FilesScanned:N0} 个文件"
        : $"{FilesScanned:N0} 个文件 · {DirectoriesScanned:N0} 个目录";
    public string ScanStateText => SkippedEntries == 0 ? "已完整扫描可访问内容" : $"跳过 {SkippedEntries:N0} 项";
    public bool CanOpenInFileWorkbench => !IsRootFiles && Directory.Exists(FullPath);
}

public sealed record StorageLargeFile(string FullPath, long Size)
{
    public string FileName => Path.GetFileName(FullPath);
    public string ParentDirectory => Path.GetDirectoryName(FullPath) ?? string.Empty;
    public string SizeText => StorageAnalysisDisplay.FormatCapacity(Size);
    public string ParentDirectoryText => string.IsNullOrWhiteSpace(ParentDirectory) ? "所在目录不可用" : ParentDirectory;
}

internal static class StorageAnalysisDisplay
{
    public static string FormatCapacity(long value)
    {
        if (value <= 0) return "0 B";
        if (value >= 1024L * 1024 * 1024 * 1024) return $"{value / 1024d / 1024d / 1024d / 1024d:F1} TB";
        if (value >= 1024L * 1024 * 1024) return $"{value / 1024d / 1024d / 1024d:F1} GB";
        if (value >= 1024L * 1024) return $"{value / 1024d / 1024d:F1} MB";
        return $"{value / 1024d:F1} KB";
    }
}
