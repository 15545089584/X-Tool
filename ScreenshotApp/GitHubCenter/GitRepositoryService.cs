using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotApp.GitHubCenter;

public sealed record GitChange(string Path, string OriginalPath, string Code, bool Staged, bool Conflict)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string DirectoryName => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";
    public string StatusLabel => Conflict ? "冲突" : Staged ? "已暂存" : Code == "?" ? "未跟踪" : "未暂存";
    public string Label => $"{(Conflict ? "冲突" : Staged ? "已暂存" : "未暂存")}  {Code}  {Path}";
}
public sealed record GitCommit(string Id, string Author, string Date, string Subject)
{
    public string DisplayTime => DateTimeOffset.TryParse(Date, out var time) ? time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : Date;
    public string ShortId => Id[..Math.Min(8, Id.Length)];
    public string Label => $"{Id[..Math.Min(8, Id.Length)]}  {Subject}  · {Author} · {Date}";
}
public sealed record GitState(string Root, string Branch, string Head, string Upstream, string Divergence,
    IReadOnlyList<GitChange> Changes, string Raw)
{
    public string Summary => $"{Branch} · {Changes.Select(c => c.Path).Distinct().Count()} 个改动 · {(Upstream.Length == 0 ? "无上游" : Divergence.Length == 0 ? "上游计数未知" : Divergence)}";
}
public sealed record GitPushPlan(string Remote, string Target, string Urls, string Head, string Preview);
public sealed record GitCommitPlan(string Fingerprint, string Preview);

