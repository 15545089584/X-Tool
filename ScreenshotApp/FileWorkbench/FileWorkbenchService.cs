using System.Globalization;
using System.IO;

namespace ScreenshotApp.FileWorkbench;

/// <summary>文件工作台的本地搜索与安全批处理规划。</summary>
internal static class FileWorkbenchService
{
    internal static IReadOnlyList<FileWorkbenchItem> Search(
        string rootDirectory,
        string keyword,
        string typeFilter,
        DateTime? modifiedAfter,
        FileSortField sortField,
        bool sortAscending,
        CancellationToken cancellationToken)
    {
        var results = new List<FileWorkbenchItem>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!Matches(info, keyword, typeFilter, modifiedAfter))
                {
                    continue;
                }

                results.Add(FileWorkbenchItem.Create(info));
            }
            catch (UnauthorizedAccessException)
            {
                // 无权限的目录不影响其余位置搜索。
            }
            catch (IOException)
            {
                // 搜索过程中被移动或占用的文件直接跳过。
            }
        }

        return OrderResults(results, sortField, sortAscending).ToArray();
    }

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
        return (field, ascending) switch
        {
            (FileSortField.Name, true) => items.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase),
            (FileSortField.Name, false) => items.OrderByDescending(item => item.FileName, StringComparer.OrdinalIgnoreCase),
            (FileSortField.Size, true) => items.OrderBy(item => item.Size).ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase),
            (FileSortField.Size, false) => items.OrderByDescending(item => item.Size).ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase),
            (FileSortField.Modified, true) => items.OrderBy(item => item.ModifiedAt).ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderByDescending(item => item.ModifiedAt).ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
        };
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
    Size,
    Modified
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
