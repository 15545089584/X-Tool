using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenshotApp.DeveloperTools;

/// <summary>管理 X-Tool 自己下载的开发工具；不会接管或删除外部安装。</summary>
public sealed class ManagedToolchainService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim ManifestLock = new(1, 1);
    private const string AdoptiumAvailableReleasesUrl = "https://api.adoptium.net/v3/info/available_releases";
    private const string TunaAdoptiumRoot = "https://mirrors.tuna.tsinghua.edu.cn/Adoptium";
    private static readonly int[] PythonMinors = { 10, 11, 12, 13, 14 };
    private readonly SafeDeveloperCommandRunner _commandRunner = new();

    public ManagedDownloadSource TemurinDownloadSource { get; set; } = ManagedDownloadSource.Official;
    public string TemurinDownloadSourceText => TemurinDownloadSource == ManagedDownloadSource.Tuna
        ? "清华 TUNA 镜像（失败回退官方源）"
        : "Eclipse Adoptium 官方源";

    public string ManagedRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "Dev");

    private string ManifestPath => Path.Combine(ManagedRoot, "managed-tools.json");
    private string DownloadsRoot => Path.Combine(ManagedRoot, "Downloads");
    private string JavaRoot => Path.Combine(ManagedRoot, "Java");
    private string PythonRoot => Path.Combine(ManagedRoot, "Python", "uv");
    private string VoltaRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("VOLTA_HOME", EnvironmentVariableTarget.User);
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Volta")
                : Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
        }
    }
    private string LogPath => Path.Combine(ManagedRoot, "Logs", "managed-toolchains.log");

    public async Task<IReadOnlyList<ManagedToolchainRelease>> GetTemurinReleasesAsync(CancellationToken cancellationToken)
    {
        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        using var infoResponse = await HttpClient.GetAsync(AdoptiumAvailableReleasesUrl, cancellationToken).ConfigureAwait(false);
        infoResponse.EnsureSuccessStatusCode();
        await using var infoStream = await infoResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var infoDocument = await JsonDocument.ParseAsync(infoStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var available = infoDocument.RootElement.GetProperty("available_releases").EnumerateArray()
            .Select(item => item.GetInt32()).Where(major => major >= 8).Distinct().OrderByDescending(major => major).ToArray();
        var ltsMajors = infoDocument.RootElement.GetProperty("available_lts_releases").EnumerateArray()
            .Select(item => item.GetInt32()).ToHashSet();
        var currentMajor = infoDocument.RootElement.TryGetProperty("most_recent_feature_release", out var current)
            ? current.GetInt32()
            : available.FirstOrDefault();

        using var gate = new SemaphoreSlim(4);
        var tasks = available.Select(async major =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var uri = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&image_type=jdk&jvm_impl=hotspot&os=windows&vendor=eclipse";
                using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var first = document.RootElement.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("binary", out var binary) ||
                    !binary.TryGetProperty("package", out var package) || !first.TryGetProperty("version", out var version)) return null;

                var releaseVersion = version.GetProperty("openjdk_version").GetString() ?? major.ToString();
                var downloadUrl = package.GetProperty("link").GetString() ?? string.Empty;
                var sha256 = package.GetProperty("checksum").GetString() ?? string.Empty;
                var fileName = package.GetProperty("name").GetString() ?? string.Empty;
                var size = package.TryGetProperty("size", out var sizeValue) ? sizeValue.GetInt64() : 0;
                if (!IsTrustedTemurinDownload(downloadUrl, fileName, sha256)) return null;

                return new ManagedToolchainRelease
                {
                    ToolchainId = "java", ProviderId = "temurin", DisplayName = $"Temurin JDK {major}", Version = releaseVersion,
                    Architecture = "x64", DownloadUrl = downloadUrl, Sha256 = sha256, FileName = fileName, DownloadSize = size, FeatureVersion = major,
                    IsLts = ltsMajors.Contains(major), IsRecommended = ltsMajors.Contains(major) || major == currentMajor,
                    IsInstalled = manifest.Installations.Any(item => item.ManagedByXTool && item.ToolchainId == "java" &&
                        item.ProviderId == "temurin" && item.Version == releaseVersion && Directory.Exists(item.InstallationPath))
                };
            }
            finally { gate.Release(); }
        });

        var releases = (await Task.WhenAll(tasks).ConfigureAwait(false)).Where(item => item is not null).Cast<ManagedToolchainRelease>();
        return releases.OrderByDescending(item => item.IsRecommended).ThenByDescending(item => ParseMajor(item.Version)).ToList();
    }

    public async Task<IReadOnlyList<ManagedToolchainRelease>> GetUvPythonReleasesAsync(CancellationToken cancellationToken)
    {
        var uvPath = await FindValidatedUvAsync(cancellationToken).ConfigureAwait(false);
        if (uvPath is null) throw new FileNotFoundException("未找到可验证的 uv.exe；请先从 uv 官方方式安装 uv。 ");
        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        var releases = new List<ManagedToolchainRelease>();
        foreach (var minor in PythonMinors)
        {
            var result = await _commandRunner.RunAsync(uvPath,
                new[] { "python", "list", $"3.{minor}", "--managed-python", "--only-downloads", "--output-format", "json", "--no-config", "--color", "never" },
                cancellationToken, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0) continue;
            using var document = JsonDocument.Parse(result.StandardOutput);
            var candidate = document.RootElement.EnumerateArray().FirstOrDefault(item =>
                item.TryGetProperty("implementation", out var implementation) && implementation.GetString() == "cpython" &&
                item.TryGetProperty("arch", out var architecture) && architecture.GetString() == "x86_64" &&
                item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String);
            if (candidate.ValueKind != JsonValueKind.Object) continue;
            var key = candidate.GetProperty("key").GetString() ?? string.Empty;
            var version = candidate.GetProperty("version").GetString() ?? string.Empty;
            var urlText = candidate.GetProperty("url").GetString() ?? string.Empty;
            if (!IsTrustedUvRelease(key, urlText)) continue;
            releases.Add(new ManagedToolchainRelease
            {
                ToolchainId = "python",
                ProviderId = "uv",
                DisplayName = $"Python 3.{minor}",
                Version = version,
                Architecture = "x64",
                DownloadUrl = urlText,
                FileName = key,
                IsInstalled = manifest.Installations.Any(item => item.ManagedByXTool && item.ToolchainId == "python" &&
                    item.ProviderId == "uv" && item.Version == version && Directory.Exists(item.InstallationPath))
            });
        }
        foreach (var entry in manifest.Installations.Where(item => item.ManagedByXTool && item.ProviderId == "uv" &&
                     Directory.Exists(item.InstallationPath) && !releases.Any(release => release.Version == item.Version)))
        {
            if (!IsTrustedUvRelease(entry.PackageKey, entry.DownloadUrl)) continue;
            releases.Add(new ManagedToolchainRelease
            {
                ToolchainId = "python", ProviderId = "uv", DisplayName = $"Python {entry.Version}", Version = entry.Version,
                Architecture = entry.Architecture, DownloadUrl = entry.DownloadUrl, FileName = entry.PackageKey, IsInstalled = true
            });
        }
        return releases.OrderByDescending(item => item.Version).ToList();
    }

    public Task<ManagedToolchainOperationResult> InstallAsync(
        ManagedToolchainRelease release, IProgress<ManagedInstallProgress>? progress, CancellationToken cancellationToken)
        => release.ProviderId switch
        {
            "temurin" => InstallTemurinAsync(release, progress, cancellationToken),
            "uv" => InstallUvPythonAsync(release, progress, cancellationToken),
            "volta" => FetchVoltaNodeAsync(release, progress, cancellationToken),
            _ => Task.FromResult(new ManagedToolchainOperationResult(false, "不支持该托管来源。"))
        };

    public async Task<IReadOnlyList<ManagedToolchainRelease>> GetVoltaNodeReleasesAsync(CancellationToken cancellationToken)
    {
        var voltaPath = await FindValidatedVoltaAsync(cancellationToken).ConfigureAwait(false);
        using var versionsResponse = await HttpClient.GetAsync("https://nodejs.org/dist/index.json", cancellationToken).ConfigureAwait(false);
        versionsResponse.EnsureSuccessStatusCode();
        await using var versionsStream = await versionsResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var versionsDocument = await JsonDocument.ParseAsync(versionsStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        using var scheduleResponse = await HttpClient.GetAsync("https://raw.githubusercontent.com/nodejs/Release/main/schedule.json", cancellationToken).ConfigureAwait(false);
        scheduleResponse.EnsureSuccessStatusCode();
        await using var scheduleStream = await scheduleResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var scheduleDocument = await JsonDocument.ParseAsync(scheduleStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var today = DateTime.UtcNow.Date;
        var supportedMajors = scheduleDocument.RootElement.EnumerateObject()
            .Where(item => item.Value.TryGetProperty("start", out var start) && DateTime.TryParse(start.GetString(), out var startDate) && startDate <= today &&
                           item.Value.TryGetProperty("end", out var end) && DateTime.TryParse(end.GetString(), out var endDate) && endDate >= today)
            .Select(item => int.TryParse(item.Name.TrimStart('v'), out var major) ? major : 0)
            .Where(major => major > 0)
            .ToHashSet();

        var latest = new Dictionary<int, JsonElement>();
        foreach (var item in versionsDocument.RootElement.EnumerateArray())
        {
            var version = item.GetProperty("version").GetString()?.TrimStart('v') ?? string.Empty;
            if (!int.TryParse(version.Split('.')[0], out var major) || !supportedMajors.Contains(major) || latest.ContainsKey(major)) continue;
            if (!item.TryGetProperty("files", out var files) || !files.EnumerateArray().Any(file => file.GetString() == "win-x64-zip")) continue;
            latest[major] = item.Clone();
        }

        var releases = new List<ManagedToolchainRelease>();
        foreach (var pair in latest.OrderByDescending(item => item.Key))
        {
            var version = pair.Value.GetProperty("version").GetString()?.TrimStart('v') ?? string.Empty;
            var cachedPath = Path.Combine(VoltaRoot, "tools", "image", "node", version);
            var cached = Directory.Exists(cachedPath) && File.Exists(Path.Combine(cachedPath, "node.exe"));
            var isLts = pair.Value.TryGetProperty("lts", out var lts) && lts.ValueKind == JsonValueKind.String;
            releases.Add(new ManagedToolchainRelease
            {
                ToolchainId = "node", ProviderId = "volta", DisplayName = $"Node.js {pair.Key}", Version = version,
                Architecture = "x64", DownloadUrl = $"https://nodejs.org/dist/v{version}/", FileName = $"node@{version}",
                IsLts = isLts, IsProviderAvailable = voltaPath is not null, IsInstalled = cached, IsReadOnlyInstalled = cached
            });
        }
        return releases;
    }

    public async Task<ManagedToolchainOperationResult> InstallVoltaWithWingetAsync(CancellationToken cancellationToken)
    {
        if (await FindValidatedVoltaAsync(cancellationToken).ConfigureAwait(false) is not null)
            return new(true, "Volta 已安装，无需重复安装。");
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (!File.Exists(winget)) return new(false, "未找到 Windows 程序包管理器 winget.exe，请按 Volta 官方指南手动安装。 ");
        var validation = await _commandRunner.RunAsync(winget, new[] { "--version" }, cancellationToken).ConfigureAwait(false);
        if (validation.TimedOut || validation.ExitCode != 0) return new(false, "winget.exe 验证失败，拒绝启动安装。");
        var result = await _commandRunner.RunAsync(winget,
            new[] { "install", "--id", "Volta.Volta", "--exact", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements" },
            cancellationToken, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
        if (result.TimedOut || result.ExitCode != 0)
        {
            await WriteLogAsync($"Volta WinGet 安装失败：{result.CombinedOutput}").ConfigureAwait(false);
            return new(false, result.TimedOut ? "Volta 安装超时。" : $"Volta 安装失败：{result.CombinedOutput}");
        }
        var volta = await FindValidatedVoltaAsync(cancellationToken).ConfigureAwait(false);
        if (volta is null) return new(false, "WinGet 返回成功，但尚未找到可验证的 volta.exe；可重启 X-Tool 后重新检查。");
        await WriteLogAsync($"已通过 WinGet 安装 Volta：{volta}").ConfigureAwait(false);
        return new(true, "Volta 已通过 WinGet 安装并完成版本验证。");
    }

    private async Task<ManagedToolchainOperationResult> FetchVoltaNodeAsync(
        ManagedToolchainRelease release, IProgress<ManagedInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (release.ToolchainId != "node" || release.ProviderId != "volta" || !IsTrustedNodeRelease(release))
            return new(false, "请求不是 Node.js 官方目录中的受支持 Windows x64 版本。");
        var volta = await FindValidatedVoltaAsync(cancellationToken).ConfigureAwait(false);
        if (volta is null) return new(false, "尚未安装可验证的 Volta。");
        progress?.Report(new("Volta 正在下载并校验 Node.js；不会切换默认版本"));
        var result = await _commandRunner.RunAsync(volta, new[] { "fetch", release.FileName }, cancellationToken, TimeSpan.FromMinutes(30)).ConfigureAwait(false);
        if (result.TimedOut || result.ExitCode != 0)
        {
            await WriteLogAsync($"Volta fetch {release.FileName} 失败：{result.CombinedOutput}").ConfigureAwait(false);
            return new(false, result.TimedOut ? "Volta 下载超时。" : $"Volta 下载失败：{result.CombinedOutput}");
        }
        var nodePath = Path.Combine(VoltaRoot, "tools", "image", "node", release.Version, "node.exe");
        if (!File.Exists(nodePath)) return new(false, "Volta 返回成功，但未在其标准缓存目录找到 node.exe。");
        var validation = await _commandRunner.RunAsync(nodePath, new[] { "--version" }, cancellationToken).ConfigureAwait(false);
        if (validation.TimedOut || validation.ExitCode != 0 || !validation.CombinedOutput.Contains($"v{release.Version}", StringComparison.OrdinalIgnoreCase))
            return new(false, "Node.js 已缓存，但绝对路径版本验证未通过。");
        await WriteLogAsync($"已通过 Volta 缓存 Node.js {release.Version}：{nodePath}").ConfigureAwait(false);
        var entry = new ManagedToolchainEntry
        {
            ToolchainId = "node",
            ProviderId = "volta",
            Version = release.Version,
            Architecture = release.Architecture,
            InstallationPath = Path.GetDirectoryName(nodePath) ?? string.Empty,
            ExecutablePath = nodePath,
            DownloadUrl = release.DownloadUrl,
            PackageKey = release.FileName,
            Sha256 = "由 Volta 与 Node.js 官方发行目录负责",
            InstalledAtUtc = DateTime.UtcNow,
            ManagedByXTool = false
        };
        return new(true, $"Node.js {release.Version} 已缓存；当前默认版本未改变。", entry);
    }

    private async Task<ManagedToolchainOperationResult> InstallUvPythonAsync(
        ManagedToolchainRelease release, IProgress<ManagedInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (release.ToolchainId != "python" || release.ProviderId != "uv" ||
            !IsTrustedUvRelease(release.FileName, release.DownloadUrl))
            return new(false, "安装请求不是 uv 官方目录中的 CPython Windows x64 版本。");

        var uvPath = await FindValidatedUvAsync(cancellationToken).ConfigureAwait(false);
        if (uvPath is null) return new(false, "未找到可验证的 uv.exe，无法执行托管安装。");
        Directory.CreateDirectory(PythonRoot);
        if (await FindInstalledPythonAsync(release.Version, cancellationToken).ConfigureAwait(false) is not null)
            return new(false, "X-Tool Python 托管目录已存在该版本，但清单没有所有权记录；为避免接管未知目录，安装已停止。");
        var uvInstalled = false;
        var manifestCommitted = false;
        try
        {
            progress?.Report(new("uv 正在下载、校验并安装 Python"));
            var result = await _commandRunner.RunAsync(uvPath,
                new[] { "python", "install", release.FileName, "--install-dir", PythonRoot, "--no-bin", "--no-registry", "--managed-python", "--no-config", "--color", "never" },
                cancellationToken, TimeSpan.FromMinutes(30)).ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0)
            {
                await WriteLogAsync($"uv Python {release.Version} 安装失败：{result.CombinedOutput}").ConfigureAwait(false);
                return new(false, result.TimedOut ? "uv 安装超时，进程已终止。" : $"uv 安装失败：{result.CombinedOutput}");
            }
            uvInstalled = true;

            var pythonPath = await FindInstalledPythonAsync(release.Version, cancellationToken).ConfigureAwait(false);
            if (pythonPath is null) return new(false, "uv 返回安装成功，但未在 X-Tool 托管目录找到匹配的 python.exe。");
            var installationPath = Directory.GetParent(pythonPath)!.FullName;
            var entry = new ManagedToolchainEntry
            {
                ToolchainId = "python", ProviderId = "uv", Version = release.Version, Architecture = release.Architecture,
                InstallationPath = installationPath, ExecutablePath = pythonPath, DownloadUrl = release.DownloadUrl,
                PackageKey = release.FileName, Sha256 = "由 uv 官方目录与内置校验负责", InstalledAtUtc = DateTime.UtcNow, ManagedByXTool = true
            };
            await AddManifestEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            manifestCommitted = true;
            await WriteLogAsync($"已通过 uv 安装 Python {release.Version} 到 {installationPath}").ConfigureAwait(false);
            return new(true, $"Python {release.Version} 已由 uv 安装并通过版本验证。", entry);
        }
        catch (OperationCanceledException)
        {
            await WriteLogAsync($"uv Python {release.Version} 安装已取消").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await WriteLogAsync($"uv Python {release.Version} 安装失败：{ex}").ConfigureAwait(false);
            return new(false, $"安装失败：{ex.Message}");
        }
        finally
        {
            if (uvInstalled && !manifestCommitted)
            {
                try
                {
                    await _commandRunner.RunAsync(uvPath,
                        new[] { "python", "uninstall", release.FileName, "--install-dir", PythonRoot, "--managed-python", "--no-config", "--color", "never" },
                        CancellationToken.None, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await WriteLogAsync($"uv Python {release.Version} 回滚失败：{ex.Message}").ConfigureAwait(false);
                }
            }
        }
    }

    public async Task<ManagedToolchainOperationResult> InstallTemurinAsync(
        ManagedToolchainRelease release,
        IProgress<ManagedInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (release.ToolchainId != "java" || release.ProviderId != "temurin" ||
            !IsTrustedTemurinDownload(release.DownloadUrl, release.FileName, release.Sha256))
        {
            return new(false, "安装请求不是受信任的 Temurin Windows ZIP。");
        }

        Directory.CreateDirectory(DownloadsRoot);
        Directory.CreateDirectory(JavaRoot);
        var operationId = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(DownloadsRoot, $"{operationId}.partial");
        var stagingPath = Path.Combine(JavaRoot, $".staging-{operationId}");
        string? installedPath = null;
        var manifestCommitted = false;
        try
        {
            var selectedSource = TemurinDownloadSourceText;
            progress?.Report(new($"正在连接 {selectedSource}"));
            var usedDownloadUrl = await DownloadTemurinWithFallbackAsync(release, archivePath, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("正在校验 SHA-256"));
            var actualHash = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await WriteLogAsync($"Temurin {release.Version} 校验失败：期望 {release.Sha256}，实际 {actualHash}").ConfigureAwait(false);
                return new(false, "下载文件的 SHA-256 与 Adoptium API 不一致，安装已停止。");
            }

            progress?.Report(new("正在安全解压 JDK"));
            Directory.CreateDirectory(stagingPath);
            await ExtractZipSafelyAsync(archivePath, stagingPath, cancellationToken).ConfigureAwait(false);
            var extractedRoot = FindJdkRoot(stagingPath);
            if (extractedRoot is null)
            {
                return new(false, "压缩包内未找到完整的 java.exe 与 javac.exe。");
            }

            var safeVersion = SanitizeDirectoryName(release.Version);
            var destination = Path.Combine(JavaRoot, $"temurin-{safeVersion}-{release.Architecture}");
            if (Directory.Exists(destination))
            {
                return new(false, "该 Temurin 版本的托管目录已经存在，请先重新扫描或卸载旧记录。");
            }

            Directory.Move(extractedRoot, destination);
            installedPath = destination;
            var javaPath = Path.Combine(destination, "bin", "java.exe");
            var validation = await _commandRunner.RunAsync(javaPath, new[] { "-version" }, cancellationToken).ConfigureAwait(false);
            if (validation.TimedOut || validation.ExitCode != 0)
            {
                TryDeleteManagedDirectory(destination);
                return new(false, "JDK 解压完成，但 java -version 验证失败，已撤销本次安装。");
            }

            var entry = new ManagedToolchainEntry
            {
                ToolchainId = "java",
                ProviderId = "temurin",
                Version = release.Version,
                Architecture = release.Architecture,
                InstallationPath = destination,
                ExecutablePath = javaPath,
                DownloadUrl = usedDownloadUrl,
                Sha256 = release.Sha256,
                InstalledAtUtc = DateTime.UtcNow,
                ManagedByXTool = true
            };
            await AddManifestEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            manifestCommitted = true;
            await WriteLogAsync($"已安装 Temurin {release.Version} 到 {destination}；下载源：{usedDownloadUrl}").ConfigureAwait(false);
            progress?.Report(new("安装与版本验证完成", release.DownloadSize, release.DownloadSize));
            return new(true, $"Temurin {release.Version} 已安装并通过 java -version 验证。", entry);
        }
        catch (OperationCanceledException)
        {
            await WriteLogAsync($"Temurin {release.Version} 安装已取消").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await WriteLogAsync($"Temurin {release.Version} 安装失败：{ex}").ConfigureAwait(false);
            return new(false, $"安装失败：{ex.Message}");
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteManagedDirectory(stagingPath);
            if (!manifestCommitted && !string.IsNullOrWhiteSpace(installedPath)) TryDeleteManagedDirectory(installedPath);
        }
    }

    public async Task<ManagedToolchainOperationResult> UninstallAsync(ManagedToolchainRelease release, CancellationToken cancellationToken)
    {
        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        var entry = manifest.Installations.FirstOrDefault(item => item.ManagedByXTool &&
            item.ToolchainId == release.ToolchainId && item.ProviderId == release.ProviderId && item.Version == release.Version);
        if (entry is null) return new(false, "未找到 X-Tool 托管记录，拒绝删除外部安装。");
        var expectedRoot = entry.ProviderId == "uv" ? PythonRoot : JavaRoot;
        if (!IsInsideRoot(entry.InstallationPath, expectedRoot)) return new(false, "托管目录超出对应的 X-Tool 根目录，拒绝删除。");

        var reference = FindReference(entry);
        if (reference is not null) return new(false, reference);
        if (IsInstallationRunning(entry.InstallationPath)) return new(false, "该工具链仍被运行中的进程使用，请关闭相关终端或 IDE 后重试。");

        try
        {
            if (entry.ProviderId == "uv")
            {
                var uvPath = await FindValidatedUvAsync(cancellationToken).ConfigureAwait(false);
                if (uvPath is null) return new(false, "未找到可验证的 uv.exe，无法安全卸载该 Python。");
                var result = await _commandRunner.RunAsync(uvPath,
                    new[] { "python", "uninstall", entry.PackageKey, "--install-dir", PythonRoot, "--managed-python", "--no-config", "--color", "never" },
                    cancellationToken, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                if (result.TimedOut || result.ExitCode != 0) return new(false, $"uv 卸载失败：{result.CombinedOutput}");
            }
            else if (Directory.Exists(entry.InstallationPath)) Directory.Delete(entry.InstallationPath, recursive: true);
            manifest.Installations.Remove(entry);
            await SaveManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
            await WriteLogAsync($"已卸载 {entry.ProviderId} {entry.Version}：{entry.InstallationPath}").ConfigureAwait(false);
            return new(true, $"{release.DisplayName} {entry.Version} 的 X-Tool 托管安装已卸载。");
        }
        catch (Exception ex)
        {
            await WriteLogAsync($"卸载 Temurin {entry.Version} 失败：{ex}").ConfigureAwait(false);
            return new(false, $"卸载失败：{ex.Message}");
        }
    }

    private async Task<string?> FindValidatedUvAsync(CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(home, ".local", "bin", "uv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "uv", "uv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "uv", "uv.exe")
        };
        foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            candidates.AddRange((Environment.GetEnvironmentVariable("Path", target) ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(item => Path.Combine(Environment.ExpandEnvironmentVariables(item.Trim().Trim('"')), "uv.exe")));
        }
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(candidate) || !File.Exists(candidate)) continue;
            var result = await _commandRunner.RunAsync(candidate, new[] { "--version" }, cancellationToken).ConfigureAwait(false);
            if (!result.TimedOut && result.ExitCode == 0 && result.StandardOutput.StartsWith("uv ", StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return null;
    }

    private async Task<string?> FindValidatedVoltaAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Volta", "volta.exe"),
            Path.Combine(VoltaRoot, "bin", "volta.exe")
        };
        foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            candidates.AddRange((Environment.GetEnvironmentVariable("Path", target) ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(item => Path.Combine(Environment.ExpandEnvironmentVariables(item.Trim().Trim('"')), "volta.exe")));
        }
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(candidate) || !File.Exists(candidate)) continue;
            var result = await _commandRunner.RunAsync(candidate, new[] { "--version" }, cancellationToken).ConfigureAwait(false);
            if (!result.TimedOut && result.ExitCode == 0 && Version.TryParse(result.StandardOutput.Trim(), out _)) return candidate;
        }
        return null;
    }

    private async Task<string?> FindInstalledPythonAsync(string expectedVersion, CancellationToken cancellationToken)
    {
        foreach (var pythonPath in Directory.EnumerateFiles(PythonRoot, "python.exe", SearchOption.AllDirectories))
        {
            var result = await _commandRunner.RunAsync(pythonPath, new[] { "--version" }, cancellationToken).ConfigureAwait(false);
            if (!result.TimedOut && result.ExitCode == 0 && result.CombinedOutput.Contains(expectedVersion, StringComparison.OrdinalIgnoreCase)) return pythonPath;
        }
        return null;
    }

    private async Task<string> DownloadTemurinWithFallbackAsync(
        ManagedToolchainRelease release,
        string destination,
        IProgress<ManagedInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string Url, string Name)>();
        if (TemurinDownloadSource == ManagedDownloadSource.Tuna)
        {
            var mirror = $"{TunaAdoptiumRoot}/{release.FeatureVersion}/jdk/x64/windows/{release.FileName}";
            if (IsTrustedTunaTemurinMirror(mirror, release.FileName)) candidates.Add((mirror, "清华 TUNA 镜像"));
        }
        candidates.Add((release.DownloadUrl, "Eclipse Adoptium 官方源"));

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteFile(destination);
            try
            {
                await DownloadAsync(candidate.Url, candidate.Name, destination, progress, cancellationToken).ConfigureAwait(false);
                return candidate.Url;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                await WriteLogAsync($"Temurin {release.Version} 从 {candidate.Name} 下载失败：{ex.Message}").ConfigureAwait(false);
                if (!string.Equals(candidate.Url, release.DownloadUrl, StringComparison.OrdinalIgnoreCase))
                    progress?.Report(new("镜像不可用，正在回退 Eclipse Adoptium 官方源"));
            }
        }
        throw new HttpRequestException("所有受控下载源均失败。", lastError);
    }

    private async Task DownloadAsync(string url, string sourceName, string destination, IProgress<ManagedInstallProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
        var buffer = new byte[1024 * 128];
        long received = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            received += count;
            if (stopwatch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(250) || received == total)
            {
                var rate = stopwatch.Elapsed.TotalSeconds <= 0 ? 0 : received / stopwatch.Elapsed.TotalSeconds;
                progress?.Report(new($"正在从{sourceName}下载", received, total, rate));
                lastReport = stopwatch.Elapsed;
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task ExtractZipSafelyAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("压缩包包含越界路径。");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
            await input.CopyToAsync(output, 1024 * 128, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? FindJdkRoot(string stagingPath)
        => Directory.EnumerateDirectories(stagingPath, "*", SearchOption.AllDirectories)
            .Prepend(stagingPath)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "bin", "java.exe")) && File.Exists(Path.Combine(path, "bin", "javac.exe")));

    private async Task<ManagedToolchainManifest> LoadManifestAsync(CancellationToken cancellationToken)
    {
        await ManifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(ManifestPath)) return new();
            await using var stream = File.OpenRead(ManifestPath);
            return await JsonSerializer.DeserializeAsync<ManagedToolchainManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? new();
        }
        catch (JsonException ex)
        {
            await WriteLogAsync($"托管清单无法解析：{ex.Message}").ConfigureAwait(false);
            throw new InvalidDataException("X-Tool 托管清单已损坏；为避免失去目录所有权记录，安装和卸载已停止。", ex);
        }
        finally { ManifestLock.Release(); }
    }

    private async Task AddManifestEntryAsync(ManagedToolchainEntry entry, CancellationToken cancellationToken)
    {
        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        manifest.Installations.RemoveAll(item => item.ToolchainId == entry.ToolchainId && item.ProviderId == entry.ProviderId && item.Version == entry.Version);
        manifest.Installations.Add(entry);
        await SaveManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveManifestAsync(ManagedToolchainManifest manifest, CancellationToken cancellationToken)
    {
        await ManifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(ManagedRoot);
            var temporary = ManifestPath + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, ManifestPath, overwrite: true);
        }
        finally { ManifestLock.Release(); }
    }

    private string? FindReference(ManagedToolchainEntry entry)
    {
        foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var scope = target == EnvironmentVariableTarget.User ? "当前用户" : "系统";
            var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME", target);
            if (!string.IsNullOrWhiteSpace(javaHome) && PathsEqual(javaHome, entry.InstallationPath))
                return $"{scope} JAVA_HOME 仍指向该 JDK，请先切换环境后再卸载。";
            var path = Environment.GetEnvironmentVariable("Path", target) ?? string.Empty;
            if (path.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(item => IsInsideRoot(Environment.ExpandEnvironmentVariables(item.Trim().Trim('"')), entry.InstallationPath)))
                return $"{scope} PATH 仍引用该 JDK，请先切换环境后再卸载。";
        }
        return null;
    }

    private static bool IsInstallationRunning(string installationPath)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && IsInsideRoot(path, installationPath)) return true;
            }
            catch { /* 普通权限无法读取的进程不作为误判依据。 */ }
            finally { process.Dispose(); }
        }
        return false;
    }

    private static bool IsTrustedTemurinDownload(string url, string fileName, string sha256)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
           string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
           fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
           sha256.Length == 64 && sha256.All(Uri.IsHexDigit);

    private static bool IsTrustedUvRelease(string key, string url)
        => key.StartsWith("cpython-", StringComparison.Ordinal) &&
           key.EndsWith("-windows-x86_64-none", StringComparison.Ordinal) &&
           !key.Any(character => char.IsWhiteSpace(character) || character is '\\' or '/' or '"') &&
           Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
           string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
           uri.AbsolutePath.StartsWith("/astral-sh/python-build-standalone/releases/", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustedNodeRelease(ManagedToolchainRelease release)
        => Version.TryParse(release.Version, out _) && release.FileName == $"node@{release.Version}" &&
           Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
           string.Equals(uri.Host, "nodejs.org", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(uri.AbsolutePath.TrimEnd('/'), $"/dist/v{release.Version}", StringComparison.Ordinal);

    private static bool IsTrustedTunaTemurinMirror(string url, string fileName)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
           string.Equals(uri.Host, "mirrors.tuna.tsinghua.edu.cn", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(Path.GetFileName(uri.AbsolutePath), fileName, StringComparison.Ordinal) &&
           uri.AbsolutePath.StartsWith("/Adoptium/", StringComparison.Ordinal);

    private static bool IsInsideRoot(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static int ParseMajor(string version)
        => int.TryParse(version.Split('.')[0], out var major) ? major : 0;

    private static string SanitizeDirectoryName(string value)
        => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 下次安装会使用新的随机临时文件名。 */ }
    }

    private static void TryDeleteManagedDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* 失败信息已由主操作记录；不会扩大删除范围。 */ }
    }

    private async Task WriteLogAsync(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            await File.AppendAllTextAsync(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}").ConfigureAwait(false);
        }
        catch { /* 日志失败不得覆盖真实安装结果。 */ }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("X-Tool/1.0 (Windows managed toolchain installer)");
        return client;
    }
}
