using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;

namespace ScreenshotApp.FileWorkbench;

/// <summary>文件工作台的本地搜索与安全批处理规划。</summary>
internal static class FileWorkbenchService
{
    /// <summary>大目录只保留当前排序下最靠前的结果，避免百万级匹配拖垮界面与内存。</summary>
    internal const int MaxDisplayResultCount = 5000;

    internal static FileSearchResult Search(
        string rootDirectory,
        string keyword,
        string typeFilter,
        DateTime? modifiedAfter,
        IReadOnlyList<FileSortDescriptor> sortDescriptors,
        FileSearchMode searchMode,
        IProgress<FileSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (searchMode == FileSearchMode.FullRecursiveScan)
        {
            const string manualScanReason = "用户选择完整扫描";
            progress?.Report(FileSearchProgress.ForStatus("已跳过 Windows Search，正在完整递归扫描…", rootDirectory));
            return SearchRecursively(
                rootDirectory,
                keyword,
                typeFilter,
                modifiedAfter,
                sortDescriptors,
                progress,
                cancellationToken,
                manualScanReason);
        }

        var indexedAttempt = WindowsSearchFileSearchBackend.TrySearch(
            rootDirectory,
            keyword,
            typeFilter,
            modifiedAfter,
            sortDescriptors,
            progress,
            cancellationToken);
        if (indexedAttempt.Result is not null)
        {
            return indexedAttempt.Result;
        }

        var fallbackReason = indexedAttempt.FallbackReason ?? "Windows Search 当前不可用";
        progress?.Report(FileSearchProgress.ForStatus($"{fallbackReason}，正在切换到本地递归扫描…", rootDirectory));
        return SearchRecursively(
            rootDirectory,
            keyword,
            typeFilter,
            modifiedAfter,
            sortDescriptors,
            progress,
            cancellationToken,
            fallbackReason);
    }

    /// <summary>在 Windows Search 降级或用户选择完整扫描时执行；不会作为常驻后台任务。</summary>
    private static FileSearchResult SearchRecursively(
        string rootDirectory,
        string keyword,
        string typeFilter,
        DateTime? modifiedAfter,
        IReadOnlyList<FileSortDescriptor> sortDescriptors,
        IProgress<FileSearchProgress>? progress,
        CancellationToken cancellationToken,
        string scanReason)
    {
        var effectiveSortDescriptors = NormalizeSortDescriptors(sortDescriptors);
        var displayComparer = new FileWorkbenchItemComparer(effectiveSortDescriptors);
        var displayResults = new SortedSet<FileWorkbenchItem>(displayComparer);
        var filesScanned = 0L;
        var matchedFiles = 0L;
        var currentPath = rootDirectory;
        var progressStopwatch = Stopwatch.StartNew();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            filesScanned++;
            currentPath = path;
            try
            {
                var info = new FileInfo(path);
                if (!Matches(info, keyword, typeFilter, modifiedAfter))
                {
                    continue;
                }

                matchedFiles++;
                KeepBestMatch(info);
            }
            catch (UnauthorizedAccessException)
            {
                // 无权限的目录不影响其余位置搜索。
            }
            catch (IOException)
            {
                // 搜索过程中被移动或占用的文件直接跳过。
            }
            finally
            {
                ReportProgress(force: false);
            }
        }

        ReportProgress(force: true);
        return new FileSearchResult(
            SortResults(displayResults, effectiveSortDescriptors),
            filesScanned,
            matchedFiles,
            FileSearchBackend.RecursiveScan,
            scanReason,
            true);

        void KeepBestMatch(FileInfo info)
        {
            if (displayResults.Count < MaxDisplayResultCount)
            {
                displayResults.Add(FileWorkbenchItem.Create(info));
                return;
            }

            var worstDisplayedItem = displayResults.Max;
            if (worstDisplayedItem is null || CompareForDisplay(info, worstDisplayedItem, effectiveSortDescriptors) >= 0)
            {
                return;
            }

            displayResults.Remove(worstDisplayedItem);
            displayResults.Add(FileWorkbenchItem.Create(info));
        }

