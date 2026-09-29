using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace ScreenshotApp.Mail;

/// <summary>邮箱连接独立于手机通道；凭据、摘要及去重进度仅在当前 Windows 用户下加密保存。</summary>
public sealed class MailService : IDisposable
{
    public static MailService Instance { get; } = new();
    private readonly object _gate = new();
    private readonly string _path;
    private readonly MailDiskCache _diskCache;
    private readonly SemaphoreSlim _prefetchDownloads = new(1);
    private readonly Dictionary<string, string> _cacheErrors = new();
    private MailDatabase _db = new([], [], new());
    private readonly Dictionary<string, Worker> _workers = new();
    private readonly Dictionary<string, string> _statuses = new();
    private readonly Dictionary<string, int> _inboxCounts = new();
    private readonly SemaphoreSlim _downloads = new(1);
    private ImapClient? _readerClient;
    private string? _readerAccount;
    private bool _readerProxy;
    private readonly Dictionary<string, (MimeMessage Message, DateTime Used, uint Size)> _bodyCache = new();
    private long _revision;
    private bool _started, _disposed;
    private string _error = "";
    public event Action<MailArrival>? Arrived;
    private sealed class Worker
    {
        public readonly CancellationTokenSource Stop = new();
        public CancellationTokenSource? Wake;
        public Task? Task;
        public Task? CacheTask;
        public readonly object Gate = new();
        public void Signal() { lock (Gate) { Wake?.Cancel(); } }
    }
    public MailService(string? storage = null)
    {
        // 必须在 MailKit/MimeKit 首次解析字符集前注册，不能依赖其他工具的初始化顺序。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _path = Path.Combine(storage ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "Mail"), "mail.bin");
        _diskCache = new MailDiskCache(Path.GetDirectoryName(_path)!);
    }
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            try
            {
                if (File.Exists(_path))
                {
                    if (new FileInfo(_path).Length > 16 * 1024 * 1024) throw new InvalidDataException();
                    var value = JsonSerializer.Deserialize<MailDatabase>(ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser));
                    if (value is null || value.Accounts.Length > 10 || value.Rows.Length > 2000 ||
                        value.Accounts.Select(a => a.Id).Distinct().Count() != value.Accounts.Length) throw new InvalidDataException();
                    foreach (var account in value.Accounts) Validate(account);
                    _db = value;
                }
            }
            catch { _error = "邮箱数据无法解密或读取。原文件已保留，请先恢复原 Windows 用户环境。"; }
            if (_error.Length == 0) foreach (var account in _db.Accounts) Launch(account);
            _revision++;
        }
    }
    private static void Validate(MailAccount a)
    {
        if (a.Provider is not ("QQ" or "Gmail" or "TencentExmail") || !MailboxAddress.TryParse(a.Address, out var mailbox) ||
            !string.Equals(mailbox.Address, a.Address, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(a.Secret) || a.Secret.Length > 2048 || a.Address.Length > 254 ||
            (a.Remark?.Length ?? 0) > 40 || a.QuietStart is < 0 or > 23 || a.QuietEnd is < 0 or > 23) throw new ArgumentException("请填写有效邮箱地址和授权码／应用专用密码。");
    }
    public MailSnapshot Read()
    {
        lock (_gate) return new(_revision, _db.Accounts.Select(a => new MailAccountView(a.Id, a.Provider, a.Address,
            _statuses.GetValueOrDefault(a.Id, "等待连接"), a.Alerts, a.Preview, a.Quiet, a.QuietStart, a.QuietEnd, a.UseSystemProxy, a.Remark)
            { InboxTotal = _inboxCounts.TryGetValue(a.Id, out var count) ? count : null }).ToArray(),
            _db.Rows.OrderByDescending(r => r.Date).ToArray(), _error);
    }
    public async Task AddAsync(string provider, string address, string secret, CancellationToken token, bool useSystemProxy = false, string remark = "")
    {
        var a = new MailAccount(Guid.NewGuid().ToString("N"), provider, address.Trim(), NormalizeSecret(provider, secret)) { UseSystemProxy = useSystemProxy, Remark = NormalizeRemark(remark) };
        Validate(a);
        lock (_gate)
        {
            if (_error.Length > 0) throw new InvalidOperationException(_error);
            if (_db.Accounts.Length >= 10) throw new InvalidOperationException("最多添加 10 个账户。");
            if (_db.Accounts.Any(x => x.Address.Equals(a.Address, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("这个邮箱已添加。");
        }
        using (var client = await Connect(a, token)) { await client.Inbox.OpenAsync(FolderAccess.ReadOnly, token); }
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed) throw new OperationCanceledException();
            if (_db.Accounts.Length >= 10 || _db.Accounts.Any(x => x.Address.Equals(a.Address, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("账户已存在或数量已达上限。");
            Commit(_db with { Accounts = [.. _db.Accounts, a] });
            Launch(a);
        }
    }
    public void Remove(string id)
    {
        lock (_gate)
        {
            var checkpoints = new Dictionary<string, MailCheckpoint>(_db.Checkpoints); checkpoints.Remove(id);
            Commit(new(_db.Accounts.Where(a => a.Id != id).ToArray(), _db.Rows.Where(r => r.AccountId != id).ToArray(), checkpoints));
            if (_workers.Remove(id, out var worker)) { worker.Stop.Cancel(); worker.Signal(); _ = Task.WhenAll(worker.Task!, worker.CacheTask ?? Task.CompletedTask).ContinueWith(_ => worker.Stop.Dispose()); }
            _statuses.Remove(id);
            _inboxCounts.Remove(id);
            _cacheErrors.Remove(id);
        }
    }
    public void Preferences(string id, bool alerts, bool preview, bool quiet, int start, int end, bool? useSystemProxy = null, string? remark = null)
    {
        if (remark is not null) remark = NormalizeRemark(remark);
        if (start is < 0 or > 23 || end is < 0 or > 23) throw new ArgumentException("时间必须为 0–23 点。");
        lock (_gate) Commit(_db with { Accounts = _db.Accounts.Select(a => a.Id == id
            ? a with { Alerts = alerts, Preview = preview, Quiet = quiet, QuietStart = start, QuietEnd = end, UseSystemProxy = useSystemProxy ?? a.UseSystemProxy, Remark = remark ?? a.Remark } : a).ToArray() });
        lock (_gate) { if (_workers.TryGetValue(id, out var worker)) worker.Signal(); }
    }
    private static string NormalizeRemark(string remark)
    {
        var value = remark.Trim();
        if (value.Length > 40 || value.Any(char.IsControl)) throw new ArgumentException("备注请使用 40 字以内的单行文字。");
        return value;
    }
    public void Refresh()
    {
        lock (_gate) foreach (var worker in _workers.Values) worker.Signal();
    }
    public bool AllowNotification(string id, out bool preview)
    {
        lock (_gate)
        {
            var account = _db.Accounts.FirstOrDefault(a => a.Id == id);
            preview = account?.Preview == true;
            return account is not null && account.Alerts && !MailRules.IsQuiet(account, DateTime.Now.Hour);
        }
    }
    private void Launch(MailAccount a)
    {
        var worker = new Worker(); _workers[a.Id] = worker;
        worker.Task = Task.Run(() => Run(a, worker));
    }
    private void Status(string id, string text)
    {
        lock (_gate)
        {
            if (!_db.Accounts.Any(a => a.Id == id)) return;
            if (_statuses.GetValueOrDefault(id) != text) { _statuses[id] = text; _revision++; }
        }
    }
    private static async Task<ImapClient> Connect(MailAccount a, CancellationToken token)
    {
        var client = new ImapClient { Timeout = 25000 };
        try
        {
            if (a.UseSystemProxy) client.ProxyClient = MailNetwork.SystemProxy(a.Host);
            await client.ConnectAsync(a.Host, 993, SecureSocketOptions.SslOnConnect, token);
            // 保持平台默认 TLS 证书校验；应用专用密码模式不尝试普通账户 OAuth。
            client.AuthenticationMechanisms.Remove("XOAUTH2");
            client.AuthenticationMechanisms.Remove("OAUTHBEARER");
            // 腾讯企业邮在认证前接受客户端标识；不携带账号或密码。
            if (a.Provider == "TencentExmail" && client.Capabilities.HasFlag(ImapCapabilities.Id))
                await client.IdentifyAsync(new ImapImplementation { Name = "X-Tool", Version = "1.0" }, token);
            try { await client.AuthenticateAsync(a.Address, a.Secret, token); }
            catch (MailKit.Security.AuthenticationException error)
            {
                error.Data["MailProvider"] = a.Provider;
                throw;
            }
            if (a.Provider == "QQ" && client.Capabilities.HasFlag(ImapCapabilities.Id))
                await client.IdentifyAsync(new ImapImplementation { Name = "X-Tool", Version = "1.0" }, token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }
    private async Task Run(MailAccount a, Worker worker)
    {
        var token = worker.Stop.Token;
        var delay = 3;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    lock (_gate) { a = _db.Accounts.FirstOrDefault(current => current.Id == a.Id) ?? a; }
                    Status(a.Id, a.UseSystemProxy ? "正在连接 · 系统代理…" : "正在连接 · 直连…");
                    using var client = await Connect(a, token);
                    var folder = client.Inbox;
                    await folder.OpenAsync(FolderAccess.ReadOnly, token);
                    await Synchronize(a, folder, token);
                    delay = 3;
                    while (!token.IsCancellationRequested)
                    {
                        var idle = client.Capabilities.HasFlag(ImapCapabilities.Idle);
                        Status(a.Id, idle ? "已连接 · 实时监听" : "已连接 · 每 60 秒检查");
                        using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
                        wake.CancelAfter(TimeSpan.FromSeconds(60));
                        lock (worker.Gate) worker.Wake = wake;
                        EventHandler<EventArgs> onChange = (_, _) => { if (!wake.IsCancellationRequested) wake.Cancel(); };
                        EventHandler<MessageFlagsChangedEventArgs> onFlags = (_, _) => { if (!wake.IsCancellationRequested) wake.Cancel(); };
                        folder.CountChanged += onChange; folder.MessageFlagsChanged += onFlags;
                        try
                        {
                            if (idle) await client.IdleAsync(wake.Token, token);
                            else { try { await Task.Delay(60000, wake.Token); } catch (OperationCanceledException) { } }
                        }
                        finally
                        {
                            folder.CountChanged -= onChange; folder.MessageFlagsChanged -= onFlags;
                            lock (worker.Gate) worker.Wake = null;
                        }
                        token.ThrowIfCancellationRequested();
                        lock (_gate) { if (_db.Accounts.FirstOrDefault(current => current.Id == a.Id)?.UseSystemProxy != a.UseSystemProxy) break; }
                        await client.NoOpAsync(token);
                        await Synchronize(a, folder, token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    Status(a.Id, FriendlyError(error));
                    var seconds = error is MailKit.Security.AuthenticationException ? 300 : delay;
                    delay = Math.Min(delay * 2, 120);
                    using var retry = CancellationTokenSource.CreateLinkedTokenSource(token);
                    lock (worker.Gate) worker.Wake = retry;
                    try { await Task.Delay(TimeSpan.FromSeconds(seconds), retry.Token); } catch (OperationCanceledException) { }
                    finally { lock (worker.Gate) worker.Wake = null; }
                }
            }
        }
        finally { /* 停止令牌在移除工作器后释放。 */ }
    }
    private async Task Synchronize(MailAccount a, IMailFolder folder, CancellationToken token)
    {
        // 按服务器实际 UID 集合读取，避免序号随收信、移走邮件而变化；不完整响应不得覆盖本地摘要。
        var allUids = await folder.SearchAsync(MailKit.Search.SearchQuery.All, token);
        var requested = allUids.OrderBy(u => u.Id).TakeLast(200).ToArray();
        var wanted = requested.Select(u => u.Id).ToHashSet();
        var fetched = requested.Length == 0 ? [] : await folder.FetchAsync(requested,
            MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure, token);
        var summaries = fetched.Where(s => wanted.Contains(s.UniqueId.Id)).GroupBy(s => s.UniqueId.Id).Select(g => g.Last()).ToArray();
        if (summaries.Length != requested.Length || summaries.Any(s => s.Envelope is null))
            throw new InvalidOperationException("服务器返回的邮件摘要不完整，已保留原列表，请刷新重试。");
        var rows = summaries.Select(s => new MailRow(a.Id, folder.UidValidity, s.UniqueId.Id,
            Clip(s.Envelope?.From.ToString(), 200), Clip(s.Envelope?.Subject, 300, "（无主题）"),
            s.Envelope?.Date ?? DateTimeOffset.MinValue, s.Flags?.HasFlag(MessageFlags.Seen) == true, s.Size ?? 0) { HasAttachments = s.Attachments?.Any() == true, Flagged = s.Flags?.HasFlag(MessageFlags.Flagged) == true }).ToArray();
        MailRow[] fresh;
        lock (_gate)
        {
            if (token.IsCancellationRequested || !_db.Accounts.Any(x => x.Id == a.Id)) return;
            _db.Checkpoints.TryGetValue(a.Id, out var previous);
            fresh = rows.Where(r => !r.Seen && MailRules.IsNew(previous, r.Validity, r.Uid)).ToArray();
            var highest = rows.Length == 0 ? 0 : rows.Max(r => r.Uid);
            if (previous?.Validity == folder.UidValidity) highest = Math.Max(highest, previous.HighUid);
            var checkpoints = new Dictionary<string, MailCheckpoint>(_db.Checkpoints)
                { [a.Id] = new(folder.UidValidity, highest, true) };
            var next = _db with { Rows = _db.Rows.Where(r => r.AccountId != a.Id).Concat(rows).ToArray(), Checkpoints = checkpoints };
            // 先持久化进度再提醒，重连/重启不会重放；保存失败不推进内存进度。
            Commit(next);
            _inboxCounts[a.Id] = allUids.Count;
        }
        if (fresh.Length > 0) Arrived?.Invoke(new(a.Id, fresh));
        lock (_gate)
        {
            if (!_disposed && _workers.TryGetValue(a.Id, out var worker) && worker.CacheTask is not { IsCompleted: false })
                worker.CacheTask = Task.Run(() => CacheReceived(a.Id, worker.Stop.Token));
        }
    }
    // 预取使用独立连接且全局串行，不占用交互阅读连接，也不阻塞 IDLE 收信。
    private async Task CacheReceived(string accountId, CancellationToken token)
    {
        try
        {
            await _prefetchDownloads.WaitAsync(token);
            try
            {
                var account = Account(accountId);
                MailRow[] rows;
                lock (_gate) rows = _db.Rows.Where(r => r.AccountId == accountId && r.Size is > 0 and <= 20971520)
                    .OrderByDescending(r => r.Date).ToArray();
                rows = rows.Where(r => !_diskCache.Contains(r)).ToArray();
                if (rows.Length == 0) return;
                using var client = await Connect(account, token);
                var folder = client.Inbox;
                await folder.OpenAsync(FolderAccess.ReadOnly, token);
                await CacheFolder(account, folder, rows, token);
            }
            finally { _prefetchDownloads.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            lock (_gate) if (_db.Accounts.Any(a => a.Id == accountId))
                _cacheErrors[accountId] = "部分邮件尚未缓存，将在后续同步时重试。";
        }
    }
    private async Task CacheFolder(MailAccount account, IMailFolder folder, MailRow[] rows, CancellationToken token)
    {
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.Size is 0 or > 20971520 || folder.UidValidity != row.Validity) continue;
            if (Account(account.Id).UseSystemProxy != account.UseSystemProxy) return;
            if (_diskCache.Contains(row)) continue;
            try
            {
                using var message = await folder.GetMessageAsync(new UniqueId(row.Uid), token);
                if (!SaveMessage(row, message, token)) return;
            }
            catch (MessageNotFoundException) { /* 邮件在同步之后被移走，继续缓存其他邮件。 */ }
        }
    }
    private bool SaveMessage(MailRow row, MimeMessage message, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            Account(row.AccountId);
            _diskCache.Write(row, message, token);
            lock (_gate) _cacheErrors.Remove(row.AccountId);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            lock (_gate) if (_db.Accounts.Any(a => a.Id == row.AccountId))
                _cacheErrors[row.AccountId] = "邮件缓存写入失败，请检查磁盘空间和目录权限；在线阅读仍可使用。";
            return false;
        }
    }
    public Task<MailStorageUsage> StorageUsageAsync(string accountId, CancellationToken token) => Task.Run(() =>
    {
        var usage = _diskCache.Measure(accountId, token);
        lock (_gate) return usage with { Error = string.Join("\n", new[] { usage.Error, _cacheErrors.GetValueOrDefault(accountId, "") }.Where(s => s.Length > 0)) };
    }, token);

    public async Task<MimeMessage> LoadMessage(MailRow row, CancellationToken token)
    {
        Account(row.AccountId);
        var local = await Task.Run(() => _diskCache.Read(row, token), token);
        if (local is not null) { Account(row.AccountId); return local; }
        await _downloads.WaitAsync(token);
        try
        {
            var a = Account(row.AccountId);
            // 内存仍有界；磁盘副本不随内存淘汰。同一账号连续阅读复用连接。
            if (_bodyCache.TryGetValue(row.Key, out var cached) && DateTime.UtcNow - cached.Used < TimeSpan.FromMinutes(10))
            { _bodyCache[row.Key] = (cached.Message, DateTime.UtcNow, cached.Size); return cached.Message; }
            if (_readerClient is not { IsConnected: true, IsAuthenticated: true } || _readerAccount != a.Id || _readerProxy != a.UseSystemProxy)
            {
                _readerClient?.Dispose(); _readerClient = null;
                _readerClient = await Connect(a, token); _readerAccount = a.Id; _readerProxy = a.UseSystemProxy;
            }
            var client = _readerClient;
            var folder = client.Inbox; await folder.OpenAsync(FolderAccess.ReadOnly, token);
            if (folder.UidValidity != row.Validity) throw new InvalidOperationException("邮箱已重建，请刷新列表。");
            var sizes = await folder.FetchAsync(new[] { new UniqueId(row.Uid) }, MessageSummaryItems.Size, token);
            if (sizes.Count == 0) throw new InvalidOperationException("邮件已移走或删除，请刷新。");
            if (sizes[0].Size is not uint size || size > 20971520) throw new InvalidOperationException("邮件超过 20 MB 或大小未知，请使用邮箱网页查看。");
            var message = await folder.GetMessageAsync(new UniqueId(row.Uid), token);
            await Task.Run(() => SaveMessage(row, message, token), token);
            _bodyCache[row.Key] = (message, DateTime.UtcNow, size);
            while (_bodyCache.Count > 12 || _bodyCache.Values.Sum(x => (long)x.Size) > 32 * 1024 * 1024)
                _bodyCache.Remove(_bodyCache.MinBy(x => x.Value.Used).Key);
            return message;
        }
        catch { _readerClient?.Dispose(); _readerClient = null; throw; }
        finally { _downloads.Release(); }
    }
    public async Task MarkRead(MailRow row, CancellationToken token)
    {
        var a = Account(row.AccountId);
        using var client = await Connect(a, token);
        var folder = client.Inbox; await folder.OpenAsync(FolderAccess.ReadWrite, token);
        if (folder.UidValidity != row.Validity) throw new InvalidOperationException("邮箱已重建，请刷新列表。");
        await folder.AddFlagsAsync(new[] { new UniqueId(row.Uid) }, MessageFlags.Seen, true, token);
        lock (_gate)
        {
            if (_db.Accounts.Any(x => x.Id == a.Id))
                Commit(_db with { Rows = _db.Rows.Select(r => r.Key == row.Key ? r with { Seen = true } : r).ToArray() });
        }
        Refresh();
    }
    private MailAccount Account(string id)
    {
        lock (_gate) return _db.Accounts.FirstOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("账户已移除。");
    }
    private void Commit(MailDatabase next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(next), null, DataProtectionScope.CurrentUser);
        var temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, _path, true);
        _db = next; _revision++;
    }
    private static string Clip(string? s, int max, string fallback = "") =>
        string.IsNullOrWhiteSpace(s) ? fallback : s.Length > max ? s[..max] : s;
    private static bool HasTimeout(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is TimeoutException || current is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.TimedOut }) return true;
        return false;
    }
    // 仅清理应用密码复制时的分组空白，不读取或记录用户凭据。
    public static string NormalizeSecret(string provider, string secret) => provider is "Gmail" or "TencentExmail"
        ? new string(secret.Where(c => !char.IsWhiteSpace(c)).ToArray())
        : secret.Trim().Replace(" ", "");
    private static string AuthenticationError(Exception error)
    {
        // 只展示预定义诊断类别，避免服务器原文携带账号或认证数据。
        if (error.Data["MailProvider"] is "TencentExmail")
            return EnterpriseAuthenticationError(error);
        var messages = new List<string>();
        for (Exception? current = error; current is not null; current = current.InnerException)
            messages.Add(current.Message);
        var text = string.Join(" ", messages);
        if (text.Contains("Application-specific password required", StringComparison.OrdinalIgnoreCase) || text.Contains("5.7.9"))
            return "认证被拒绝：服务器要求应用专用密码（5.7.9）。网络与 TLS 已连接。";
        if (text.Contains("Web login required", StringComparison.OrdinalIgnoreCase) || text.Contains("5.7.14"))
            return "认证被拒绝：服务器要求网页验证或安全确认。请在 Google 账户中查看安全提示。网络与 TLS 已连接。";
        if (text.Contains("Too many", StringComparison.OrdinalIgnoreCase) || text.Contains("4.7.0"))
            return "认证暂被限制：服务器提示尝试过多或临时限制，请稍后重试。网络与 TLS 已连接。";
        if (text.Contains("Invalid credentials", StringComparison.OrdinalIgnoreCase) || text.Contains("AUTHENTICATIONFAILED", StringComparison.OrdinalIgnoreCase) || text.Contains("5.7.8"))
            return "认证被拒绝：服务器返回凭据未通过（AUTHENTICATIONFAILED／5.7.8）。可能是账号与应用密码不匹配、密码失效或账户策略；此返回不能进一步区分。网络与 TLS 已连接。";
        return "认证被拒绝：服务器未返回可识别的具体原因。网络与 TLS 已连接；暂不能断定是密码错误。";
    }
    private static string EnterpriseAuthenticationError(Exception error)
    {
        var text = error.Message + " " + error.InnerException?.Message;
        var category = text.Contains("LOGIN fail", StringComparison.OrdinalIgnoreCase) ? "LOGIN 被拒绝"
            : text.Contains("AUTHENTICATE fail", StringComparison.OrdinalIgnoreCase) ? "AUTHENTICATE 被拒绝"
            : text.Contains("AUTHENTICATIONFAILED", StringComparison.OrdinalIgnoreCase) ? "AUTHENTICATIONFAILED"
            : "认证被拒绝（原因未明确）";
        if (text.Contains("disabled", StringComparison.OrdinalIgnoreCase) || text.Contains("not enabled", StringComparison.OrdinalIgnoreCase))
            category = "服务器提示服务或账户未启用";
        if (text.Contains("too many", StringComparison.OrdinalIgnoreCase) || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            category = "服务器提示尝试过多，请稍后重试";
        return "腾讯企业邮箱：" + category + "。网络与 TLS 已连接。\n请核对：完整邮箱地址与生成密码的账号一致；客户端页 IMAP/SMTP 已勾选并保存；使用客户端专用密码。若均正确，请学校管理员检查第三方客户端服务范围／IP 限制，或用同一账号在 Foxmail 中手动配置 IMAP 对照验证。";
    }
    public static string FriendlyError(Exception error) => error switch
    {
        MailKit.Security.AuthenticationException => AuthenticationError(error),
        MailKit.Security.SslHandshakeException when HasTimeout(error) => "TLS 握手超时，尚未验证账号密码。请切换代理节点或检查 IMAP 993 端口路由；网页可用不代表邮箱端口可用。",
        MailKit.Security.SslHandshakeException => "TLS 握手失败，请检查代理链路、系统时间和证书信任；未关闭证书校验。",
        TimeoutException => "连接超时，请检查代理节点与 IMAP 993 端口路由。",
        OperationCanceledException => "连接超时或操作已取消，请检查代理是否支持 IMAP 993 端口",
        MailKit.Net.Proxy.ProxyProtocolException => "代理连接失败：请确认系统代理允许 CONNECT 到 IMAP 993 端口",
        IOException or System.Net.Sockets.SocketException => "网络暂不可用，将自动重连",
        ImapCommandException => "邮箱拒绝请求，请确认 IMAP 已开启及账户访问权限",
        ArgumentException or InvalidOperationException => error.Message,
        _ => "连接或读取失败，请检查网络与账户设置后重试"
    };
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var worker in _workers.Values) { worker.Stop.Cancel(); worker.Signal(); _ = Task.WhenAll(worker.Task!, worker.CacheTask ?? Task.CompletedTask).ContinueWith(_ => worker.Stop.Dispose()); }
            _workers.Clear();
        }
        _ = Task.Run(async () => { await _downloads.WaitAsync(); try { _readerClient?.Dispose(); _readerClient = null; _bodyCache.Clear(); } finally { _downloads.Release(); } });
    }
}
