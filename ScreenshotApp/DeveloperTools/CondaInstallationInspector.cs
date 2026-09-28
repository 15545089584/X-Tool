using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotApp.DeveloperTools;

/// <summary>只读识别 Conda 基础安装，不执行批处理、激活环境或改写用户配置。</summary>
internal static class CondaInstallationInspector
{
    private static readonly string[] DistributionNames = ["miniconda3", "anaconda3", "miniforge3", "mambaforge"];

    internal static string? FindRoot(string path)
    {
        try
        {
            var directory = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (Path.GetFileName(directory).Equals("condabin", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(directory).Equals("Scripts", StringComparison.OrdinalIgnoreCase))
                directory = Directory.GetParent(directory)?.FullName ?? directory;
            if (!Directory.Exists(Path.Combine(directory, "conda-meta"))) return null;
            return new DirectoryInfo(directory).ResolveLinkTarget(true)?.FullName ?? directory;
        }
        catch { return null; }
    }

    internal static ToolchainInstallation? Inspect(string path, string source, bool active)
    {
        var root = FindRoot(path);
        if (root is null) return null;
        var executable = Path.Combine(root, "Scripts", "conda.exe");
        var batch = Path.Combine(root, "condabin", "conda.bat");
        if (!File.Exists(executable) && !File.Exists(batch)) return null;
        string? version = null;
        var architecture = "未知";
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "conda-meta"), "conda-*.json").Take(64))
            {
                // 元数据过大或损坏时保持“待验证”，不执行未知命令来兜底。
                if (new FileInfo(file).Length > 2 * 1024 * 1024) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(file, System.Text.Encoding.UTF8));
                var package = document.RootElement;
                if (package.TryGetProperty("name", out var name) && name.GetString() == "conda" &&
                    package.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String &&
                    Regex.IsMatch(value.GetString() ?? "", @"^\d+\.\d+(?:\.\d+)?(?:[a-zA-Z0-9.+_-]*)$"))
                {
                    version = value.GetString();
                    if (package.TryGetProperty("subdir", out var subdir))
                        architecture = subdir.GetString() switch { "win-64" => "x64", "win-32" => "x86", "win-arm64" => "arm64", _ => "未知" };
                    break;
                }
            }
        }
        catch { /* 无法完整读取安装元数据时报告未验证。 */ }
        var complete = version is not null && File.Exists(executable) && File.Exists(batch) && File.Exists(Path.Combine(root, "python.exe"));
        return new ToolchainInstallation
        {
            ToolchainId = "conda", DisplayName = "Conda", Version = version ?? "待验证",
            InstallationPath = root, ExecutablePath = File.Exists(batch) ? batch : executable,
            ResolvedExecutablePath = File.Exists(executable) ? executable : string.Empty,
            Source = source, IsActive = active, IsVerified = complete,
            Architecture = architecture,
            Evidence = complete ? "已核对 conda-meta 包版本、condabin 入口与基础 Python；未执行激活或初始化。"
                : "安装元数据或基础入口不完整，请使用原发行版安装器修复。"
        };
    }

    internal static IReadOnlyList<ToolchainInstallation> Discover(IReadOnlyList<string> persistentPaths, CancellationToken token)
    {
        var found = new Dictionary<string, ToolchainInstallation>(StringComparer.OrdinalIgnoreCase);
        var activeAssigned = false;
        void Add(string path, string source, bool active = false)
        {
            token.ThrowIfCancellationRequested();
            var item = Inspect(path, source, active);
            if (item is not null && !found.ContainsKey(item.InstallationPath)) found[item.InstallationPath] = item;
        }
        foreach (var path in persistentPaths)
        {
            // 仅持久 PATH 中直接命中的命令才算当前入口，不继承宿主激活状态。
            if (!File.Exists(Path.Combine(path, "conda.bat")) && !File.Exists(Path.Combine(path, "conda.exe"))) continue;
            Add(path, "持久 PATH", !activeAssigned);
            activeAssigned = true;
        }
        foreach (var scope in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        foreach (var variable in new[] { "CONDA_EXE", "CONDA_PREFIX" })
        {
            var value = Environment.GetEnvironmentVariable(variable, scope);
            if (!string.IsNullOrWhiteSpace(value)) Add(Environment.ExpandEnvironmentVariables(value.Trim('"')), $"{scope} {variable}");
        }
        foreach (var parent in new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        foreach (var name in DistributionNames) Add(Path.Combine(parent, name), "常用 Conda 目录");

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(drive.RootDirectory.FullName).Take(300)
                             .Where(p => DistributionNames.Any(n => Path.GetFileName(p).StartsWith(n, StringComparison.OrdinalIgnoreCase))))
                    Add(directory, "固定盘顶层 Conda 目录");
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 单个磁盘访问失败不阻断其他来源。 */ }
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var product = uninstall!.OpenSubKey(name);
                    var display = product?.GetValue("DisplayName") as string ?? name;
                    if (!new[] { "miniconda", "anaconda", "miniforge", "mambaforge" }.Any(n => display.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
                    if (product?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location)) Add(location, "Conda 卸载登记");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 注册表不可读时保留其他已发现安装。 */ }
        }
        return found.Values.OrderByDescending(i => i.IsActive).ThenBy(i => i.InstallationPath).ToArray();
    }

    internal static IEnumerable<DeveloperDiagnosticIssue> Diagnose(ToolchainSummary? conda, IReadOnlyList<string> paths)
    {
        if (conda is null) yield break;
        foreach (var installation in conda.Installations)
        {
            if (!installation.IsVerified)
                yield return new DeveloperDiagnosticIssue { Severity = DeveloperIssueSeverity.Warning, Title = "Conda 安装不完整", Description = installation.Evidence, Evidence = installation.InstallationPath };
            else if (!conda.HasActiveCommand)
                yield return new DeveloperDiagnosticIssue { Severity = DeveloperIssueSeverity.Info, Title = "Conda 尚未加入持久 PATH", Description = "可在 SDK 与工具链中配置 condabin；使用专用 Conda 终端时也可保持当前设置。", Evidence = installation.InstallationPath };
            var root = installation.InstallationPath.TrimEnd(Path.DirectorySeparatorChar);
            if (paths.Any(p => p.TrimEnd(Path.DirectorySeparatorChar).Equals(root, StringComparison.OrdinalIgnoreCase) ||
                               p.TrimEnd(Path.DirectorySeparatorChar).Equals(root + @"\Scripts", StringComparison.OrdinalIgnoreCase) ||
                               p.StartsWith(root + @"\envs\", StringComparison.OrdinalIgnoreCase)))
                yield return new DeveloperDiagnosticIssue { Severity = DeveloperIssueSeverity.Warning, Title = "Conda Python 目录位于全局 PATH", Description = "基础或项目环境可能抢占其他 Python；建议仅配置 condabin，在终端按需激活环境。X-Tool 不会自动删除现有 PATH。", Evidence = root };
        }
    }
}
