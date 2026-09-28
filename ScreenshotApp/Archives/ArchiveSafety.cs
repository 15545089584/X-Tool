using System.IO;

namespace ScreenshotApp.Archives;

internal static class ArchiveSafety
{
    public const int MaximumEntryCount = 100_000;
    public const int MaximumPreviewEntryCount = 5_000;
    public const int MaximumPathDepth = 64;
    public const long MaximumSingleEntryBytes = 256L * 1024 * 1024 * 1024;
    public const long MaximumExpandedBytes = 512L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string ResolveSafeDestination(string destinationRoot, string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            throw new InvalidDataException("压缩包包含未命名条目，已停止解压。");
        }

        var normalizedEntry = entryPath.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedEntry) || normalizedEntry.Contains(':'))
        {
            throw new InvalidDataException($"条目包含绝对路径或数据流路径：{entryPath}");
        }

        var segments = normalizedEntry.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Length > MaximumPathDepth)
        {
            throw new InvalidDataException($"条目路径层级异常：{entryPath}");
        }

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                throw new InvalidDataException($"条目路径不安全：{entryPath}");
            }

            var fileStem = segment.Split('.')[0];
            if (ReservedFileNames.Contains(fileStem))
            {
                throw new InvalidDataException($"条目使用了 Windows 保留名称：{entryPath}");
            }
        }

        var root = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"条目试图写出目标目录：{entryPath}");
        }

        return destination;
    }

    public static void ValidateEntryMetadata(string entryPath, long size, string? linkTarget)
    {
        if (!string.IsNullOrWhiteSpace(linkTarget))
        {
            throw new InvalidDataException($"为避免链接跳出目标目录，暂不解压链接条目：{entryPath}");
        }

        if (size < 0 || size > MaximumSingleEntryBytes)
        {
            throw new InvalidDataException($"条目大小超出安全限制：{entryPath}");
        }
    }

    public static long GetMaximumAllowedExpandedBytes(long compressedBytes)
    {
        const long minimumAllowance = 4L * 1024 * 1024 * 1024;
        long ratioAllowance;
        try
        {
            ratioAllowance = checked(Math.Max(compressedBytes, 1) * 1_000);
        }
        catch (OverflowException)
        {
            ratioAllowance = MaximumExpandedBytes;
        }

        return Math.Min(MaximumExpandedBytes, Math.Max(minimumAllowance, ratioAllowance));
    }

    public static void EnsureSufficientFreeSpace(string destinationPath, long requiredBytes)
    {
        if (requiredBytes <= 0)
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(destinationPath);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return;
            }

            const long reserveBytes = 256L * 1024 * 1024;
            if (drive.AvailableFreeSpace < requiredBytes + reserveBytes)
            {
                throw new IOException(
                    $"目标磁盘空间不足：任务预计需要 {ArchiveSizeFormatter.Format(requiredBytes)}，" +
                    $"当前可用 {ArchiveSizeFormatter.Format(drive.AvailableFreeSpace)}。");
            }
        }
        catch (ArgumentException)
        {
            // 某些网络路径无法映射为 DriveInfo，实际写入错误仍由文件系统返回。
        }
        catch (UnauthorizedAccessException)
        {
            // 无权读取卷信息时不提前阻断，实际文件写入仍会执行权限检查。
        }
    }

    public static void EnsureNoReparsePointBetween(string destinationRoot, string destinationPath)
    {
        var root = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(destinationPath);
        while (!string.IsNullOrWhiteSpace(parent) &&
               parent.Length > root.Length &&
               parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(parent) &&
                (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"目标路径包含重解析点，已停止解压：{parent}");
            }

            parent = Path.GetDirectoryName(parent);
        }
    }
}