        void ReportProgress(bool force)
        {
            if (progress is null || (!force && progressStopwatch.Elapsed < TimeSpan.FromMilliseconds(220)))
            {
                return;
            }

            progressStopwatch.Restart();
            progress.Report(new FileSearchProgress(filesScanned, matchedFiles, currentPath));
        }
    }

    /// <summary>表头排序仅重排已加载的结果，不重新访问磁盘。</summary>
    internal static IReadOnlyList<FileWorkbenchItem> SortResults(
        IEnumerable<FileWorkbenchItem> items,
        IReadOnlyList<FileSortDescriptor> sortDescriptors)
        => OrderResults(items, sortDescriptors).ToArray();

    internal static IReadOnlyList<FileOperationPlan> CreatePlans(
        IEnumerable<FileWorkbenchItem> items,
        FileBatchOperation operation,
        string renamePrefix,
        int startNumber,
        int numberDigits,
        string newExtension,
        string destinationDirectory)
    {
        var orderedItems = items.OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        // 服务层同样限制补零位数，避免绕过界面调用时生成超长文件名。
        numberDigits = Math.Clamp(numberDigits, 1, 6);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<FileOperationPlan>(orderedItems.Length);
        for (var index = 0; index < orderedItems.Length; index++)
        {
            var item = orderedItems[index];
            var destination = operation switch
            {
                FileBatchOperation.Rename => Path.Combine(
                    Path.GetDirectoryName(item.FullPath) ?? string.Empty,
                    $"{renamePrefix}_{(startNumber + index).ToString($"D{numberDigits}", CultureInfo.InvariantCulture)}{item.Extension}"),
                FileBatchOperation.ChangeExtension => Path.Combine(
                    Path.GetDirectoryName(item.FullPath) ?? string.Empty,
                    $"{Path.GetFileNameWithoutExtension(item.FullPath)}{NormalizeExtension(newExtension)}"),
                FileBatchOperation.Classify => Path.Combine(destinationDirectory, GetCategory(item.Extension), item.FileName),
                FileBatchOperation.Move => Path.Combine(destinationDirectory, item.FileName),
                FileBatchOperation.PermanentDelete => string.Empty,
                _ => item.FullPath
            };

            if (operation != FileBatchOperation.PermanentDelete)
            {
                destination = GetAvailableDestination(destination, destinations);
                destinations.Add(destination);
            }

            plans.Add(new FileOperationPlan(item.FullPath, destination, operation, item.Size));
        }

        return plans;
    }

    internal static FileBatchExecutionResult Execute(IReadOnlyList<FileOperationPlan> plans)
    {
        var failures = new List<string>();
        var completedPaths = new List<string>();
        var completed = 0;
        foreach (var plan in plans)
        {
            try
            {
                if (plan.Operation == FileBatchOperation.PermanentDelete)
                {
                    if (!File.Exists(plan.SourcePath))
                    {
                        throw new FileNotFoundException("文件已经不存在。", plan.SourcePath);
                    }

                    File.Delete(plan.SourcePath);
                    completed++;
                    completedPaths.Add(plan.SourcePath);
                    continue;
                }

                if (string.Equals(plan.SourcePath, plan.DestinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(plan.DestinationPath) ?? throw new IOException("目标目录无效。"));
                File.Move(plan.SourcePath, plan.DestinationPath);
                completed++;
                completedPaths.Add(plan.SourcePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                failures.Add($"{Path.GetFileName(plan.SourcePath)}：{exception.Message}");
            }
        }

        return new FileBatchExecutionResult(completed, completedPaths, failures);
    }

    private static bool Matches(FileInfo info, string keyword, string typeFilter, DateTime? modifiedAfter)
    {
        if (!string.IsNullOrWhiteSpace(keyword) && !info.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(typeFilter, "全部", StringComparison.Ordinal) &&
            !string.Equals(GetCategory(info.Extension), typeFilter, StringComparison.Ordinal))
        {
            return false;
        }

        return modifiedAfter is null || info.LastWriteTime >= modifiedAfter.Value;
    }

    private static IOrderedEnumerable<FileWorkbenchItem> OrderResults(
        IEnumerable<FileWorkbenchItem> items,
        IReadOnlyList<FileSortDescriptor> sortDescriptors)
    {
        return items.OrderBy(item => item, new FileWorkbenchItemComparer(sortDescriptors));
    }

    private static int CompareForDisplay(
        FileInfo left,
        FileWorkbenchItem right,
        IReadOnlyList<FileSortDescriptor> sortDescriptors)
    {
        foreach (var descriptor in sortDescriptors)
        {
            var comparison = descriptor.Field switch
            {
                FileSortField.Name => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.FileName),
                FileSortField.Extension => StringComparer.OrdinalIgnoreCase.Compare(left.Extension, right.Extension),
                FileSortField.Size => left.Length.CompareTo(right.Size),
                FileSortField.Modified => left.LastWriteTime.CompareTo(right.ModifiedAt),
                _ => 0
            };

            if (comparison != 0)
            {
                return descriptor.Ascending ? comparison : -comparison;
            }
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left.FullName, right.FullPath);
    }

    private static IReadOnlyList<FileSortDescriptor> NormalizeSortDescriptors(IReadOnlyList<FileSortDescriptor> sortDescriptors)
        => sortDescriptors.Count == 0 ? DefaultSortDescriptors : sortDescriptors;

    private static readonly FileSortDescriptor[] DefaultSortDescriptors =
    {
        new(FileSortField.Name, true)
    };

    private sealed class FileWorkbenchItemComparer : IComparer<FileWorkbenchItem>
    {
        private readonly IReadOnlyList<FileSortDescriptor> _sortDescriptors;

        public FileWorkbenchItemComparer(IReadOnlyList<FileSortDescriptor> sortDescriptors)
        {
            _sortDescriptors = NormalizeSortDescriptors(sortDescriptors);
        }

        public int Compare(FileWorkbenchItem? left, FileWorkbenchItem? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            foreach (var descriptor in _sortDescriptors)
            {
                var comparison = descriptor.Field switch
                {
                    FileSortField.Name => StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName),
                    FileSortField.Extension => StringComparer.OrdinalIgnoreCase.Compare(left.Extension, right.Extension),
                    FileSortField.Size => left.Size.CompareTo(right.Size),
                    FileSortField.Modified => left.ModifiedAt.CompareTo(right.ModifiedAt),
                    _ => 0
                };

                if (comparison != 0)
                {
                    return descriptor.Ascending ? comparison : -comparison;
                }
            }

            return StringComparer.OrdinalIgnoreCase.Compare(left.FullPath, right.FullPath);
        }
    }

    internal static string GetCategory(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" => "图片",
            ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".flv" or ".wmv" => "视频",
            ".mp3" or ".wav" or ".m4a" or ".flac" or ".aac" or ".ogg" or ".opus" => "音频",
            ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".pdf" or ".txt" or ".md" => "文档",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "压缩包",
            _ => "其他"
        };
    }

    private static string NormalizeExtension(string extension) => string.IsNullOrWhiteSpace(extension)
        ? string.Empty
        : extension.Trim().StartsWith('.') ? extension.Trim() : $".{extension.Trim()}";

    private static string GetAvailableDestination(string destination, ISet<string> plannedDestinations)
    {
        if (!File.Exists(destination) && !plannedDestinations.Contains(destination))
        {
            return destination;
        }

        var directory = Path.GetDirectoryName(destination) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
            if (!File.Exists(candidate) && !plannedDestinations.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}

internal enum FileBatchOperation
{
    Rename,
    ChangeExtension,
    Classify,
    Move,
    PermanentDelete
}

internal enum FileSortField
{
    Name,
    Extension,
    Size,
    Modified
}

/// <summary>用户选中的一项排序规则；列表顺序就是多列排序的优先级。</summary>
internal readonly record struct FileSortDescriptor(FileSortField Field, bool Ascending);

/// <summary>文件搜索的阶段性进度；目录总量未知，因此展示已扫描数量与当前路径。</summary>
internal sealed record FileSearchProgress(long FilesScanned, long MatchedFiles, string CurrentPath, string? StatusText = null)
{
    public string SummaryText => StatusText ?? $"已扫描 {FilesScanned:N0} 个文件 · 匹配 {MatchedFiles:N0} 个";

    internal static FileSearchProgress ForStatus(string statusText, string currentPath) => new(0, 0, currentPath, statusText);
}

/// <summary>文件搜索结果只保留可流畅显示的部分；Windows Search 的超过上限结果只报告下界。</summary>
internal sealed record FileSearchResult(
    IReadOnlyList<FileWorkbenchItem> Items,
    long FilesScanned,
    long MatchedFiles,
    FileSearchBackend Backend,
    string? FallbackReason,
    bool IsMatchCountExact)
{
    public bool IsTruncated => !IsMatchCountExact || MatchedFiles > Items.Count;
}

internal enum FileSearchBackend
{
    WindowsSearch,
    RecursiveScan
}

/// <summary>智能搜索优先读取系统索引；完整扫描明确绕过索引，以换取当前目录的可读取文件覆盖。</summary>
internal enum FileSearchMode
{
    Smart,
    FullRecursiveScan
}

public sealed class FileWorkbenchItem : INotifyPropertyChanged
{
    private bool _isSelectedForDeletion;

    public FileWorkbenchItem(string fullPath, string fileName, string extension, long size, DateTime modifiedAt)
    {
        FullPath = fullPath;
        FileName = fileName;
        Extension = extension;
        Size = size;
        ModifiedAt = modifiedAt;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FullPath { get; }
    public string FileName { get; }
    public string Extension { get; }
    public long Size { get; }
    public DateTime ModifiedAt { get; }

    public bool IsSelectedForDeletion
    {
        get => _isSelectedForDeletion;
        set
        {
            if (_isSelectedForDeletion == value)
            {
                return;
            }

            _isSelectedForDeletion = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedForDeletion)));
        }
    }

    public string ExtensionText => Extension.TrimStart('.').ToUpperInvariant();

    public string SizeText => Size >= 1024L * 1024 * 1024
        ? $"{Size / 1024d / 1024d / 1024d:F2} GB"
        : Size >= 1024L * 1024
            ? $"{Size / 1024d / 1024d:F2} MB"
            : $"{Size / 1024d:F1} KB";

    public string ModifiedText => ModifiedAt.ToString("yyyy-MM-dd HH:mm");

    internal static FileWorkbenchItem Create(FileInfo info) => new(info.FullName, info.Name, info.Extension, info.Length, info.LastWriteTime);
}

internal sealed record FileOperationPlan(
    string SourcePath,
    string DestinationPath,
    FileBatchOperation Operation,
    long Size)
{
    public string SourceName => Path.GetFileName(SourcePath);
    public string DestinationText => Operation switch
    {
        FileBatchOperation.PermanentDelete => $"永久删除 · {FormatSize(Size)} · {SourcePath}",
        _ => Path.Combine(Path.GetFileName(Path.GetDirectoryName(DestinationPath) ?? string.Empty), Path.GetFileName(DestinationPath))
    };

    private static string FormatSize(long size) => size >= 1024L * 1024 * 1024
        ? $"{size / 1024d / 1024d / 1024d:F2} GB"
        : size >= 1024L * 1024
            ? $"{size / 1024d / 1024d:F2} MB"
            : $"{size / 1024d:F1} KB";
}

internal sealed record FileBatchExecutionResult(
    int CompletedCount,
    IReadOnlyList<string> CompletedPaths,
    IReadOnlyList<string> Failures);
