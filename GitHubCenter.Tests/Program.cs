using System.Net;
using System.Text;
using ScreenshotApp.GitHubCenter;

var root = Path.Combine(Path.GetTempPath(), "XTool-GitHub-Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var git = new GitRepositoryService();
var ct = CancellationToken.None;
int count = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    Console.WriteLine("PASS: " + label); count++;
}
async Task Reject(Func<Task> operation, string label)
{
    try { await operation(); } catch (InvalidOperationException) { Check(true, label); return; }
    throw new Exception("FAIL: " + label);
}
async Task<string> Init(string name, bool bare = false)
{
    string path = Path.Combine(root, name); Directory.CreateDirectory(path);
    await git.RunAsync(path, ct, bare ? ["init", "--bare", "--initial-branch=main"] : ["init", "--initial-branch=main"]);
    if (!bare)
    {
        await git.RunAsync(path, ct, "config", "user.name", "XTool Test");
        await git.RunAsync(path, ct, "config", "user.email", "test@example.invalid");
        await git.RunAsync(path, ct, "config", "commit.gpgsign", "false");
        await git.RunAsync(path, ct, "config", "core.hooksPath", Path.Combine(root, "empty-hooks"));
    }
    return path;
}
async Task<GitState> Commit(string path, string text)
{
    var state = await git.StatusAsync(path, ct);
    var plan = await git.PrepareCommitAsync(state, ct);
    await git.CommitAsync(state, plan, text, ct);
    return await git.StatusAsync(path, ct);
}
Console.WriteLine("临时测试目录：" + root);
var repo = await Init("中文 仓库");
var state = await git.StatusAsync(repo, ct);
Check(state.Head == "(initial)" && state.Branch == "main", "空仓库与初始分支");
await File.WriteAllTextAsync(Path.Combine(repo, "中文 文档.txt"), "第一行\n第二行\n", Encoding.UTF8);
await File.WriteAllTextAsync(Path.Combine(repo, "其他任务.txt"), "不得暂存", Encoding.UTF8);
state = await git.StatusAsync(repo, ct);
Check(state.Changes.Count == 2, "中文及空格路径解析");
var first = state.Changes.Single(c => c.Path == "中文 文档.txt");
Check((await git.DiffAsync(state, first, ct)).Contains("第一行"), "新文件预览");
await git.StageAsync(state, first, true, ct);
state = await git.StatusAsync(repo, ct);
Check(state.Changes.Count(c => c.Staged) == 1 && state.Changes.Any(c => c.Path == "其他任务.txt" && !c.Staged), "只暂存选中文件，保留其他任务");
await git.StageAsync(state, state.Changes.Single(c => c.Staged), false, ct);
state = await git.StatusAsync(repo, ct);
Check(state.Changes.All(c => !c.Staged) && File.Exists(Path.Combine(repo, "中文 文档.txt")), "初始提交前取消暂存保留文件");
await git.StageAsync(state, state.Changes.Single(c => c.Path == "中文 文档.txt"), true, ct);
state = await Commit(repo, "初始提交");
Check(state.Head.Length == 40 && state.Changes.Count == 1, "初始提交仅包含暂存文件");
Check((await git.HistoryAsync(state, ct)).Single().Subject == "初始提交", "UTF-8 提交历史");
await Reject(() => git.SwitchAsync(state, "topic", true, ct), "脏工作区拒绝切换");
await File.WriteAllTextAsync(Path.Combine(repo, "中文 文档.txt"), "新的第一行\n第二行\n", Encoding.UTF8);
state = await git.StatusAsync(repo, ct);
await git.StageAsync(state, state.Changes.Single(c => c.Path == "中文 文档.txt"), true, ct);
state = await git.StatusAsync(repo, ct);
var preview = await git.PrepareCommitAsync(state, ct);
Check(preview.Preview.Contains("XTool Test") && preview.Preview.Contains("中文 文档.txt"), "提交确认含身份与暂存文件");
await File.AppendAllTextAsync(Path.Combine(repo, "中文 文档.txt"), "外部变更\n", Encoding.UTF8);
await git.RunAsync(repo, ct, "add", "--", "中文 文档.txt");
await Reject(() => git.CommitAsync(state, preview, "不应提交", ct), "相同状态但暂存内容变化，拒绝旧确认");
state = await git.StatusAsync(repo, ct);
await git.StageAsync(state, state.Changes.Single(c => c.Staged), false, ct);
state = await git.StatusAsync(repo, ct);
Check(state.Changes.All(c => !c.Staged), "有 HEAD 时取消暂存");
var stale = state;
await File.WriteAllTextAsync(Path.Combine(repo, ":(glob)危险.txt".Replace(':', '_')), "pathspec");
await Reject(() => git.StageAsync(stale, stale.Changes.First(), true, ct), "状态变化拒绝旧操作");
state = await git.StatusAsync(repo, ct);
foreach (var change in state.Changes)
{
    var current = await git.StatusAsync(repo, ct);
    await git.StageAsync(current, current.Changes.Single(c => c.Path == change.Path && !c.Staged), true, ct);
}
state = await Commit(repo, "第二次提交");
await git.SwitchAsync(state, "codex/test", true, ct);
state = await git.StatusAsync(repo, ct);
Check(state.Branch == "codex/test", "创建分支");
await git.SwitchAsync(state, "main", false, ct);
state = await git.StatusAsync(repo, ct);
Check(state.Branch == "main", "切换已有分支");
await git.RunAsync(repo, ct, "mv", "--", "中文 文档.txt", "重命名 文档.txt");
state = await git.StatusAsync(repo, ct);
var renamed = state.Changes.Single();
Check(renamed.OriginalPath == "中文 文档.txt" && renamed.Path == "重命名 文档.txt" && renamed.Staged, "重命名源目标正确解析");
Check((await git.DiffAsync(state, renamed, ct)).Contains("rename"), "重命名差异");
await git.StageAsync(state, renamed, false, ct);
state = await git.StatusAsync(repo, ct);
Check(state.Changes.All(c => !c.Staged), "取消暂存重命名的两端");
await git.RunAsync(repo, ct, "add", "--all");
state = await Commit(repo, "重命名提交");
var bare = await Init("remote.git", true);
await git.RunAsync(repo, ct, "remote", "add", "origin", bare);
var push = await git.PreparePushAsync(state, "origin", ct);
Check(push.Target == "refs/heads/main" && push.Preview.Contains("新上游"), "无上游推送计划");
await git.PushAsync(state, push, ct);
state = await git.StatusAsync(repo, ct);
Check(state.Upstream == "origin/main" && state.Divergence.Contains("领先 0"), "仅向本地裸仓库推送并建立上游");
await git.FetchAsync(repo, "origin", ct);
await git.PullAsync(await git.StatusAsync(repo, ct), "origin", ct);
Check(true, "获取与无变化快进拉取");
string clone = Path.Combine(root, "其他开发者");
await git.RunAsync(root, ct, "clone", "--", bare, clone);
await git.RunAsync(clone, ct, "config", "user.name", "Other");
await git.RunAsync(clone, ct, "config", "user.email", "other@example.invalid");
await git.RunAsync(clone, ct, "config", "commit.gpgsign", "false");
await git.RunAsync(clone, ct, "config", "core.hooksPath", Path.Combine(root, "empty-hooks"));
await File.WriteAllTextAsync(Path.Combine(clone, "remote.txt"), "remote");
await git.RunAsync(clone, ct, "add", "--", "remote.txt");
var other = await Commit(clone, "远程新提交");
await git.PushAsync(other, await git.PreparePushAsync(other, "origin", ct), ct);
await git.PullAsync(await git.StatusAsync(repo, ct), "origin", ct);
Check(File.Exists(Path.Combine(repo, "remote.txt")), "快进拉取更新文件");
await File.WriteAllTextAsync(Path.Combine(repo, "local.txt"), "local");
await git.RunAsync(repo, ct, "add", "--", "local.txt");
state = await Commit(repo, "本地分叉");
await File.AppendAllTextAsync(Path.Combine(clone, "remote.txt"), "remote 2");
await git.RunAsync(clone, ct, "add", "--", "remote.txt");
other = await Commit(clone, "远程分叉");
await git.PushAsync(other, await git.PreparePushAsync(other, "origin", ct), ct);
await Reject(() => git.PullAsync(state, "origin", ct), "分叉拉取拒绝自动合并");
Check((await git.StatusAsync(repo, ct)).Head == state.Head, "拉取失败不改变本地 HEAD");
await git.RunAsync(repo, ct, "remote", "set-url", "--push", "origin", "https://user:secret@github.com/test/repo.git");
// 上一步获取会更新计数，重新读取后测试远程凭据校验。
state = await git.StatusAsync(repo, ct);
await Reject(() => git.PreparePushAsync(state, "origin", ct), "已刷新状态仍拒绝 URL 凭据");
await Reject(() => git.CloneAsync("https://evil.invalid/repo.git", Path.Combine(root, "invalid"), ct), "克隆域名白名单");
await Reject(() => git.CloneAsync("https://github.com/test/repo.git", repo, ct), "克隆不覆盖现有目录");
Check(GitRepositoryService.GitHubSlug("git@github.com:openai/example.git") == "openai/example", "SSH 地址解析");
Check(GitRepositoryService.GitHubSlug("https://github.com/openai/example.git") == "openai/example", "HTTPS 地址解析");
Check(GitRepositoryService.GitHubSlug("https://github.com.evil.invalid/openai/example") is null, "拒绝伪造 GitHub 域名");
Check(!GitRepositoryService.Sanitize("https://u:password@github.com/a/b ghp_secretABC").Contains("password"), "错误信息凭据脱敏");
var canceled = new CancellationTokenSource(); canceled.Cancel();
try { await git.StatusAsync(repo, canceled.Token); throw new Exception("取消未生效"); } catch (OperationCanceledException) { Check(true, "Git 取消生效"); }
var conflict = GitRepositoryService.ParseStatus(root, "# branch.oid abc\0# branch.head main\0u UU N... 100644 100644 100644 100644 a b c 冲突 文件.txt\0");
Check(conflict.Changes.Single().Conflict && conflict.Changes.Single().Path == "冲突 文件.txt", "冲突格式解析");
var store = new GitHubStore(Path.Combine(root, "store"));
store.SaveCredential(new("test", "ghp_test_only"));
Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(store.Root, "account.dat"))).Contains("ghp_test_only"), "令牌 DPAPI 加密落盘");
Check(store.LoadCredential()?.Token == "ghp_test_only", "当前用户解密令牌");
store.SavePreferences(new() { Repositories = [new() { Path = repo, Summary = "中文状态" }] });
Check(store.LoadPreferences().Repositories.Single().Path == repo, "仓库持久化与 UTF-8");
store.SaveCache("test:repos", new[] { "private" });
Check(store.LoadCache<string[]>("test:repos")?.Value.Single() == "private" && store.CacheSize() > 0, "加密缓存与占用统计");
store.ClearCache();
Check(store.CacheSize() == 0 && File.Exists(Path.Combine(repo, "local.txt")) && store.LoadCredential() is not null, "清缓存保留代码与凭据");
store.Disconnect(); Check(store.LoadCredential() is null, "断开账户删除本模块凭据");
var handler = new FakeHandler();
using var api = new GitHubApi(handler);
var commits = await api.CommitsAsync("tester/repo", "test-token", 2, ct);
Check(commits.Single().Message == "测试提交\n正文" && handler.LastUrl!.Contains("page=2"), "远程提交正文与分页解析");
await Reject(() => api.CommitsAsync("../bad", "test-token", 1, ct), "拒绝无效提交仓库地址");
handler.Reset();
await Reject(() => api.LoginAsync("ghs_installation_example", ct), "安装令牌在联网前明确拒绝");
await Reject(() => api.LoginAsync("-----BEGIN PRIVATE KEY-----", ct), "私钥不发送给 API");
Check(handler.LastUrl is null, "错误凭据类型不触发 HTTP 请求");
Check(await api.LoginAsync("test-token", ct) == "tester", "API 账户读取");
Check(handler.LastAuth == "Bearer test-token", "令牌仅放入授权头");
Check((await api.RepositoriesAsync("test-token", 2, ct)).Single().Name == "tester/repo" && handler.LastUrl!.Contains("page=2"), "仓库分页解析");
var overview = await api.OverviewAsync("tester/repo", "test-token", ct);
Check(overview.PullRequests.Single().Title.Contains("测试 PR") && overview.Runs.Single().Status.Contains("success"), "PR 与 Actions 摘要解析");
handler.Status = HttpStatusCode.Forbidden;
overview = await api.OverviewAsync("tester/repo", "test-token", ct);
Check(overview.PullStatus.Contains("拒绝") && overview.RunStatus.Contains("拒绝"), "权限失败不显示伪造空列表成功");
handler.Status = HttpStatusCode.Unauthorized;
try { await api.LoginAsync("test", ct); throw new Exception("过期令牌被接受"); } catch (HttpRequestException ex) { Check(ex.Message.Contains("过期"), "401 明确显示授权过期"); }
handler.Status = HttpStatusCode.TooManyRequests;
try { await api.RepositoriesAsync("test", 1, ct); throw new Exception("限流未处理"); } catch (HttpRequestException ex) { Check(ex.Message.Contains("受限"), "429 限流提示"); }
handler.Status = HttpStatusCode.OK;
using (var token = new CancellationTokenSource())
{
    token.Cancel();
    try { await api.LoginAsync("test", token.Token); throw new Exception("API 取消未生效"); } catch (OperationCanceledException) { Check(true, "API 取消请求"); }
}
var cancellationRepo = await Init("取消测试");
string hooks = Path.Combine(root, "cancel-hooks"); Directory.CreateDirectory(hooks);
await File.WriteAllTextAsync(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\nsleep 20\n", new UTF8Encoding(false));
await git.RunAsync(cancellationRepo, ct, "config", "core.hooksPath", hooks);
await File.WriteAllTextAsync(Path.Combine(cancellationRepo, "sample.txt"), "保留暂存");
await git.RunAsync(cancellationRepo, ct, "add", "--", "sample.txt");
var beforeCancel = await git.StatusAsync(cancellationRepo, ct);
var cancelPlan = await git.PrepareCommitAsync(beforeCancel, ct);
using (var token = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    try { await git.CommitAsync(beforeCancel, cancelPlan, "取消操作", token.Token); throw new Exception("运行中的 Git 未取消"); }
    catch (OperationCanceledException) { Check(true, "取消运行中的 Git 及 hook 子进程"); }
    Check(watch.Elapsed < TimeSpan.FromSeconds(8), "取消无需等待 20 秒 hook 自然结束");
}
Check((await git.StatusAsync(cancellationRepo, ct)).Changes.Any(c => c.Staged), "取消提交保留暂存内容");
string worktree = Path.Combine(root, "worktree");
await git.RunAsync(repo, ct, "worktree", "add", "-b", "codex/worktree", "--", worktree);
Check((await git.StatusAsync(worktree, ct)).Branch == "codex/worktree", "支持 worktree 仓库读取");
await git.RunAsync(repo, ct, "switch", "--detach");
var detached = await git.StatusAsync(repo, ct);
await Reject(() => git.PreparePushAsync(detached, "origin", ct), "分离 HEAD 禁止模糊推送");
var binary = Path.Combine(cancellationRepo, "binary.bin"); await File.WriteAllBytesAsync(binary, [0, 1, 2, 3]);
var withBinary = await git.StatusAsync(cancellationRepo, ct);
Check((await git.DiffAsync(withBinary, withBinary.Changes.Single(c => c.Path == "binary.bin"), ct)).Contains("二进制"), "二进制预览保护");
var scan = RepositoryDiscovery.Scan([root, root], ct);
Check(scan.Paths.Contains(repo) && scan.Paths.Contains(worktree), "扫描识别普通仓库与 worktree");
Check(scan.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == scan.Paths.Count, "扫描重叠路径去重");
Check(RepositoryDiscovery.Scan([root], ct, 1).Limited, "扫描目录数量限制");
using (var cancelledScan = new CancellationTokenSource())
{
    cancelledScan.Cancel();
    try { RepositoryDiscovery.Scan([root], cancelledScan.Token); throw new Exception("扫描未取消"); }
    catch (OperationCanceledException) { Check(true, "扫描可取消"); }
}
Console.WriteLine($"\n全部 {count} 项通过。未使用真实账户，未向网络推送。测试文件保留于：{root}");

sealed class FakeHandler : HttpMessageHandler
{
    public string? LastUrl { get; private set; }
    public string? LastAuth { get; private set; }
    public void Reset() { LastUrl = null; LastAuth = null; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastUrl = request.RequestUri!.AbsoluteUri; LastAuth = request.Headers.Authorization?.ToString();
        string json = request.RequestUri.AbsolutePath switch
        {
            "/user" => "{\"login\":\"tester\"}",
            "/repos/tester/repo/commits" => "[{\"sha\":\"1234567890\",\"html_url\":\"https://github.com/tester/repo/commit/1234567890\",\"commit\":{\"message\":\"测试提交\\n正文\",\"author\":{\"name\":\"Tester\",\"date\":\"2026-09-29T00:00:00Z\"}}}]",
            "/user/repos" => "[{\"full_name\":\"tester/repo\",\"html_url\":\"https://github.com/tester/repo\",\"clone_url\":\"https://github.com/tester/repo.git\",\"private\":true}]",
            "/repos/tester/repo/pulls" => "[{\"number\":1,\"title\":\"测试 PR\",\"head\":{\"ref\":\"topic\"},\"base\":{\"ref\":\"main\"},\"draft\":false,\"html_url\":\"https://github.com/tester/repo/pull/1\"}]",
            _ => "{\"workflow_runs\":[{\"display_title\":\"CI\",\"name\":\"Build\",\"head_branch\":\"main\",\"status\":\"completed\",\"conclusion\":\"success\",\"html_url\":\"https://github.com/tester/repo/actions/runs/1\"}]}"
        };
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
