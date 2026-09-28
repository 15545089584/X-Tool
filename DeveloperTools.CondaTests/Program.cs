using System.Text;
using System.IO;
using ScreenshotApp.DeveloperTools;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Contains("--ui"))
{
    Exception? failure = null;
    var thread = new Thread(() => { try { UiSmoke.Run(args.SkipWhile(a => a != "--ui").Skip(1).FirstOrDefault()); } catch (Exception ex) { failure = ex; } });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw failure;
    return;
}
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("失败：" + name);
    passed++;
    Console.WriteLine("通过：" + name);
}
void Reject(Action action, string name)
{
    try { action(); } catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception("未拒绝：" + name);
}

// 所有安装样本仅存在于本次创建的临时目录，空入口文件从不执行。
var fixture = Path.Combine(Path.GetTempPath(), "XTool-CondaTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    var root = Path.Combine(fixture, "中文 Conda");
    Directory.CreateDirectory(Path.Combine(root, "conda-meta"));
    Directory.CreateDirectory(Path.Combine(root, "condabin"));
    Directory.CreateDirectory(Path.Combine(root, "Scripts"));
    foreach (var name in new[] { "python.exe", "Scripts/conda.exe", "condabin/conda.bat" })
        File.WriteAllText(Path.Combine(root, name), "", Encoding.UTF8);
    var metadata = Path.Combine(root, "conda-meta", "conda-26.7.2-0.json");
    File.WriteAllText(metadata, """{"name":"conda","version":"26.7.2","subdir":"win-64"}""", Encoding.UTF8);
    var item = CondaInstallationInspector.Inspect(root, "测试", false)!;
    Check(item.IsVerified && item.Version == "26.7.2" && item.Architecture == "x64", "安装元数据与入口识别");
    Check(CondaInstallationInspector.FindRoot(Path.Combine(root, "condabin") + "\\") == root, "带尾部分隔符的 condabin");
    Check(CondaInstallationInspector.FindRoot(Path.Combine(root, "Scripts", "conda.exe")) == root, "从 EXE 还原根目录");
    Check(CondaInstallationInspector.Inspect(fixture, "测试", false) is null, "普通目录不误识别");
    var summary = new ToolchainSummary { Id = "conda" };
    summary.Installations.Add(item);
    var plan = DeveloperEnvironmentConfigurationPlanner.Build(summary, item);
    Check(plan.CanApply && plan.PathEntries.SequenceEqual(new[] { Path.Combine(root, "condabin") }) && plan.Variables.Count == 0, "只规划 condabin 且无额外变量");
    var python = new ToolchainInstallation { IsVerified = true, ExecutablePath = Path.Combine(root, "python.exe"), InstallationPath = root };
    Check(!DeveloperEnvironmentConfigurationPlanner.Build(new ToolchainSummary { Id = "python" }, python).CanApply, "阻止 Conda Python 全局配置");
    Check(CondaInstallationInspector.Diagnose(summary, []).Any(i => i.Title.Contains("尚未加入")), "缺失 PATH 诊断");
    Check(CondaInstallationInspector.Diagnose(summary, [root]).Any(i => i.Severity == DeveloperIssueSeverity.Warning), "基础 Python 抢占诊断");
    Check(CondaInstallationInspector.Diagnose(summary, [Path.Combine(root, "Scripts")]).Any(i => i.Severity == DeveloperIssueSeverity.Warning), "Scripts 抢占诊断");
    Check(CondaInstallationInspector.Diagnose(summary, [Path.Combine(root, "condabin")]).All(i => i.Severity != DeveloperIssueSeverity.Warning), "安全 condabin 不误报");
    File.WriteAllText(metadata, "{损坏", Encoding.UTF8);
    Check(CondaInstallationInspector.Inspect(root, "测试", false) is { IsVerified: false }, "损坏元数据降级");
    Check(!DeveloperEnvironmentConfigurationPlanner.Build(summary, item).CanApply, "配置前重新核对元数据");
    var incomplete = new ToolchainSummary { Id = "conda" };
    incomplete.Installations.Add(CondaInstallationInspector.Inspect(root, "测试", false)!);
    Check(CondaInstallationInspector.Diagnose(incomplete, []).Any(i => i.Title.Contains("不完整")), "安装不完整诊断");

    var hash = new string('a', 64);
    var file = "Miniconda3-py314_26.7.1-1-Windows-x86_64.exe";
    var html = $"<tr><td><a href=\"Miniconda3-latest-Windows-x86_64.exe\">latest</a></td><td>{hash}</td></tr><tr><td><a href=\"{file}\">release</a></td><td class=\"s\">126.4M</td><td>{hash}</td></tr>";
    var release = CondaInstallerService.ParseMiniconda(html);
    Check(release.FileName == file && release.Sha256 == hash && release.DownloadSize > 0, "Miniconda 固定版本与校验匹配");
    Reject(() => CondaInstallerService.ParseMiniconda("<tr></tr>"), "拒绝缺少官方校验目录");
    Reject(() => CondaInstallerService.ParseMiniconda(html.Replace($"href=\"{file}\"", "href=\"unknown.exe\"")), "拒绝无法固定版本的目录");
    Check(CondaInstallerService.ParseChecksum(hash + "  " + file + "\r\n", file) == hash, "SHA 文件解析");
    Reject(() => CondaInstallerService.ParseChecksum(hash + "  other.exe", file), "拒绝错名校验文件");
    Reject(() => CondaInstallerService.ParseChecksum("abc  " + file, file), "拒绝无效校验值");
    Check(CondaInstallerService.TrustedUrl("miniconda", release.DownloadUrl, file), "允许 Miniconda 官方固定地址");
    foreach (var url in new[] { release.DownloadUrl.Replace("https:", "http:"), release.DownloadUrl + "?evil=1", release.DownloadUrl.Replace("repo.anaconda.com", "repo.anaconda.com.evil.test"), release.DownloadUrl.Replace("repo.anaconda.com", "user@repo.anaconda.com"), release.DownloadUrl.Replace("repo.anaconda.com", "repo.anaconda.com:8443") })
        Check(!CondaInstallerService.TrustedUrl("miniconda", url, file), "拒绝不可信下载地址");
    var forge = "Miniforge3-26.7.2-0-Windows-x86_64.exe";
    Check(CondaInstallerService.TrustedUrl("miniforge", "https://github.com/conda-forge/miniforge/releases/download/26.7.2-0/" + forge, forge), "允许 Miniforge 官方固定地址");
    Check(!(await CondaInstallerService.LaunchInstallerAsync(new ManagedToolchainRelease { ToolchainId = "conda" }, null, default)).Succeeded, "无来源安装请求在下载前拒绝");
    if (args.Contains("--live"))
        foreach (var provider in new[] { "miniconda", "miniforge" })
        {
            var live = await CondaInstallerService.GetReleaseAsync(provider, default);
            Check(live.Sha256.Length == 64 && CondaInstallerService.TrustedUrl(provider, live.DownloadUrl, live.FileName), $"官方目录联通：{provider} {live.Version}");
        }
    Console.WriteLine($"全部通过：{passed} 项。未下载或启动安装器，未修改环境变量。");
}
finally
{
    // 仅删除已知临时根目录内、本次生成的 GUID 样本。
    var resolved = Path.GetFullPath(fixture);
    var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
    if (Directory.GetParent(resolved)?.FullName == expectedParent && Path.GetFileName(resolved).StartsWith("XTool-CondaTests-", StringComparison.Ordinal))
        Directory.Delete(resolved, true);
}
