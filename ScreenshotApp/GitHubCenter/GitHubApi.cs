using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ScreenshotApp.GitHubCenter;

public sealed record RemoteRepository(string Name, string Url, string CloneUrl, bool Private)
{
    public string Label => $"{Name}  · {(Private ? "私有" : "公开")}";
}
public sealed record GitHubActivity(string Title, string Status, string Url)
{
    public string Label => $"{Title}\n{Status}";
}
public sealed record GitHubOverview(List<GitHubActivity> PullRequests, List<GitHubActivity> Runs, string PullStatus, string RunStatus);

public sealed class GitHubApi : IDisposable
{
    private readonly HttpClient _client;
    public GitHubApi(HttpMessageHandler? handler = null)
    {
        _client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }
    private async Task<JsonDocument> GetAsync(string path, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.github.com/" + path));
        request.Headers.UserAgent.ParseAdd("X-Tool-GitHubCenter/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "GitHub 授权无效或已过期，请重新连接账户。",
                HttpStatusCode.Forbidden => "GitHub 拒绝访问：请检查令牌权限、组织审批或请求额度。",
                HttpStatusCode.NotFound => "仓库不存在，或当前令牌无权访问。",
                HttpStatusCode.TooManyRequests => "GitHub 请求受限，请稍后重试。",
                _ => $"GitHub 请求失败（HTTP {(int)response.StatusCode}）。"
            };
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0") error = "GitHub API 额度已耗尽，请稍后重试。";
            throw new HttpRequestException(error, null, response.StatusCode);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("GitHub 响应过大，已停止读取。");
            memory.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(memory.ToArray());
    }
    public async Task<string> LoginAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("请输入 Personal Access Token。");
        using var json = await GetAsync("user", token.Trim(), ct);
        return json.RootElement.GetProperty("login").GetString() ?? throw new InvalidDataException("GitHub 账户响应无效。");
    }
    public async Task<List<RemoteRepository>> RepositoriesAsync(string token, int page, CancellationToken ct)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        using var json = await GetAsync($"user/repos?sort=updated&per_page=100&page={page}", token, ct);
        return json.RootElement.EnumerateArray().Select(e => new RemoteRepository(Text(e, "full_name"), Text(e, "html_url"), Text(e, "clone_url"), e.GetProperty("private").GetBoolean())).ToList();
    }
    public async Task<GitHubOverview> OverviewAsync(string slug, string? token, CancellationToken ct)
    {
        if (GitRepositoryService.GitHubSlug("https://github.com/" + slug) != slug) throw new InvalidOperationException("GitHub 仓库地址无效。");
        var pulls = new List<GitHubActivity>();
        var runs = new List<GitHubActivity>();
        string ps = "开放的 PR（最多 30 条）", rs = "最近构建（最多 20 条）";
        try
        {
            using var json = await GetAsync($"repos/{slug}/pulls?state=open&per_page=30", token, ct);
            pulls.AddRange(json.RootElement.EnumerateArray().Select(e => new GitHubActivity($"#{e.GetProperty("number")} {Text(e, "title")}", $"{Text(e.GetProperty("head"), "ref")} → {Text(e.GetProperty("base"), "ref")}" + (e.GetProperty("draft").GetBoolean() ? " · 草稿" : " · 开放"), Text(e, "html_url"))));
        }
        catch (HttpRequestException ex) { ps = ex.Message; }
        try
        {
            using var json = await GetAsync($"repos/{slug}/actions/runs?per_page=20", token, ct);
            runs.AddRange(json.RootElement.GetProperty("workflow_runs").EnumerateArray().Select(e => new GitHubActivity(Text(e, "display_title"), $"{Text(e, "name")} · {Text(e, "head_branch")} · {Text(e, "status")} / {Text(e, "conclusion")}", Text(e, "html_url"))));
        }
        catch (HttpRequestException ex) { rs = ex.Message; }
        return new(pulls, runs, ps, rs);
    }
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    public void Dispose() => _client.Dispose();
}
