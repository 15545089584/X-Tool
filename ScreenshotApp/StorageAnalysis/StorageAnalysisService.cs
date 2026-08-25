using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Security;
using System.Collections.Concurrent;

namespace ScreenshotApp.StorageAnalysis;

/// <summary>
/// 仅在用户明确触发时扫描指定卷，聚合一级目录并保留固定数量的大文件，
/// 不建立常驻索引，也不把全量文件路径保存在内存中。
/// </summary>
internal static class StorageAnalysisService
{
    internal const int DefaultLargeFileLimit = 100;
    private static readonly TimeSpan ResultCacheLifetime = TimeSpan.FromMinutes(3);
    private static readonly ConcurrentDictionary<string, CachedStorageAnalysis> ResultCache = new(StringComparer.OrdinalIgnoreCase);

    internal static StorageAnalysisResult Analyze(
        StorageAnalysisTarget target,
        int largeFileLimit,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken,
        bool forceRefresh = false,
        bool allowEverything = true)
    {
        if (string.IsNullOrWhiteSpace(target.RootPath) || !Directory.Exists(target.RootPath))
        {
            throw new DirectoryNotFoundException("所选本地卷已不可用，请返回存储概览后刷新。");
        }

        var normalizedRoot = Path.GetFullPath(target.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(normalizedRoot) || !Directory.Exists(normalizedRoot))
        {
            throw new IOException("无法读取所选本地卷的根目录。");
        }

        if (!forceRefresh && ResultCache.TryGetValue(normalizedRoot, out var cached) &&
            DateTime.Now - cached.CachedAt <= ResultCacheLifetime)
        {
            progress?.Report(new StorageScanProgress(cached.Result.FilesScanned, cached.Result.DirectoriesScanned,
                cached.Result.SkippedEntries, "正在读取最近一次分析结果…", "缓存结果"));
            return cached.Result with { IsCached = true };
        }

        var volumeRoot = Path.GetPathRoot(normalizedRoot);
        if (allowEverything && string.Equals(normalizedRoot, volumeRoot, StringComparison.OrdinalIgnoreCase) &&
            EverythingStorageIndexClient.TryAnalyze(normalizedRoot, largeFileLimit, progress, cancellationToken, out var indexed) &&
            indexed is not null && HasPlausibleIndexCoverage(target, indexed))
        {
            var indexedResult = BuildIndexedResult(target, indexed);
            ResultCache[normalizedRoot] = new CachedStorageAnalysis(indexedResult, DateTime.Now);
            return indexedResult;
        }

        var state = new ScanState(Math.Clamp(largeFileLimit, 1, 500), progress);
        var usages = new ConcurrentBag<MutableDirectoryUsage>();
        var rootFiles = new MutableDirectoryUsage("根目录文件", normalizedRoot, isRootFiles: true);
        var topDirectories = new List<(string Path, string Name)>();

        try
        {
            foreach (var entry in EnumerateEntries(normalizedRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                state.SetCurrentPath(entry.Path);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    state.RecordSkipped(rootFiles);
                    continue;
                }

                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    var name = Path.GetFileName(entry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    topDirectories.Add((entry.Path, string.IsNullOrWhiteSpace(name) ? entry.Path : name));
                }
                else
                {
                    ScanFile(entry, rootFiles, state, cancellationToken);
                }
            }
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            state.RecordSkipped(rootFiles);
        }

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            // 机械盘和实时防护场景中过多随机元数据请求反而会变慢，因此只做温和并行。
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4)
        };
        Parallel.ForEach(topDirectories, parallelOptions, directory =>
        {
            var usage = new MutableDirectoryUsage(directory.Name, directory.Path, isRootFiles: false);
            ScanDirectory(directory.Path, usage, state, cancellationToken);
            if (usage.LogicalBytes > 0 || usage.SkippedEntries > 0) usages.Add(usage);
        });

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

        var result = new StorageAnalysisResult(
            target,
            directoryUsages,
            largeFiles,
            state.FilesScanned,
            state.DirectoriesScanned,
            state.SkippedEntries,
            totalScannedBytes,
            completedAt,
            "优化原生扫描",
            false);
        ResultCache[normalizedRoot] = new CachedStorageAnalysis(result, DateTime.Now);
        return result;
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
                usage.DirectoriesScanned++;
                state.RecordDirectory();
                foreach (var entry in EnumerateEntries(directoryPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    state.SetCurrentPath(entry.Path);
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        state.RecordSkipped(usage);
                        continue;
                    }

                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        pendingDirectories.Push(entry.Path);
                    }
                    else
                    {
                        ScanFile(entry, usage, state, cancellationToken);
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
        NativeFileSystemEntry file,
        MutableDirectoryUsage usage,
        ScanState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = Math.Max(0, file.Length);
        usage.LogicalBytes += length;
        usage.FilesScanned++;
        state.RecordFile(file.Path, length);
    }

    private static IEnumerable<NativeFileSystemEntry> EnumerateEntries(string directoryPath)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };
        return new FileSystemEnumerable<NativeFileSystemEntry>(directoryPath,
            (ref FileSystemEntry entry) => new NativeFileSystemEntry(entry.ToFullPath(), entry.Attributes, entry.Length), options);
    }

    private static StorageAnalysisResult BuildIndexedResult(StorageAnalysisTarget target, EverythingStorageAnalysis indexed)
    {
        var usages = indexed.DirectoryUsages
            .OrderByDescending(item => item.Bytes)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => new StorageDirectoryUsage(
                item.Name, item.FullPath, item.Bytes, item.Files, item.Directories, 0,
                indexed.TotalBytes <= 0 ? 0 : Math.Clamp(item.Bytes / (double)indexed.TotalBytes, 0, 1),
                MutableDirectoryUsage.AccentAt(index), item.IsRootFiles))
            .ToArray();
        return new StorageAnalysisResult(target, usages, indexed.LargeFiles,
            indexed.FilesScanned, indexed.DirectoriesScanned, 0, indexed.TotalBytes,
            DateTime.Now, "Everything 索引", false);
    }

    private static bool HasPlausibleIndexCoverage(StorageAnalysisTarget target, EverythingStorageAnalysis indexed)
    {
        var usedBytes = Math.Max(0, target.TotalBytes - target.FreeBytes);
        // 空卷允许返回空索引；非空卷若索引只覆盖很小一部分，说明存在排除规则或索引尚未就绪。
        return usedBytes == 0 || indexed.TotalBytes >= usedBytes * 0.60;
    }

    private static bool IsExpectedFileSystemException(Exception exception) => exception is UnauthorizedAccessException
        or IOException
        or SecurityException
        or NotSupportedException
        or ArgumentException;

    private sealed class MutableDirectoryUsage
    {
        private static readonly string[] AccentPalette = { "#4D7CFE", "#16B99B", "#A66CE5", "#F3A847", "#4D9CB5", "#E16670" };
        public static string AccentAt(int index) => AccentPalette[index % AccentPalette.Length];

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
            AccentAt(index),
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

        private long _filesScanned;
        private long _directoriesScanned;
        private long _skippedEntries;
        private readonly object _largeFilesSync = new();
        private readonly object _progressSync = new();

        public long FilesScanned => Interlocked.Read(ref _filesScanned);
        public long DirectoriesScanned
        {
            get => Interlocked.Read(ref _directoriesScanned);
            set => Interlocked.Exchange(ref _directoriesScanned, value);
        }
        public long SkippedEntries => Interlocked.Read(ref _skippedEntries);

        public void SetCurrentPath(string path)
        {
            lock (_progressSync) _currentPath = path;
            Report(force: false);
        }

        public void RecordSkipped(MutableDirectoryUsage usage)
        {
            usage.SkippedEntries++;
            Interlocked.Increment(ref _skippedEntries);
            Report(force: false);
        }

        public void RecordFile(string path, long size)
        {
            Interlocked.Increment(ref _filesScanned);
            if (size <= 0)
            {
                Report(force: false);
                return;
            }

            lock (_largeFilesSync)
            {
                var entry = new StorageLargeFile(path, size);
                if (_largeFiles.Count < _largeFileLimit)
                    _largeFiles.Enqueue(entry, size);
                else if (_largeFiles.TryPeek(out _, out var smallestSize) && size > smallestSize)
                {
                    _largeFiles.Dequeue();
                    _largeFiles.Enqueue(entry, size);
                }
            }

            Report(force: false);
        }

        public void RecordDirectory() => Interlocked.Increment(ref _directoriesScanned);

        public IReadOnlyList<StorageLargeFile> GetLargeFiles()
        {
            lock (_largeFilesSync)
                return _largeFiles.UnorderedItems.Select(item => item.Element)
                    .OrderByDescending(item => item.Size).ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public void Report(bool force)
        {
            if (_progress is null) return;
            lock (_progressSync)
            {
                if (!force && _progressStopwatch.Elapsed < TimeSpan.FromMilliseconds(220)) return;
                _progressStopwatch.Restart();
                _progress.Report(new StorageScanProgress(FilesScanned, DirectoriesScanned, SkippedEntries, _currentPath, "优化原生扫描"));
            }
        }
    }

    private sealed record CachedStorageAnalysis(StorageAnalysisResult Result, DateTime CachedAt);
    private readonly record struct NativeFileSystemEntry(string Path, FileAttributes Attributes, long Length);
}

