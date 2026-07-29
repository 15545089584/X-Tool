using System.Globalization;
using System.IO;
using System.Diagnostics;

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
        FileSortField sortField,
        bool sortAscending,
        IProgress<FileSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var displayComparer = new FileWorkbenchItemComparer(sortField, sortAscending);
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
        return new FileSearchResult(SortResults(displayResults, sortField, sortAscending), filesScanned, matchedFiles);

        void KeepBestMatch(FileInfo info)
        {
            if (displayResults.Count < MaxDisplayResultCount)
            {
                displayResults.Add(FileWorkbenchItem.Create(info));
                return;
            }

            var worstDisplayedItem = displayResults.Max;
            if (worstDisplayedItem is null || CompareForDisplay(info, worstDisplayedItem, sortField, sortAscending) >= 0)
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
    internal static IReadOnlyList<FileWorkbenchItem> SortResults(IEnumerable<FileWorkbenchItem> items, FileSortField sortField, bool sortAscending)
        => OrderResults(items, sortField, sortAscending).ToArray();

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
                _ => item.FullPath
            };

            destination = GetAvailableDestination(destination, destinations);
            destinations.Add(destination);
            plans.Add(new FileOperationPlan(item.FullPath, destination));
        }

        return plans;
    }

    internal static FileBatchExecutionResult Execute(IReadOnlyList<FileOperationPlan> plans)
    {
        var failures = new List<string>();
        var completed = 0;
        foreach (var plan in plans)
        {
            try
            {
                if (string.Equals(plan.SourcePath, plan.DestinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(plan.DestinationPath) ?? throw new IOException("目标目录无效。"));
                File.Move(plan.SourcePath, plan.DestinationPath);
                completed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(plan.SourcePath)}：{exception.Message}");
            }
        }

        return new FileBatchExecutionResult(completed, failures);
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

    private static IOrderedEnumerable<FileWorkbenchItem> OrderResults(IEnumerable<FileWorkbenchItem> items, FileSortField field, bool ascending)
    {
        return items.OrderBy(item => item, new FileWorkbenchItemComparer(field, ascending));
    }

    private static int CompareForDisplay(FileInfo left, FileWorkbenchItem right, FileSortField field, bool ascending)
    {
        var comparison = field switch
        {
            FileSortField.Name => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.FileName),
            FileSortField.Extension => StringComparer.OrdinalIgnoreCase.Compare(left.Extension, right.Extension),
            FileSortField.Size => left.Length.CompareTo(right.Size),
            FileSortField.Modified => left.LastWriteTime.CompareTo(right.ModifiedAt),
            _ => 0
        };

        if (comparison == 0)
        {
            comparison = StringComparer.OrdinalIgnoreCase.Compare(left.FullName, right.FullPath);
        }

        return ascending ? comparison : -comparison;
    }

    private sealed class FileWorkbenchItemComparer : IComparer<FileWorkbenchItem>
    {
        private readonly FileSortField _field;
        private readonly bool _ascending;

        public FileWorkbenchItemComparer(FileSortField field, bool ascending)
        {
            _field = field;
            _ascending = ascending;
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

            var comparison = _field switch
            {
                FileSortField.Name => StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName),
                FileSortField.Extension => StringComparer.OrdinalIgnoreCase.Compare(left.Extension, right.Extension),
                FileSortField.Size => left.Size.CompareTo(right.Size),
                FileSortField.Modified => left.ModifiedAt.CompareTo(right.ModifiedAt),
                _ => 0
            };

            if (comparison == 0)
            {
                comparison = StringComparer.OrdinalIgnoreCase.Compare(left.FullPath, right.FullPath);
            }

            return _ascending ? comparison : -comparison;
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
    Move
}

internal enum FileSortField
{
    Name,
    Extension,
    Size,
    Modified
}

/// <summary>文件搜索的阶段性进度；目录总量未知，因此展示已扫描数量与当前路径。</summary>
internal sealed record FileSearchProgress(long FilesScanned, long MatchedFiles, string CurrentPath)
{
    public string SummaryText => $"已扫描 {FilesScanned:N0} 个文件 · 匹配 {MatchedFiles:N0} 个";
}

/// <summary>文件搜索结果只保留可流畅显示的部分，仍返回完整匹配计数供用户缩小筛选范围。</summary>
internal sealed record FileSearchResult(IReadOnlyList<FileWorkbenchItem> Items, long FilesScanned, long MatchedFiles)
{
    public bool IsTruncated => MatchedFiles > Items.Count;
}

public sealed record FileWorkbenchItem(string FullPath, string FileName, string Extension, long Size, DateTime ModifiedAt)
{
    public string ExtensionText => Extension.TrimStart('.').ToUpperInvariant();

    public string SizeText => Size >= 1024L * 1024 * 1024
        ? $"{Size / 1024d / 1024d / 1024d:F2} GB"
        : Size >= 1024L * 1024
            ? $"{Size / 1024d / 1024d:F2} MB"
            : $"{Size / 1024d:F1} KB";

    public string ModifiedText => ModifiedAt.ToString("yyyy-MM-dd HH:mm");

    internal static FileWorkbenchItem Create(FileInfo info) => new(info.FullName, info.Name, info.Extension, info.Length, info.LastWriteTime);
}

public sealed record FileOperationPlan(string SourcePath, string DestinationPath)
{
    public string SourceName => Path.GetFileName(SourcePath);
    public string DestinationText => Path.Combine(Path.GetFileName(Path.GetDirectoryName(DestinationPath) ?? string.Empty), Path.GetFileName(DestinationPath));
}

internal sealed record FileBatchExecutionResult(int CompletedCount, IReadOnlyList<string> Failures);
