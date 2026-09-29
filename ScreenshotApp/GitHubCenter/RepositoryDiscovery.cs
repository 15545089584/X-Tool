using System.IO;

namespace ScreenshotApp.GitHubCenter;

public sealed record RepositoryScan(List<string> Paths, bool Limited, int Skipped);

public static class RepositoryDiscovery
{
    public static IEnumerable<string> DefaultRoots()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string name in new[] { "source", "repos", "Projects", "Documents", "Desktop" })
            yield return Path.Combine(home, name);
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            foreach (string name in new[] { "Claude Code", "Code", "Projects", "Repos", "Git" })
                yield return Path.Combine(drive.RootDirectory.FullName, name);
    }

    public static RepositoryScan Scan(IEnumerable<string> roots, CancellationToken ct, int maxDirectories = 10000)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();
        foreach (string root in roots.Where(Directory.Exists)) queue.Enqueue((Path.GetFullPath(root), 0));
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".venv", "venv", "AppData", "$RECYCLE.BIN", "Windows" };
        int skipped = 0;
        bool limited = false;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (seen.Count >= maxDirectories) { limited = true; break; }
            var (path, depth) = queue.Dequeue();
            if (!seen.Add(path)) continue;
            try
            {
                // 不跟随目录链接，避免循环和扫描范围逃逸；识别 worktree 的 .git 文件。
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                string marker = Path.Combine(path, ".git");
                if (Directory.Exists(marker) || File.Exists(marker)) found.Add(path);
                if (depth >= 6) { limited = true; continue; }
                foreach (string child in Directory.EnumerateDirectories(path))
                    if (!excluded.Contains(Path.GetFileName(child))) queue.Enqueue((child, depth + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(found.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(), limited, skipped);
    }
}
