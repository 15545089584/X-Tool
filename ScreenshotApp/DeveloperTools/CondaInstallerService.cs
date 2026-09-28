using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotApp.DeveloperTools;

/// <summary>从发行方读取带校验值的 Windows 安装器；不接管外部 Conda 环境。</summary>
internal static class CondaInstallerService
{
    private const string MinicondaIndex = "https://repo.anaconda.com/miniconda/";
    private const string MiniforgeApi = "https://api.github.com/repos/conda-forge/miniforge/releases/latest";
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XTool-Conda/1.0");
        return client;
    }

    internal static async Task<ManagedToolchainRelease> GetReleaseAsync(string provider, CancellationToken token)
    {
        if (provider == "miniconda")
            return ParseMiniconda(await Client.GetStringAsync(MinicondaIndex, token).ConfigureAwait(false));
        if (provider != "miniforge") throw new ArgumentException("未知 Conda 发行版。", nameof(provider));
        string releaseJson;
        try { releaseJson = await Client.GetStringAsync(MiniforgeApi, token).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
        {
            return await GetMiniforgeFromReleasePageAsync(token).ConfigureAwait(false);
        }
        using var document = JsonDocument.Parse(releaseJson);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Miniforge 未返回稳定发行版。");
        var version = release.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+-\d+$")) throw new InvalidDataException("无法识别 Miniforge 版本。");
        var name = $"Miniforge3-{version}-Windows-x86_64.exe";
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        var executable = assets.First(a => a.GetProperty("name").GetString() == name);
        var checksum = assets.First(a => a.GetProperty("name").GetString() == name + ".sha256");
        var url = executable.GetProperty("browser_download_url").GetString() ?? "";
        var checksumUrl = checksum.GetProperty("browser_download_url").GetString() ?? "";
        if (!TrustedUrl(provider, url, name) || checksumUrl != url + ".sha256")
            throw new InvalidDataException("Miniforge 下载来源不符合官方发行路径。");
        var hashText = await Client.GetStringAsync(checksumUrl, token).ConfigureAwait(false);
        var hash = ParseChecksum(hashText, name);
        return new ManagedToolchainRelease
        {
            ToolchainId = "conda", ProviderId = provider, DisplayName = "Miniforge3", Version = version,
            FileName = name, DownloadUrl = url, Sha256 = hash, DownloadSize = executable.GetProperty("size").GetInt64(),
            IsRecommended = true, ReleaseChannelText = "最新稳定安装器"
        };
    }

    private static async Task<ManagedToolchainRelease> GetMiniforgeFromReleasePageAsync(CancellationToken token)
    {
        // GitHub 匿名 API 限流时，仅使用官方 latest 重定向解析固定版本。
        using var response = await Client.GetAsync("https://github.com/conda-forge/miniforge/releases/latest",
            HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var uri = response.RequestMessage?.RequestUri;
        var match = Regex.Match(uri?.AbsolutePath ?? "", @"\A/conda-forge/miniforge/releases/tag/(?<version>\d+\.\d+\.\d+-\d+)\z");
        if (uri?.Scheme != "https" || uri.Host != "github.com" || !match.Success)
            throw new InvalidDataException("无法从官方发行页确认 Miniforge 稳定版本。");
        var version = match.Groups["version"].Value;
        var name = $"Miniforge3-{version}-Windows-x86_64.exe";
        var url = $"https://github.com/conda-forge/miniforge/releases/download/{version}/{name}";
        var hash = ParseChecksum(await Client.GetStringAsync(url + ".sha256", token).ConfigureAwait(false), name);
        return new ManagedToolchainRelease
        {
            ToolchainId = "conda", ProviderId = "miniforge", DisplayName = "Miniforge3", Version = version,
            FileName = name, DownloadUrl = url, Sha256 = hash,
            IsRecommended = true, ReleaseChannelText = "最新稳定安装器"
        };
    }

    internal static ManagedToolchainRelease ParseMiniconda(string html)
    {
        var rows = Regex.Matches(html, @"<tr\b[^>]*>(?<row>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        string? latestHash = null;
        foreach (Match row in rows)
        {
            if (!row.Value.Contains("href=\"Miniconda3-latest-Windows-x86_64.exe\"", StringComparison.Ordinal)) continue;
            var hash = Regex.Match(row.Value, @"<td[^>]*>\s*(?<hash>[a-fA-F0-9]{64})\s*</td>");
            if (hash.Success) latestHash = hash.Groups["hash"].Value;
        }
        if (latestHash is null) throw new InvalidDataException("官方目录缺少 Miniconda Windows 安装器校验值。");
        foreach (Match row in rows)
        {
            var file = Regex.Match(row.Value, "href=\"(?<name>Miniconda3-(?<version>py[0-9]+_[0-9.]+-[0-9]+)-Windows-x86_64.exe)\"");
            if (!file.Success || !row.Value.Contains(latestHash, StringComparison.OrdinalIgnoreCase)) continue;
            var size = Regex.Match(row.Value, @"class=""s"">(?<size>[0-9.]+)(?<unit>[MGK])</td>");
            var bytes = size.Success ? double.Parse(size.Groups["size"].Value, CultureInfo.InvariantCulture) *
                (size.Groups["unit"].Value == "G" ? 1024 * 1024 * 1024 : size.Groups["unit"].Value == "M" ? 1024 * 1024 : 1024) : 0;
            var name = file.Groups["name"].Value;
            return new ManagedToolchainRelease
            {
                ToolchainId = "conda", ProviderId = "miniconda", DisplayName = "Miniconda3",
                Version = file.Groups["version"].Value, FileName = name, DownloadUrl = MinicondaIndex + name,
                Sha256 = latestHash, DownloadSize = (long)bytes, IsRecommended = true, ReleaseChannelText = "最新稳定安装器"
            };
        }
        throw new InvalidDataException("无法将 Miniconda 最新校验值匹配到固定版本安装器。");
    }

    internal static string ParseChecksum(string text, string fileName)
    {
        var match = Regex.Match(text.Trim(), @"\A(?<hash>[a-fA-F0-9]{64})\s+\*?(?<name>[^\r\n]+)\z");
        if (!match.Success || match.Groups["name"].Value != fileName)
            throw new InvalidDataException("安装器校验文件不匹配。");
        return match.Groups["hash"].Value;
    }

    internal static bool TrustedUrl(string provider, string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        if (provider == "miniconda")
            return Regex.IsMatch(name, @"\AMiniconda3-py\d+_[0-9.]+-\d+-Windows-x86_64\.exe\z") &&
                   uri.Host == "repo.anaconda.com" && uri.AbsolutePath == "/miniconda/" + name;
        if (provider == "miniforge")
        {
            var match = Regex.Match(name, @"\AMiniforge3-(?<version>\d+\.\d+\.\d+-\d+)-Windows-x86_64\.exe\z");
            return match.Success && uri.Host == "github.com" && uri.AbsolutePath ==
                $"/conda-forge/miniforge/releases/download/{match.Groups["version"].Value}/{name}";
        }
        return false;
    }

    internal static async Task<ManagedToolchainOperationResult> LaunchInstallerAsync(
        ManagedToolchainRelease release, IProgress<ManagedInstallProgress>? progress, CancellationToken token)
    {
        if (release.ToolchainId != "conda" || !TrustedUrl(release.ProviderId, release.DownloadUrl, release.FileName) ||
            !Regex.IsMatch(release.Sha256, @"\A[a-fA-F0-9]{64}\z"))
            return new(false, "Conda 安装器来源或校验信息不可信。");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "X-Tool", "Dev", "Downloads", "Conda", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(folder, release.FileName);
        var partial = destination + ".partial";
        var launched = false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            Directory.CreateDirectory(folder);
            using var response = await Client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidDataException("拒绝非 HTTPS 下载重定向。");
            var total = response.Content.Headers.ContentLength ?? 0;
            if (total > 512L * 1024 * 1024) throw new InvalidDataException("安装器超出 512 MB 限额。");
            await using (var input = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                var elapsed = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                int count;
                while ((count = await input.ReadAsync(buffer, budget.Token).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (received > 512L * 1024 * 1024) throw new InvalidDataException("安装器超出 512 MB 限额。");
                    await output.WriteAsync(buffer.AsMemory(0, count), budget.Token).ConfigureAwait(false);
                    if (elapsed.Elapsed - lastReport >= TimeSpan.FromMilliseconds(250))
                    {
                        progress?.Report(new("正在下载安装器", received, total, received / Math.Max(.001, elapsed.Elapsed.TotalSeconds)));
                        lastReport = elapsed.Elapsed;
                    }
                }
            }
            progress?.Report(new("正在核对官方 SHA-256"));
            await using (var stream = File.OpenRead(partial))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, budget.Token).ConfigureAwait(false));
                if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 不匹配，已拒绝执行安装器。");
            }
            budget.Token.ThrowIfCancellationRequested();
            File.Move(partial, destination);
            // 用户已在界面确认；只打开交互式向导，不传递静默安装或环境变量参数。
            using var process = Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
            launched = process is not null;
            return new(launched, launched
                ? $"{release.DisplayName} 安装向导已打开；完成后请重新扫描。安装目录、环境和卸载由发行版管理。"
                : "安装向导未能启动。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return new(false, $"Conda 安装器处理失败：{exception.Message}"); }
        finally
        {
            // 仅回收本次下载创建的文件，绝不删除用户安装目录。
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            if (!launched)
            {
                try { if (File.Exists(destination)) File.Delete(destination); Directory.Delete(folder); } catch { }
            }
        }
    }
}