public sealed record StorageAnalysisTarget(string RootPath, string Title, long TotalBytes, long FreeBytes)
{
    public string CapacityText => $"{StorageAnalysisDisplay.FormatCapacity(TotalBytes)} 容量 · {StorageAnalysisDisplay.FormatCapacity(FreeBytes)} 可用";
}

public sealed record StorageScanProgress(long FilesScanned, long DirectoriesScanned, long SkippedEntries, string CurrentPath, string SourceText)
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
    DateTime CompletedAt,
    string SourceText,
    bool IsCached)
{
    public string ScannedBytesText => StorageAnalysisDisplay.FormatCapacity(ScannedBytes);
    public string SummaryText => $"{SourceText}{(IsCached ? " · 缓存" : string.Empty)} · 已扫描 {FilesScanned:N0} 个文件 · {DirectoriesScanned:N0} 个目录 · 跳过 {SkippedEntries:N0} 项 · 完成于 {CompletedAt:HH:mm:ss}";
    public string NoticeText => SkippedEntries == 0
        ? SourceText.StartsWith("Everything", StringComparison.Ordinal)
            ? "结果来自本机已有 Everything 索引；索引排除规则可能影响统计范围。显示的是文件逻辑大小，不代表磁盘实际分配空间。"
            : "显示的是已扫描文件的逻辑大小；文件系统保留空间、硬链接、压缩和稀疏文件可能使其与卷已用空间不同。"
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
