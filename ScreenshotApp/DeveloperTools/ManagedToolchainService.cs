using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Microsoft.Win32;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ScreenshotApp.DeveloperTools;

/// <summary>管理 X-Tool 自己下载的开发工具；不会接管或删除外部安装。</summary>
public sealed class ManagedToolchainService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim ManifestLock = new(1, 1);
    private const string AdoptiumAvailableReleasesUrl = "https://api.adoptium.net/v3/info/available_releases";
    private const string TunaAdoptiumRoot = "https://mirrors.tuna.tsinghua.edu.cn/Adoptium";
    private const string MysqlEolApiUrl = "https://endoflife.date/api/mysql.json";
    private const string MysqlArchivesRoot = "https://cdn.mysql.com/archives/mysql-";
    private const string DockerDesktopAppcastUrl = "https://desktop.docker.com/win/main/amd64/appcast.xml";
    private const string DockerCliDirectoryUrl = "https://download.docker.com/win/static/stable/x86_64/";
    // MySQL 生命周期基线更新于 2026-08：8.4 与 9.7 为 LTS，其余 9.x 为创新版，8.0 及更早已停止维护。
    private static readonly HashSet<string> MysqlLtsCycles = new(StringComparer.Ordinal) { "8.4", "9.7" };
    // Python 官方支持状态基线更新于 2026-08；uv 目录是否仍提供对应构建仍以实际查询结果为准。
    private static readonly int[] PythonMinors = { 8, 9, 10, 11, 12, 13, 14 };
    private static readonly HashSet<int> SupportedPythonMinors = new() { 10, 11, 12, 13, 14 };
    private readonly SafeDeveloperCommandRunner _commandRunner = new();

    public ManagedDownloadSource TemurinDownloadSource { get; set; } = ManagedDownloadSource.Official;
    public string TemurinDownloadSourceText => TemurinDownloadSource == ManagedDownloadSource.Tuna
        ? "清华 TUNA 镜像（失败回退官方源）"
        : "Eclipse Adoptium 官方源";

    public string ManagedRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "Dev");

    public string OperationLogPath => Path.Combine(ManagedRoot, "Logs", "managed-toolchains.log");

    private string ManifestPath => Path.Combine(ManagedRoot, "managed-tools.json");
    private string DownloadsRoot => Path.Combine(ManagedRoot, "Downloads");
    private string JavaRoot => Path.Combine(ManagedRoot, "Java");
    private string PythonRoot => Path.Combine(ManagedRoot, "Python", "uv");
    private string MysqlRoot => Path.Combine(ManagedRoot, "MySQL");
    private string DockerRoot => Path.Combine(ManagedRoot, "Docker");
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
    private string LogPath => OperationLogPath;

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
                    ReleaseChannelText = major == currentMajor && !ltsMajors.Contains(major) ? "最新特性版" : string.Empty,
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
            var isSupported = SupportedPythonMinors.Contains(minor);
            releases.Add(new ManagedToolchainRelease
            {
                ToolchainId = "python",
                ProviderId = "uv",
                DisplayName = $"Python 3.{minor}",
                Version = version,
                Architecture = "x64",
                DownloadUrl = urlText,
                FileName = key,
                FeatureVersion = minor,
                IsRecommended = isSupported,
                ReleaseChannelText = isSupported ? "官方支持" : "历史兼容",
                IsInstalled = manifest.Installations.Any(item => item.ManagedByXTool && item.ToolchainId == "python" &&
                    item.ProviderId == "uv" && item.Version == version && Directory.Exists(item.InstallationPath))
            });
        }
        foreach (var entry in manifest.Installations.Where(item => item.ManagedByXTool && item.ProviderId == "uv" &&
                     Directory.Exists(item.InstallationPath) && !releases.Any(release => release.Version == item.Version)))
        {
            if (!IsTrustedUvRelease(entry.PackageKey, entry.DownloadUrl)) continue;
            var minor = ParsePythonMinor(entry.Version);
            var isSupported = SupportedPythonMinors.Contains(minor);
            releases.Add(new ManagedToolchainRelease
            {
                ToolchainId = "python", ProviderId = "uv", DisplayName = $"Python {entry.Version}", Version = entry.Version,
                Architecture = entry.Architecture, DownloadUrl = entry.DownloadUrl, FileName = entry.PackageKey,
                FeatureVersion = minor, IsRecommended = isSupported,
                ReleaseChannelText = isSupported ? "官方支持" : "历史兼容", IsInstalled = true
            });
        }
        return releases.OrderByDescending(item => item.IsRecommended).ThenByDescending(item => item.FeatureVersion).ToList();
    }

    public Task<ManagedToolchainOperationResult> InstallAsync(
        ManagedToolchainRelease release, IProgress<ManagedInstallProgress>? progress, CancellationToken cancellationToken)
        => release.ProviderId switch
        {
            "temurin" => InstallTemurinAsync(release, progress, cancellationToken),
            "uv" => InstallUvPythonAsync(release, progress, cancellationToken),
            "mysql" => InstallMysqlAsync(release, progress, cancellationToken),
            "docker-desktop" => InstallDockerDesktopAsync(release, progress, cancellationToken),
            "docker-cli" => InstallDockerCliAsync(release, progress, cancellationToken),
            "volta" => FetchVoltaNodeAsync(release, progress, cancellationToken),
            _ => Task.FromResult(new ManagedToolchainOperationResult(false, "不支持该托管来源。"))
        };

    public async Task<IReadOnlyList<ManagedToolchainRelease>> GetDockerDesktopReleasesAsync(CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(DockerDesktopAppcastUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var document = XDocument.Parse(content);
        var item = document.Root?.Element("channel")?.Element("item");
        var enclosure = item?.Element("enclosure");
        var sparkleNamespace = XNamespace.Get("http://www.andymatuschak.org/xml-namespaces/sparkle");
        var version = item?.Element("title")?.Value.Trim();
        var url = enclosure?.Attribute("url")?.Value;
        var lengthText = enclosure?.Attribute("length")?.Value;
        var versionText = enclosure?.Attribute(sparkleNamespace + "shortVersionString")?.Value;
        if (string.IsNullOrWhiteSpace(url) || !IsTrustedDockerDesktopUrl(url))
        {
            return Array.Empty<ManagedToolchainRelease>();
        }

        var installed = IsDockerDesktopInstalled(out var installedVersion);
        return new[]
        {
            new ManagedToolchainRelease
            {
                ToolchainId = "docker",
                ProviderId = "docker-desktop",
                DisplayName = "Docker Desktop",
                Version = string.IsNullOrWhiteSpace(versionText) ? version ?? "未知" : versionText,
                Architecture = "x64",
                DownloadUrl = url,
                FileName = "Docker Desktop Installer.exe",
                DownloadSize = long.TryParse(lengthText, out var length) ? length : 0,
                IsRecommended = true,
                ReleaseChannelText = installed ? $"本机 {installedVersion}" : "官方更新源",
                IsInstalled = installed,
                IsReadOnlyInstalled = installed
            }
        };
    }

    public async Task<IReadOnlyList<ManagedToolchainRelease>> GetDockerCliReleasesAsync(CancellationToken cancellationToken)
    {
        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        using var response = await HttpClient.GetAsync(DockerCliDirectoryUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var versions = Regex.Matches(content, @"docker-(?<version>[0-9]+\.[0-9]+\.[0-9]+)\.zip")
            .Select(match => match.Groups["version"].Value)
            .Where(value => Version.TryParse(value, out _))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => Version.Parse(value))
            .Take(1)
            .ToArray();
        if (versions.Length == 0)
        {
            return Array.Empty<ManagedToolchainRelease>();
        }

        var version = versions[0];
        var fileName = $"docker-{version}.zip";
        var zipUrl = $"{DockerCliDirectoryUrl}{fileName}";
        long size = 0;
        try
        {
            using var head = await HttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, zipUrl), cancellationToken).ConfigureAwait(false);
            if (head.IsSuccessStatusCode) size = head.Content.Headers.ContentLength ?? 0;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 大小未知时仍允许安装，下载时以实际为准。 */ }

        return new[]
        {
            new ManagedToolchainRelease
            {
                ToolchainId = "docker",
                ProviderId = "docker-cli",
                DisplayName = "docker CLI",
                Version = version,
                Architecture = "x64",
                DownloadUrl = zipUrl,
                FileName = fileName,
                DownloadSize = size,
                IsRecommended = true,
                IsInstalled = manifest.Installations.Any(item => item.ManagedByXTool &&
                    item.ToolchainId == "docker" && item.ProviderId == "docker-cli" &&
                    item.Version == version && Directory.Exists(item.InstallationPath))
            }
        };
    }

    public async Task<IReadOnlyList<ManagedToolchainRelease>> GetMysqlReleasesAsync(CancellationToken cancellationToken)
    {
        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        using var response = await HttpClient.GetAsync(MysqlEolApiUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var cycles = new List<MysqlCycle>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("cycle", out var cycleElement) || !item.TryGetProperty("latest", out var latestElement)) continue;
            var cycle = cycleElement.GetString();
            var latest = latestElement.GetString();
            if (string.IsNullOrWhiteSpace(cycle) || string.IsNullOrWhiteSpace(latest)) continue;
            if (!TryParseMysqlCycle(cycle, out var major)) continue;
            if (major < 8) continue; // 5.x 及更早版本不列入托管下载。
            var supported = IsMysqlCycleSupported(item);
            if (!supported && cycle != "8.0") continue; // 历史兼容区只保留 8.0。
            cycles.Add(new MysqlCycle(cycle, latest, supported, MysqlLtsCycles.Contains(cycle)));
        }

        using var gate = new SemaphoreSlim(3);
        var tasks = cycles.Select(async cycle =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ProbeMysqlReleaseAsync(cycle, manifest, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
            finally
            {
                gate.Release();
            }
        });

        var releases = (await Task.WhenAll(tasks).ConfigureAwait(false))
            .Where(item => item is not null)
            .Cast<ManagedToolchainRelease>()
            .OrderByDescending(item => item.IsRecommended)
            .ThenByDescending(item => item.IsLts)
            .ThenByDescending(item => item.Version)
            .ToList();
        return releases;
    }

    private async Task<ManagedToolchainRelease?> ProbeMysqlReleaseAsync(
        MysqlCycle cycle,
        ManagedToolchainManifest manifest,
        CancellationToken cancellationToken)
    {
        // endoflife 的最新补丁号可能超前于 CDN 归档，从 latest 向下探测最多 5 个补丁。
        for (var offset = 0; offset < 5; offset++)
        {
            var version = DecrementPatchVersion(cycle.Latest, offset);
            if (version is null) return null;
            var fileName = $"mysql-{version}-winx64.zip";
            var zipUrl = $"{MysqlArchivesRoot}{cycle.Cycle}/{fileName}";
            try
            {
                using var head = await HttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, zipUrl), cancellationToken).ConfigureAwait(false);
                if (!head.IsSuccessStatusCode) continue;
                var size = head.Content.Headers.ContentLength ?? 0;
                var md5 = await FetchMysqlMd5Async(zipUrl, cancellationToken).ConfigureAwait(false);
                if (md5 is null || !IsTrustedMysqlDownload(zipUrl, fileName, md5)) continue;

                return new ManagedToolchainRelease
                {
                    ToolchainId = "mysql",
                    ProviderId = "mysql",
                    DisplayName = $"MySQL {cycle.Cycle}",
                    Version = version,
                    Architecture = "x64",
                    DownloadUrl = zipUrl,
                    Sha256 = md5,
                    HashAlgorithm = "MD5",
                    FileName = fileName,
                    DownloadSize = size,
                    FeatureVersion = ParseMajor(version),
                    IsLts = cycle.IsLts,
                    IsRecommended = cycle.IsSupported,
                    ReleaseChannelText = cycle.IsLts ? string.Empty : cycle.IsSupported ? "创新版" : "停止维护",
                    IsInstalled = manifest.Installations.Any(item => item.ManagedByXTool &&
                        item.ToolchainId == "mysql" && item.ProviderId == "mysql" &&
                        item.Version == version && Directory.Exists(item.InstallationPath))
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }
        }
        return null;
    }

    private static async Task<string?> FetchMysqlMd5Async(string zipUrl, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(zipUrl + ".md5", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var match = Regex.Match(content, @"\b[0-9a-fA-F]{32}\b");
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private static bool TryParseMysqlCycle(string cycle, out int major)
    {
        major = 0;
        var parts = cycle.Split('.');
        return parts.Length >= 1 && int.TryParse(parts[0], out major);
    }

    private static bool IsMysqlCycleSupported(JsonElement item)
    {
        if (!item.TryGetProperty("support", out var support)) return false;
        if (support.ValueKind == JsonValueKind.True) return true;
        if (support.ValueKind == JsonValueKind.False) return false;
        var text = support.GetString();
        return DateTime.TryParse(text, out var date) && date.Date >= DateTime.UtcNow.Date;
    }

    private static string? DecrementPatchVersion(string version, int offset)
    {
        if (!Version.TryParse(version, out var parsed) || parsed.Build < 0) return null;
        var patch = parsed.Build - offset;
        if (patch < 0) return null;
        return $"{parsed.Major}.{parsed.Minor}.{patch}";
    }

    private sealed record MysqlCycle(string Cycle, string Latest, bool IsSupported, bool IsLts);

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

    public async Task<ManagedToolchainOperationResult> InstallMysqlAsync(
        ManagedToolchainRelease release,
        IProgress<ManagedInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (release.ToolchainId != "mysql" || release.ProviderId != "mysql" ||
            !string.Equals(release.HashAlgorithm, "MD5", StringComparison.OrdinalIgnoreCase) ||
            !IsTrustedMysqlDownload(release.DownloadUrl, release.FileName, release.Sha256))
        {
            return new(false, "安装请求不是受信任的 MySQL 官方归档 ZIP。");
        }

        Directory.CreateDirectory(DownloadsRoot);
        Directory.CreateDirectory(MysqlRoot);
        var operationId = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(DownloadsRoot, $"{operationId}.partial");
        var stagingPath = Path.Combine(MysqlRoot, $".staging-{operationId}");
        string? installedPath = null;
        var manifestCommitted = false;
        try
        {
            progress?.Report(new("正在连接 MySQL 官方 CDN"));
            await DownloadAsync(release.DownloadUrl, "MySQL 官方 CDN", archivePath, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("正在校验 MD5"));
            var actualHash = await ComputeMd5Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await WriteLogAsync($"MySQL {release.Version} 校验失败：期望 {release.Sha256}，实际 {actualHash}").ConfigureAwait(false);
                return new(false, "下载文件的 MD5 与 MySQL 官方校验值不一致，安装已停止。");
            }

            progress?.Report(new("正在安全解压 MySQL"));
            Directory.CreateDirectory(stagingPath);
            await ExtractZipSafelyAsync(archivePath, stagingPath, cancellationToken).ConfigureAwait(false);
            var extractedRoot = FindMysqlRoot(stagingPath);
            if (extractedRoot is null)
            {
                return new(false, "压缩包内未找到完整的 mysql.exe 与 mysqld.exe。");
            }

            var safeVersion = SanitizeDirectoryName(release.Version);
            var destination = Path.Combine(MysqlRoot, $"mysql-{safeVersion}-winx64");
            if (Directory.Exists(destination))
            {
                return new(false, "该 MySQL 版本的托管目录已经存在，请先重新扫描或卸载旧记录。");
            }

            Directory.Move(extractedRoot, destination);
            installedPath = destination;
            var mysqlPath = Path.Combine(destination, "bin", "mysql.exe");
            var validation = await _commandRunner.RunAsync(mysqlPath, new[] { "--version" }, cancellationToken).ConfigureAwait(false);
            if (validation.TimedOut || validation.ExitCode != 0)
            {
                TryDeleteManagedDirectory(destination);
                return new(false, "MySQL 解压完成，但 mysql --version 验证失败（可能缺少 VC++ 运行库），已撤销本次安装。");
            }

            var entry = new ManagedToolchainEntry
            {
                ToolchainId = "mysql",
                ProviderId = "mysql",
                Version = release.Version,
                Architecture = release.Architecture,
                InstallationPath = destination,
                ExecutablePath = mysqlPath,
                DownloadUrl = release.DownloadUrl,
                Sha256 = release.Sha256,
                HashAlgorithm = "MD5",
                InstalledAtUtc = DateTime.UtcNow,
                ManagedByXTool = true
            };
            await AddManifestEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            manifestCommitted = true;
            await WriteLogAsync($"已安装 MySQL {release.Version} 到 {destination}；校验：MD5 {release.Sha256}").ConfigureAwait(false);
            progress?.Report(new("安装与版本验证完成", release.DownloadSize, release.DownloadSize));
            return new(true, $"MySQL {release.Version} 已安装并通过 mysql --version 验证。", entry);
        }
        catch (OperationCanceledException)
        {
            await WriteLogAsync($"MySQL {release.Version} 安装已取消").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await WriteLogAsync($"MySQL {release.Version} 安装失败：{ex}").ConfigureAwait(false);
            return new(false, $"安装失败：{ex.Message}");
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteManagedDirectory(stagingPath);
            if (!manifestCommitted && !string.IsNullOrWhiteSpace(installedPath)) TryDeleteManagedDirectory(installedPath);
        }
    }

    public async Task<ManagedToolchainOperationResult> InstallDockerDesktopAsync(
        ManagedToolchainRelease release,
        IProgress<ManagedInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (release.ToolchainId != "docker" || release.ProviderId != "docker-desktop" ||
            !IsTrustedDockerDesktopUrl(release.DownloadUrl))
        {
            return new(false, "安装请求不是受信任的 Docker Desktop 官方安装器。");
        }

        Directory.CreateDirectory(DownloadsRoot);
        var installerPath = Path.Combine(DownloadsRoot, $"docker-desktop-installer-{SanitizeDirectoryName(release.Version)}.exe");
        var temporary = installerPath + ".partial";
        try
        {
            progress?.Report(new("正在下载 Docker Desktop 安装器"));
            await DownloadAsync(release.DownloadUrl, "Docker Desktop 官方更新源", temporary, progress, cancellationToken).ConfigureAwait(false);
            if (File.Exists(installerPath))
            {
                TryDeleteFile(installerPath);
            }
            File.Move(temporary, installerPath);

            progress?.Report(new("正在校验官方数字签名"));
            if (!VerifyDockerDesktopSignature(installerPath))
            {
                TryDeleteFile(installerPath);
                return new(false, "安装器数字签名验证失败（非 Docker, Inc. 签名或签名无效），文件已删除。");
            }

            await WriteLogAsync($"已下载 Docker Desktop {release.Version} 安装器：{installerPath}；签名验证通过").ConfigureAwait(false);
            progress?.Report(new("正在启动安装向导"));
            Process.Start(new ProcessStartInfo { FileName = installerPath, UseShellExecute = true });
            return new(true, $"Docker Desktop {release.Version} 安装器已启动，请按向导完成安装（需要管理员权限与 WSL2）。");
        }
        catch (OperationCanceledException)
        {
            await WriteLogAsync($"Docker Desktop {release.Version} 下载已取消").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await WriteLogAsync($"Docker Desktop {release.Version} 下载失败：{ex}").ConfigureAwait(false);
            return new(false, $"下载失败：{ex.Message}");
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    public async Task<ManagedToolchainOperationResult> InstallDockerCliAsync(
        ManagedToolchainRelease release,
        IProgress<ManagedInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (release.ToolchainId != "docker" || release.ProviderId != "docker-cli" ||
            !IsTrustedDockerCliDownload(release.DownloadUrl, release.FileName))
        {
            return new(false, "安装请求不是受信任的 Docker 官方静态包。");
        }

        Directory.CreateDirectory(DownloadsRoot);
        Directory.CreateDirectory(DockerRoot);
        var operationId = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(DownloadsRoot, $"{operationId}.partial");
        var stagingPath = Path.Combine(DockerRoot, $".staging-{operationId}");
        string? installedPath = null;
        var manifestCommitted = false;
        try
        {
            progress?.Report(new("正在下载 docker CLI 静态包"));
            await DownloadAsync(release.DownloadUrl, "Docker 官方静态包", archivePath, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("正在安全解压"));
            Directory.CreateDirectory(stagingPath);
            await ExtractZipSafelyAsync(archivePath, stagingPath, cancellationToken).ConfigureAwait(false);
            var extractedRoot = FindDockerCliRoot(stagingPath);
            if (extractedRoot is null)
            {
                return new(false, "压缩包内未找到 docker.exe。");
            }

            var safeVersion = SanitizeDirectoryName(release.Version);
            var destination = Path.Combine(DockerRoot, $"docker-{safeVersion}");
            if (Directory.Exists(destination))
            {
                return new(false, "该 docker CLI 版本的托管目录已经存在，请先重新扫描或卸载旧记录。");
            }

            Directory.Move(extractedRoot, destination);
            installedPath = destination;
            var dockerPath = Path.Combine(destination, "docker.exe");
            var validation = await _commandRunner.RunAsync(dockerPath, new[] { "--version" }, cancellationToken).ConfigureAwait(false);
            if (validation.TimedOut || validation.ExitCode != 0)
            {
                TryDeleteManagedDirectory(destination);
                return new(false, "docker CLI 解压完成，但 docker --version 验证失败，已撤销本次安装。");
            }

            var entry = new ManagedToolchainEntry
            {
                ToolchainId = "docker",
                ProviderId = "docker-cli",
                Version = release.Version,
                Architecture = release.Architecture,
                InstallationPath = destination,
                ExecutablePath = dockerPath,
                DownloadUrl = release.DownloadUrl,
                InstalledAtUtc = DateTime.UtcNow,
                ManagedByXTool = true
            };
            await AddManifestEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            manifestCommitted = true;
            await WriteLogAsync($"已安装 docker CLI {release.Version} 到 {destination}").ConfigureAwait(false);
            progress?.Report(new("安装与版本验证完成", release.DownloadSize, release.DownloadSize));
            return new(true, $"docker CLI {release.Version} 已安装并通过 docker --version 验证。", entry);
        }
        catch (OperationCanceledException)
        {
            await WriteLogAsync($"docker CLI {release.Version} 安装已取消").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await WriteLogAsync($"docker CLI {release.Version} 安装失败：{ex}").ConfigureAwait(false);
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
        var expectedRoot = entry.ToolchainId switch
        {
            "java" => JavaRoot,
            "python" => PythonRoot,
            "mysql" => MysqlRoot,
            "docker" => DockerRoot,
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(expectedRoot)) return new(false, "该托管类型没有可删除的安装目录。");
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

    private static async Task<string> ComputeMd5Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        using var md5 = MD5.Create();
        var hash = await md5.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool VerifyDockerDesktopSignature(string path)
    {
        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            if (!certificate.Subject.Contains("Docker, Inc.", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.VerificationTime = DateTime.Now;
            return chain.Build(certificate);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDockerDesktopInstalled(out string version)
    {
        version = string.Empty;
        try
        {
            foreach (var uninstallRoot in new[]
                     {
                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                     })
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var uninstall = baseKey.OpenSubKey(uninstallRoot);
                foreach (var keyName in uninstall?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var productKey = uninstall?.OpenSubKey(keyName);
                    var displayName = productKey?.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrWhiteSpace(displayName) ||
                        !displayName.Contains("Docker Desktop", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    version = productKey?.GetValue("DisplayVersion")?.ToString() ?? string.Empty;
                    return true;
                }
            }
        }
        catch
        {
            // 注册表不可读时退回目录存在性判断。
        }

        return File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker", "Docker", "resources", "bin", "docker.exe"));
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

    private static string? FindMysqlRoot(string stagingPath)
        => Directory.EnumerateDirectories(stagingPath, "*", SearchOption.AllDirectories)
            .Prepend(stagingPath)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "bin", "mysql.exe")) && File.Exists(Path.Combine(path, "bin", "mysqld.exe")));

    private static string? FindDockerCliRoot(string stagingPath)
        => Directory.EnumerateDirectories(stagingPath, "*", SearchOption.AllDirectories)
            .Prepend(stagingPath)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "docker.exe")));

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
        var homeVariables = entry.ToolchainId switch
        {
            "java" => new[] { "JAVA_HOME", "JDK_HOME" },
            "mysql" => new[] { "MYSQL_HOME" },
            _ => Array.Empty<string>()
        };
        foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var scope = target == EnvironmentVariableTarget.User ? "当前用户" : "系统";
            foreach (var variable in homeVariables)
            {
                var value = Environment.GetEnvironmentVariable(variable, target);
                if (!string.IsNullOrWhiteSpace(value) && PathsEqual(value, entry.InstallationPath))
                    return $"{scope} {variable} 仍指向该安装，请先切换环境后再卸载。";
            }
            var path = Environment.GetEnvironmentVariable("Path", target) ?? string.Empty;
            if (path.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(item => IsInsideRoot(Environment.ExpandEnvironmentVariables(item.Trim().Trim('"')), entry.InstallationPath)))
                return $"{scope} PATH 仍引用该安装，请先切换环境后再卸载。";
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

    private static bool IsTrustedMysqlDownload(string url, string fileName, string md5)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
           string.Equals(uri.Host, "cdn.mysql.com", StringComparison.OrdinalIgnoreCase) &&
           uri.AbsolutePath.StartsWith("/archives/mysql-", StringComparison.OrdinalIgnoreCase) &&
           Regex.IsMatch(fileName, @"^mysql-[0-9]+\.[0-9]+\.[0-9]+-winx64\.zip$", RegexOptions.IgnoreCase) &&
           md5.Length == 32 && md5.All(Uri.IsHexDigit);

    private static bool IsTrustedDockerDesktopUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "desktop.docker.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // AbsolutePath 会保留 %20 转义，先还原空格再匹配安装器文件名。
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        return path.StartsWith("/win/main/amd64/", StringComparison.OrdinalIgnoreCase) &&
               path.EndsWith("/Docker Desktop Installer.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrustedDockerCliDownload(string url, string fileName)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
           string.Equals(uri.Host, "download.docker.com", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(Path.GetDirectoryName(uri.AbsolutePath), "/win/static/stable/x86_64", StringComparison.OrdinalIgnoreCase) &&
           Regex.IsMatch(fileName, @"^docker-[0-9]+\.[0-9]+\.[0-9]+\.zip$", RegexOptions.IgnoreCase) &&
           string.Equals(Path.GetFileName(uri.AbsolutePath), fileName, StringComparison.OrdinalIgnoreCase);

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

    private static int ParsePythonMinor(string version)
    {
        var parts = version.Split('.');
        return parts.Length > 1 && int.TryParse(parts[1], out var minor) ? minor : 0;
    }

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