public sealed class GitRepositoryService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private const int OutputLimit = 8 * 1024 * 1024;

    public async Task<string> RunAsync(string directory, CancellationToken ct, params string[] args)
    {
        var result = await RunCoreAsync(directory, ct, args);
        if (result.Code != 0) throw new InvalidOperationException(Sanitize(result.Error.Length > 0 ? result.Error : result.Output));
        if (result.Truncated) throw new InvalidOperationException("Git 输出超过安全上限，请使用外部 Git 工具查看此仓库。");
        return result.Output;
    }

    private static async Task<(int Code, string Output, string Error, bool Truncated)> RunCoreAsync(string directory, CancellationToken ct, string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["GIT_ALLOW_PROTOCOL"] = "https:ssh:file";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        start.Environment["GIT_EDITOR"] = "false";
        start.Environment["GIT_MERGE_AUTOEDIT"] = "no";
        start.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        foreach (var arg in new[] { "--no-pager", "-c", "color.ui=false", "-c", "core.quotepath=false", "-c", "core.fsmonitor=false" }.Concat(args)) start.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        ct.ThrowIfCancellationRequested();
        using var job = new GitProcessJob();
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("未找到可用的 Git。请安装 Git for Windows 后重新打开 X-Tool。"); }
        job.Attach(process);
        process.StandardInput.Close();
        // 持续排空两个输出流，限量保留，避免缓冲区死锁和大仓库耗尽内存。
        var output = ReadLimitedAsync(process.StandardOutput);
        var error = ReadLimitedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            job.Terminate();
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            if (!ct.IsCancellationRequested) throw new TimeoutException("Git 操作超时；请刷新检查实际状态。私有仓库需先配置系统 Git 凭据。");
            throw;
        }
        var o = await output;
        var e = await error;
        return (process.ExitCode, o.Text, e.Text, o.Truncated || e.Truncated);
    }

    private static async Task<(string Text, bool Truncated)> ReadLimitedAsync(StreamReader reader)
    {
        var value = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            int take = Math.Min(count, OutputLimit - value.Length);
            value.Append(buffer, 0, take);
            truncated |= take != count;
        }
        return (value.ToString(), truncated);
    }

    public static string Sanitize(string value)
    {
        value = Regex.Replace(value, @"https?://[^\s/@]+@", "https://[凭据已隐藏]@", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\b(?:github_pat_|gh[pousr]_)[A-Za-z0-9_]+", "[令牌已隐藏]");
        return value.Length > 4000 ? value[..4000] + "\n（输出已截断）" : value;
    }

    public async Task<GitState> StatusAsync(string directory, CancellationToken ct)
    {
        string root = (await RunAsync(directory, ct, "rev-parse", "--show-toplevel")).TrimEnd('\r', '\n');
        string raw = await RunAsync(root, ct, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all");
        return await Task.Run(() => ParseStatus(root, raw), ct);
    }

    public static GitState ParseStatus(string root, string raw)
    {
        string branch = "", head = "", upstream = "", divergence = "";
        var changes = new List<GitChange>();
        var records = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < records.Length; i++)
        {
            string line = records[i];
            if (line.StartsWith("# branch.head ")) branch = line[14..];
            else if (line.StartsWith("# branch.oid ")) head = line[13..];
            else if (line.StartsWith("# branch.upstream ")) upstream = line[18..];
            else if (line.StartsWith("# branch.ab ")) divergence = line[12..].Replace("+", "领先 ").Replace("-", "落后 ");
            else if (line.StartsWith("? ")) changes.Add(new(line[2..], "", "?", false, false));
            else if (line.Length > 1 && "12u".Contains(line[0]))
            {
                int fields = line[0] == '1' ? 9 : line[0] == '2' ? 10 : 11;
                var parts = line.Split(' ', fields);
                if (parts.Length != fields) throw new InvalidDataException("Git 状态格式无效。");
                string original = line[0] == '2' ? records[++i] : "";
                string path = parts[^1], xy = parts[1];
                if (line[0] == 'u') changes.Add(new(path, original, xy, false, true));
                else
                {
                    if (xy[0] != '.') changes.Add(new(path, original, xy[0].ToString(), true, false));
                    if (xy[1] != '.') changes.Add(new(path, original, xy[1].ToString(), false, false));
                }
            }
        }
        return new(root, branch, head, upstream, divergence, changes, raw);
    }

    public async Task<string> DiffAsync(GitState state, GitChange change, CancellationToken ct)
    {
        if (change.Code == "?")
        {
            string path = Path.GetFullPath(Path.Combine(state.Root, change.Path));
            if (!path.StartsWith(Path.GetFullPath(state.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件不在仓库内。");
            for (string? item = path; item is not null && !string.Equals(item, state.Root, StringComparison.OrdinalIgnoreCase); item = Path.GetDirectoryName(item))
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) return "链接文件，请在外部工具中查看。";
            if (new FileInfo(path).Length > 512 * 1024) return "新文件超过 512 KiB，未加载预览。";
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.Contains((byte)0)) return "二进制文件，不显示文本预览。";
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return "非 UTF-8 文件，请在编辑器中查看。"; }
        }
        var args = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--no-color" };
        if (change.Staged) args.Add("--cached");
        args.Add("--"); args.Add(change.Path);
        if (change.OriginalPath.Length > 0) args.Add(change.OriginalPath);
        string diff = await RunAsync(state.Root, ct, args.ToArray());
        return diff.Length > 512 * 1024 ? diff[..(512 * 1024)] + "\n（预览已截断）" : diff.Length == 0 ? "没有文本差异（可能是子模块、文件模式或仅状态变化）。" : diff;
    }

    private async Task MutateAsync(string root, CancellationToken ct, Func<Task> action)
    {
        string common = (await RunAsync(root, ct, "rev-parse", "--path-format=absolute", "--git-common-dir")).Trim();
        var gate = Gates.GetOrAdd(common, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { await action(); } finally { gate.Release(); }
    }

    public Task StageAsync(GitState expected, GitChange change, bool stage, CancellationToken ct) => MutateAsync(expected.Root, ct, async () =>
    {
        await EnsureStateAsync(expected, ct);
        if (change.Conflict) throw new InvalidOperationException("请先在编辑器中解决冲突，再刷新仓库。");
        var paths = new List<string> { change.Path };
        if (change.OriginalPath.Length > 0) paths.Add(change.OriginalPath);
        string[] prefix = stage ? ["add", "--"] : expected.Head == "(initial)" ? ["rm", "--cached", "--"] : ["restore", "--staged", "--"];
        await RunAsync(expected.Root, ct, prefix.Concat(paths).ToArray());
    });

    private async Task EnsureStateAsync(GitState expected, CancellationToken ct)
    {
        var current = await StatusAsync(expected.Root, ct);
        if (current.Raw != expected.Raw) throw new InvalidOperationException("仓库状态已被其他操作改变，请刷新后重试。");
    }

    public async Task<GitCommitPlan> PrepareCommitAsync(GitState state, CancellationToken ct)
    {
        await EnsureStateAsync(state, ct);
        if (state.Changes.Any(c => c.Conflict)) throw new InvalidOperationException("存在未解决冲突，不能提交。");
        if (!state.Changes.Any(c => c.Staged)) throw new InvalidOperationException("没有已暂存文件。");
        string diff = await RunAsync(state.Root, ct, "diff", "--cached", "--binary", "--no-ext-diff", "--no-textconv");
        string identity = await RunAsync(state.Root, ct, "var", "GIT_AUTHOR_IDENT");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.Head + "\0" + diff)));
        return new(hash, $"分支：{state.Branch}\n身份：{identity.Trim()}\n\n全部暂存文件（含外部工具已暂存内容）：\n" + string.Join("\n", state.Changes.Where(c => c.Staged).Select(c => c.Path)));
    }

    public Task CommitAsync(GitState state, GitCommitPlan plan, string message, CancellationToken ct) => MutateAsync(state.Root, ct, async () =>
    {
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("请填写提交说明。");
        if ((await PrepareCommitAsync(state, ct)).Fingerprint != plan.Fingerprint) throw new InvalidOperationException("暂存内容已变化，请重新检查差异并确认。");
        await RunAsync(state.Root, ct, "commit", "-m", message);
    });

    public async Task<string[]> BranchesAsync(string root, CancellationToken ct) => (await RunAsync(root, ct, "for-each-ref", "--format=%(refname:short)", "refs/heads/")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r')).ToArray();
    public async Task<string[]> RemotesAsync(string root, CancellationToken ct) => (await RunAsync(root, ct, "remote")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r')).ToArray();
    public Task SwitchAsync(GitState state, string branch, bool create, CancellationToken ct) => MutateAsync(state.Root, ct, async () =>
    {
        await EnsureStateAsync(state, ct);
        if (state.Changes.Count > 0) throw new InvalidOperationException("切换分支前请先处理所有本地改动。不会自动暂存、隐藏或丢弃。");
        if (branch.StartsWith('-') || string.IsNullOrWhiteSpace(branch)) throw new InvalidOperationException("分支名称无效。");
        await RunAsync(state.Root, ct, "check-ref-format", "--branch", branch);
        await RunAsync(state.Root, ct, create ? ["switch", "-c", branch] : ["switch", "--", branch]);
    });

    public async Task<IReadOnlyList<GitCommit>> HistoryAsync(GitState state, CancellationToken ct)
    {
        if (state.Head == "(initial)") return [];
        string raw = await RunAsync(state.Root, ct, "log", "-n", "100", "--format=%H%x00%an%x00%aI%x00%s%x00");
        var fields = raw.Split('\0');
        var result = new List<GitCommit>();
        for (int i = 0; i + 3 < fields.Length; i += 4) result.Add(new(fields[i].Trim(), fields[i + 1], fields[i + 2], fields[i + 3]));
        return result;
    }
    public Task<string> ShowCommitAsync(string root, GitCommit commit, CancellationToken ct) => RunAsync(root, ct, "show", "--no-ext-diff", "--no-textconv", "--format=fuller", "--stat", "--patch", commit.Id, "--");

    public async Task<string> RemoteUrlAsync(string root, string remote, bool push, CancellationToken ct)
    {
        if (!(await RemotesAsync(root, ct)).Contains(remote)) throw new InvalidOperationException("请选择有效的远程仓库。");
        string urls = await RunAsync(root, ct, push ? ["remote", "get-url", "--push", "--all", remote] : ["remote", "get-url", remote]);
        foreach (string url in urls.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (url.Contains("::") || url.TrimStart().StartsWith('-') || Regex.IsMatch(url, @"https?://[^/]*@")) throw new InvalidOperationException("不支持此远程地址，或地址内含凭据。请在外部 Git 工具中改为安全地址。");
        return urls.Trim();
    }

    public Task FetchAsync(string root, string remote, CancellationToken ct) => MutateAsync(root, ct, async () =>
    {
        await RemoteUrlAsync(root, remote, false, ct);
        await RunAsync(root, ct, "fetch", "--no-recurse-submodules", "--", remote);
    });

    public Task PullAsync(GitState state, string remote, CancellationToken ct) => MutateAsync(state.Root, ct, async () =>
    {
        await EnsureStateAsync(state, ct);
        if (state.Changes.Count > 0 || state.Upstream.Length == 0 || state.Branch == "(detached)") throw new InvalidOperationException("仅支持工作区干净且已设置上游分支的快进拉取。");
        string configured = (await RunAsync(state.Root, ct, "config", "--get", $"branch.{state.Branch}.remote")).Trim();
        if (configured != remote) throw new InvalidOperationException($"当前上游属于 {configured}，请先选择该远程。");
        await RemoteUrlAsync(state.Root, remote, false, ct);
        await RunAsync(state.Root, ct, "fetch", "--no-recurse-submodules", "--", remote);
        var after = await StatusAsync(state.Root, ct);
        if (after.Head != state.Head || after.Branch != state.Branch || after.Upstream != state.Upstream || after.Changes.Count > 0) throw new InvalidOperationException("获取期间工作区已变化，已停止拉取。");
        await RunAsync(state.Root, ct, "merge", "--ff-only", "--no-autostash", "--no-edit", "@{upstream}");
    });

    public async Task<GitPushPlan> PreparePushAsync(GitState state, string remote, CancellationToken ct)
    {
        await EnsureStateAsync(state, ct);
        if (state.Head == "(initial)" || state.Branch == "(detached)") throw new InvalidOperationException("请先在本地分支创建提交。");
        string urls = await RemoteUrlAsync(state.Root, remote, true, ct);
        string target = "refs/heads/" + state.Branch;
        if (state.Upstream.Length > 0)
        {
            string configured = (await RunAsync(state.Root, ct, "config", "--get", $"branch.{state.Branch}.remote")).Trim();
            if (configured != remote) throw new InvalidOperationException($"当前分支上游属于 {configured}，请选择该远程；更改上游请使用外部 Git 工具。");
            target = (await RunAsync(state.Root, ct, "config", "--get", $"branch.{state.Branch}.merge")).Trim();
        }
        if (!target.StartsWith("refs/heads/")) throw new InvalidOperationException("目标必须是远程分支。");
        await RunAsync(state.Root, ct, "check-ref-format", target);
        string commits = state.Upstream.Length > 0 ? await RunAsync(state.Root, ct, "log", "--oneline", "-n", "30", "@{upstream}..HEAD", "--") : "新上游：将推送当前分支可达的提交历史。";
        return new(remote, target, urls, state.Head, $"远程：{remote}\n{Sanitize(urls)}\n\n{state.Branch} → {target}\nHEAD：{state.Head}\n\n待推送提交（本地跟踪信息，最多 30 条）：\n{commits}\n不会推送未提交文件，不使用强制推送。");
    }
    public Task PushAsync(GitState state, GitPushPlan plan, CancellationToken ct) => MutateAsync(state.Root, ct, async () =>
    {
        var current = await PreparePushAsync(state, plan.Remote, ct);
        if (current.Urls != plan.Urls || current.Target != plan.Target || current.Head != plan.Head) throw new InvalidOperationException("推送目标或提交已变化，请重新确认。");
        await RunAsync(state.Root, ct, "push", "--porcelain", "--no-follow-tags", "--set-upstream", "--", plan.Remote, $"HEAD:{plan.Target}");
    });

    public async Task<string> CloneAsync(string url, string destination, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath.Trim('/').Split('/').Length != 2)
            throw new InvalidOperationException("请输入不含令牌的 GitHub HTTPS 仓库地址。");
        string path = Path.GetFullPath(destination);
        if (Directory.Exists(path) || File.Exists(path)) throw new InvalidOperationException("目标目录已存在，请选择新的子目录。不会覆盖现有文件。");
        string parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("目标路径无效。");
        if (!Directory.Exists(parent)) throw new InvalidOperationException("父目录不存在。");
        await RunAsync(parent, ct, "-c", "core.hooksPath=NUL", "clone", "--", uri.AbsoluteUri, path);
        return path;
    }

    public static string? GitHubSlug(string url)
    {
        var match = Regex.Match(url.Trim(), @"^(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
