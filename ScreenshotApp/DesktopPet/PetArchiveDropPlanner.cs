using System.IO;
using ScreenshotApp.Archives;

namespace ScreenshotApp.DesktopPet;

internal enum PetArchiveDropAction
{
    Compress,
    Extract
}

internal static class PetArchiveDropPlanner
{
    internal static PetArchiveDropAction ResolveAction(IReadOnlyList<string> paths)
    {
        return paths.Count > 0 &&
               paths.All(path => File.Exists(path) && ArchiveService.IsSupportedArchivePath(path))
            ? PetArchiveDropAction.Extract
            : PetArchiveDropAction.Compress;
    }

    internal static string CreateCompressionOutputPath(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            throw new ArgumentException("至少需要一个压缩来源。", nameof(paths));
        }

        var firstPath = Path.GetFullPath(paths[0]);
        var parent = Directory.Exists(firstPath)
            ? Directory.GetParent(firstPath)?.FullName
            : Path.GetDirectoryName(firstPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException("无法确定压缩包输出位置。");
        }

        var baseName = paths.Count == 1
            ? Directory.Exists(firstPath)
                ? new DirectoryInfo(firstPath).Name
                : Path.GetFileNameWithoutExtension(firstPath)
            : "压缩文件";
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "压缩文件";
        }

        return ResolveUniquePath(Path.Combine(parent, $"{baseName}.zip"));
    }

    internal static string GetExtractionDestination(string archivePath)
    {
        var fullPath = Path.GetFullPath(archivePath);
        return Path.GetDirectoryName(fullPath)
               ?? throw new InvalidOperationException("无法确定压缩包所在位置。");
    }

    private static string ResolveUniquePath(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"无法为压缩包生成可用名称：{desiredPath}");
    }
}
