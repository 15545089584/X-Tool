using System.Globalization;

namespace ScreenshotApp.Archives;

public enum ArchiveOutputFormat
{
    Zip,
    SevenZip,
    TarGZip
}

public enum ArchiveCompressionPreset
{
    Store,
    Fast,
    Balanced,
    Smallest
}

public enum ArchiveConflictPolicy
{
    Rename,
    Skip,
    Overwrite
}

public sealed record ArchiveSourceItem(
    string FullPath,
    string DisplayName,
    bool IsDirectory,
    long? Size)
{
    public string KindText => IsDirectory ? "文件夹" : "文件";

    public string SizeText => Size.HasValue ? ArchiveSizeFormatter.Format(Size.Value) : "执行前计算";
}

public sealed record ArchivePreviewItem(
    string EntryPath,
    long Size,
    bool IsDirectory,
    bool IsEncrypted,
    DateTime? LastModifiedTime)
{
    public string DisplayName => string.IsNullOrWhiteSpace(EntryPath)
        ? "（未命名条目）"
        : EntryPath.Replace('/', '\\');

    public string KindText => IsDirectory ? "文件夹" : "文件";

    public string SizeText => IsDirectory ? "—" : ArchiveSizeFormatter.Format(Size);

    public string EncryptionText => IsEncrypted ? "已加密" : string.Empty;

    public string ModifiedText => LastModifiedTime?.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "时间未知";
}

public sealed record ArchiveInspection(
    string FormatName,
    IReadOnlyList<ArchivePreviewItem> PreviewEntries,
    int TotalEntryCount,
    long TotalUncompressedBytes,
    bool IsEncrypted,
    bool PreviewTruncated);

public sealed record ArchiveProgressInfo(
    string Phase,
    string CurrentEntry,
    long ProcessedBytes,
    long TotalBytes)
{
    public double Percent => TotalBytes <= 0
        ? 0
        : Math.Clamp(ProcessedBytes * 100d / TotalBytes, 0, 100);
}

public sealed record ArchiveOperationResult(
    int ProcessedFiles,
    int SkippedFiles,
    long ProcessedBytes,
    string OutputPath);

public static class ArchiveSizeFormatter
{
    public static string Format(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var value = (double)bytes;
        var units = new[] { "KB", "MB", "GB", "TB" };
        var unitIndex = -1;
        do
        {
            value /= 1024;
            unitIndex++;
        }
        while (value >= 1024 && unitIndex < units.Length - 1);

        return $"{value:0.##} {units[unitIndex]}";
    }
}
