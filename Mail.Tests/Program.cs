using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailKit;
using MimeKit;
using ScreenshotApp.Mail;
using MailService = ScreenshotApp.Mail.MailService;

var root = Path.Combine(Path.GetTempPath(), "xtool-mail-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool good, string name) { if (!good) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
if (args.Contains("--charset-probe"))
{
    var encoding = CodePagesEncodingProvider.Instance.GetEncoding(936)!;
    var encoded = Convert.ToBase64String(encoding.GetBytes("企业邮箱异地登录提醒"));
    using var input = new MemoryStream(Encoding.ASCII.GetBytes($"From: =?gb2312?B?{encoded}?= <test@example.invalid>\r\nSubject: =?gb2312?B?{encoded}?=\r\nContent-Type: text/plain; charset=gb2312\r\nContent-Transfer-Encoding: base64\r\n\r\n{encoded}"));
    using var message = MimeMessage.Load(input);
    Console.WriteLine($"Subject: {message.Subject}\nBody: {message.TextBody}");
    return;
}
Check(MailService.FriendlyError(new MailKit.Security.SslHandshakeException("synthetic",new TimeoutException())).Contains("尚未验证账号密码"), "TLS 超时与认证失败分离");
using (var initialized = new MailService(root))
{
    foreach (var charset in new[] { "gb2312", "gbk", "gb18030", "big5", "utf-8" })
    {
        var expected = "企業郵箱登入提醒";
        var encoded = Convert.ToBase64String(Encoding.GetEncoding(charset).GetBytes(expected));
        using var input = new MemoryStream(Encoding.ASCII.GetBytes($"From: =?{charset}?B?{encoded}?= <test@example.invalid>\r\nSubject: =?{charset}?B?{encoded}?=\r\nContent-Type: text/html; charset={charset}\r\nContent-Transfer-Encoding: base64\r\n\r\n{encoded}"));
        using var message = MimeMessage.Load(input);
        Check(message.Subject == expected && message.From.Mailboxes.Single().Name == expected && message.HtmlBody == expected, charset + " 标题、发件人与正文解码");
    }
}
Check(!JsonSerializer.Deserialize<MailAccount>("{\"Id\":\"old\",\"Provider\":\"Gmail\",\"Address\":\"a@example.invalid\",\"Secret\":\"synthetic\"}")!.UseSystemProxy, "旧账户缺省直连");
Check(JsonSerializer.Deserialize<MailAccount>(JsonSerializer.Serialize(new MailAccount("id","Gmail","a@example.invalid","synthetic",UseSystemProxy:true)))!.UseSystemProxy, "账户代理选择可持久化");
var legacy = JsonSerializer.Deserialize<MailAccount>("{\"Id\":\"old\",\"Provider\":\"QQ\",\"Address\":\"a@example.invalid\",\"Secret\":\"synthetic\"}")!;
Check(string.IsNullOrEmpty(legacy.Remark) && legacy.Host == "imap.qq.com", "旧账户备注缺省兼容");
var enterprise = new MailAccount("school", "TencentExmail", "student@example.invalid", "synthetic", Remark: "教育邮箱");
typeof(MailService).GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [enterprise]);
Check(enterprise.Host == "imap.exmail.qq.com" && JsonSerializer.Deserialize<MailAccount>(JsonSerializer.Serialize(enterprise))!.Remark == "教育邮箱", "腾讯企业邮服务器与中文备注持久化");
var account = new MailAccount("test", "QQ", "synthetic@example.invalid", "TEST-ONLY-NOT-A-CREDENTIAL");
var service = new MailService(root);
var dbField = typeof(MailService).GetField("_db", BindingFlags.NonPublic | BindingFlags.Instance)!;
dbField.SetValue(service, new MailDatabase([account], [], new()));
var method = typeof(MailService).GetMethod("Synchronize", BindingFlags.NonPublic | BindingFlags.Instance)!;
var folder = DispatchProxy.Create<IMailFolder, MailFolderDouble>();
var fake = (MailFolderDouble)folder;
var arrivals = new List<MailArrival>();
service.Arrived += arrivals.Add;
async Task Sync() => await (Task)method.Invoke(service, [account, folder, CancellationToken.None])!;
void SetRows(params (uint Uid, bool Seen)[] values)
{
    fake.Rows = values.Select((v, i) => (IMessageSummary)new MessageSummary(i) {
        UniqueId = new UniqueId(v.Uid), Flags = v.Seen ? MessageFlags.Seen : MessageFlags.None,
        Envelope = new Envelope { Subject = "合成测试 " + v.Uid, Date = DateTimeOffset.UtcNow }, Size = 100
    }).ToList();
}
SetRows((10, false), (11, true)); await Sync();
Check(arrivals.Count == 0 && service.Read().Rows.Length == 2, "首次同步建立基线，不追发历史未读");
SetRows((10, false), (11, true), (12, false), (13, false)); await Sync();
Check(arrivals.Count == 1 && arrivals[0].Rows.Length == 2, "一批新邮件只发一次合并事件");
await Sync(); Check(arrivals.Count == 1, "重复同步与重连不重复提醒");
SetRows((12, true), (13, false)); await Sync();
Check(service.Read().Rows.Length == 2 && service.Read().Rows.Single(r => r.Uid == 12).Seen, "服务器移走和已读状态同步");
SetRows((12, true), (13, false), (14, true)); await Sync();
Check(arrivals.Count == 1, "已读的新邮件不弹提醒");
fake.Validity = 2; SetRows((1, false), (2, false)); await Sync();
Check(arrivals.Count == 1, "UIDVALIDITY 变化重新建立基线");
SetRows((1, false), (2, false), (3, false)); await Sync();
Check(arrivals.Count == 2 && arrivals[1].Rows.Single().Uid == 3, "重建后仍能识别新邮件");
var path = Path.Combine(root, "mail.bin");
var bytes = File.ReadAllBytes(path);
Check(!Encoding.UTF8.GetString(bytes).Contains(account.Secret), "磁盘不含明文凭据");
var saved = JsonSerializer.Deserialize<MailDatabase>(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser))!;
Check(saved.Accounts.Single().Secret == account.Secret && saved.Checkpoints["test"].HighUid == 3, "DPAPI 加密凭据与去重进度可还原");
var restored = new MailService(Path.Combine(root, "restored"));
dbField.SetValue(restored, saved);
var resumed = new List<MailArrival>(); restored.Arrived += resumed.Add;
await (Task)method.Invoke(restored, [account, folder, CancellationToken.None])!;
Check(resumed.Count == 0, "保存副本恢复后不重放提醒");
var before = service.Read().Revision;
var occupied = path + ".tmp"; Directory.CreateDirectory(occupied);
SetRows((4, false));
try { await Sync(); throw new Exception("应因暂存路径不可写而失败"); } catch (UnauthorizedAccessException) { }
Check(service.Read().Revision == before && arrivals.Count == 2, "持久化失败不推进内存进度也不发提醒");
Directory.Delete(occupied);
service.Preferences("test", false, false, false, 23, 8);
Check(!service.AllowNotification("test", out _), "关闭提醒不影响收信配置");
service.Preferences("test", false, false, false, 23, 8, remark: "  教育邮箱  ");
Check(service.Read().Accounts.Single().DisplayName == "教育邮箱", "修改备注立即用于账户显示且去除首尾空白");
service.Preferences("test", false, false, false, 23, 8);
Check(service.Read().Accounts.Single().Remark == "教育邮箱", "旧设置调用保留备注");
service.Preferences("test", false, false, false, 23, 8, remark: "");
Check(service.Read().Accounts.Single().DisplayName == "QQ", "清空备注恢复服务名称");
var quiet = account with { Quiet = true, QuietStart = 23, QuietEnd = 8 };
Check(MailRules.IsQuiet(quiet,23) && MailRules.IsQuiet(quiet,7) && !MailRules.IsQuiet(quiet,8), "跨午夜免打扰边界");
Check(MailRules.IsQuiet(quiet with { QuietStart = 8 },12), "相同起止为全天免打扰");
Check(MailRules.SafeFileName(@"../../秘密\file.txt") == "file.txt", "附件名剥离目录穿越路径");
var html = MailRules.HtmlText("<p>你好 &amp; 邮件</p><script>alert('bad')</script><img src='https://example.invalid/track'>");
Check(html.Contains("你好") && !html.Contains("alert") && !html.Contains("https://"), "HTML 转纯文本不保留脚本与远程图片");
var corruptRoot = Path.Combine(root, "corrupt"); Directory.CreateDirectory(corruptRoot);
File.WriteAllBytes(Path.Combine(corruptRoot, "mail.bin"), [1,2,3]);
using var corrupt = new MailService(corruptRoot); corrupt.Start();
Check(corrupt.Read().Error.Length > 0 && File.ReadAllBytes(Path.Combine(corruptRoot, "mail.bin")).SequenceEqual(new byte[] {1,2,3}), "损坏缓存保留原件并停止加载");
var cachedBody = new MimeMessage { Body = new TextPart("plain") { Text = "合成缓存正文" } };
var cachedRow = new MailRow("test",2,99,"synthetic@example.invalid","缓存测试",DateTimeOffset.UtcNow,false,100);
var bodyCache = (Dictionary<string,(MimeMessage Message,DateTime Used,uint Size)>)typeof(MailService).GetField("_bodyCache",BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
bodyCache[cachedRow.Key] = (cachedBody,DateTime.UtcNow,100);
Check(ReferenceEquals(await service.LoadMessage(cachedRow,CancellationToken.None),cachedBody), "重复阅读命中内存缓存，无需网络");
service.Remove("test");
try { await service.LoadMessage(cachedRow,CancellationToken.None); throw new Exception("已移除账号不可读取缓存"); }
catch (InvalidOperationException) { Check(true,"移除账号后缓存不可读取"); }
Check(service.Read().Accounts.Length == 0 && service.Read().Rows.Length == 0, "移除账户清空本机摘要与配置");
Check(MailService.NormalizeSecret("Gmail", "abcd\u00a0efgh\tijkl\r\nmnop") == "abcdefghijklmnop", "应用密码分组空白包含不间断空格");
Check(MailService.FriendlyError(new MailKit.Security.AuthenticationException("Invalid credentials (Failure) private@example.test")).Contains("凭据未通过"), "识别服务器凭据拒绝");
Check(MailService.FriendlyError(new MailKit.Security.AuthenticationException("Application-specific password required 5.7.9")).Contains("要求应用专用密码"), "区分应用密码要求");
Check(!MailService.FriendlyError(new MailKit.Security.AuthenticationException("private@example.test secret-data")).Contains("secret-data"), "认证错误不泄露服务器原文");
Check(MailService.NormalizeSecret("TencentExmail", "abcd\u00a0efgh\tijkl\r\nmnop") == "abcdefghijklmnop", "企业邮箱专用密码清理复制分组空白");
var enterpriseError = new MailKit.Security.AuthenticationException("LOGIN failed private@example.test secret-data");
enterpriseError.Data["MailProvider"] = "TencentExmail";
var diagnosis = MailService.FriendlyError(enterpriseError);
Check(diagnosis.Contains("LOGIN 被拒绝") && diagnosis.Contains("IMAP/SMTP") && !diagnosis.Contains("private@example") && !diagnosis.Contains("secret-data"), "企业邮箱诊断分类且不泄露服务端原文");
using (var syncService = new MailService(Path.Combine(root, "uid-sync")))
{
    dbField.SetValue(syncService, new MailDatabase([account], [], new()));
    fake.Rows = Enumerable.Range(1, 250).Select(i => (IMessageSummary)new MessageSummary(i - 1) { UniqueId = new UniqueId((uint)i), Envelope = new Envelope { Subject = "合成邮件" }, Flags = MessageFlags.Seen }).ToList();
    await (Task)method.Invoke(syncService, [account, folder, CancellationToken.None])!;
    Check(syncService.Read().Rows.Length == 200 && syncService.Read().Rows.Min(r => r.Uid) == 51 && syncService.Read().Accounts.Single().InboxTotal == 250, "按 UID 读取最近 200 封并保留服务器总数");
    var revision = syncService.Read().Revision;
    fake.OmitLast = true;
    try { await (Task)method.Invoke(syncService, [account, folder, CancellationToken.None])!; throw new Exception("不应接受缺失摘要"); }
    catch (InvalidOperationException) { Check(syncService.Read().Revision == revision && syncService.Read().Rows.Length == 200, "不完整 FETCH 不覆盖已有列表"); }
    fake.OmitLast = false; fake.Rows.Clear();
    await (Task)method.Invoke(syncService, [account, folder, CancellationToken.None])!;
    Check(syncService.Read().Rows.Length == 0 && syncService.Read().Accounts.Single().InboxTotal == 0, "空收件箱同步清理旧摘要");
}
var translationEngine = new TranslationDouble();
var translatedMail = await MailTranslation.TranslateAsync("A new account alert", "First English paragraph.\n\u034f\u200c\u00a0\u034f\u200c\nhttps://example.invalid/path\n保留中文段落\nFinal English paragraph.", translationEngine, CancellationToken.None);
Check(translatedMail.Subject == "译文：A new account alert" && translatedMail.Body.Contains("译文：First English paragraph.") && translatedMail.Body.Contains("译文：Final English paragraph."), "标题和正文分段翻译，无遗漏末段");
Check(!translationEngine.Inputs.Any(t => t.Contains('\u034f') || t.Contains('\u200c') || t.Contains("https://") || t.Contains("保留中文")), "不可见填充、链接和中文不送入英文模型");
Check(translatedMail.Body.Contains("https://example.invalid/path") && translatedMail.Body.Contains("保留中文段落"), "网址与中文原样保留");
var longSource = string.Join(" ", Enumerable.Repeat("Long newsletter content", 90));
var segments = MailTranslation.Segments(longSource).ToArray();
Check(segments.All(s => s.Length <= 280) && string.Concat(segments) == longSource, "长段落分块有界且不丢失内容");
Check(!MailTranslation.IsUsable("<unk> <unk>") && !MailTranslation.IsUsable(string.Concat(Enumerable.Repeat("重复片段", 8))), "拒绝未知词元与重复解码输出");
translationEngine.Fail = true;
var brandMail = await MailTranslation.TranslateAsync("[Docker] You + Docker = Ready for Action", "Welcome to Docker q47595101800! Download Docker Desktop and visit Docker Hub.", translationEngine, CancellationToken.None);
Check(brandMail.Subject.Contains("[Docker]") && brandMail.Body.Contains("Docker Desktop") && brandMail.Body.Contains("Docker Hub") && brandMail.Body.Contains("q47595101800"), "品牌、产品名和用户名保持原文");
Check(!translationEngine.Inputs.Any(t => t.Contains("Docker") || t.Contains("q47595101800")), "受保护品牌与账号不送入模型");
var shortMail = await MailTranslation.TranslateAsync("Download", "Download Docker Desktop and sign in to Docker Hub.", translationEngine, CancellationToken.None);
Check(shortMail.Subject == "下载" && shortMail.Body == "下载 Docker Desktop 并登录 Docker Hub.", "品牌间短操作文案使用明确术语");
var fallback = await MailTranslation.TranslateAsync("Subject alert", "First English paragraph.", translationEngine, CancellationToken.None);
Check(fallback.Subject == "Subject alert" && fallback.Body == "First English paragraph." && fallback.RetainedSegments == 2, "异常标题和正文逐段回退原文");
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    try { await MailTranslation.TranslateAsync("Title alert", "Body text", translationEngine, canceled.Token); throw new Exception("翻译未响应取消"); }
    catch (OperationCanceledException) { Check(true, "翻译取消不继续输出"); }
}
service.Dispose(); restored.Dispose();
var diskRoot = Path.Combine(root, "persistent");
var disk = new MailDiskCache(diskRoot);
var diskRow = cachedRow with { Uid = 101 };
var builder = new BodyBuilder { TextBody = "离线测试正文", HtmlBody = "<p>离线测试正文</p>" };
builder.Attachments.Add("合成附件.txt", Encoding.UTF8.GetBytes("附件内容"));
using var diskMessage = new MimeMessage { Subject = "离线邮件", Body = builder.ToMessageBody() };
disk.Write(diskRow, diskMessage, CancellationToken.None);
var persisted = File.ReadAllBytes(disk.MessagePath(diskRow));
Check(!Encoding.UTF8.GetString(persisted).Contains("离线测试正文"), "邮件正文不以明文落盘");
var reopened = new MailDiskCache(diskRoot);
using var readBack = reopened.Read(diskRow, CancellationToken.None)!;
Check(readBack.Subject == diskMessage.Subject && readBack.TextBody == builder.TextBody && readBack.HtmlBody == builder.HtmlBody && readBack.Attachments.Count() == 1, "新缓存实例完整恢复标题、双格式正文及附件");
using (var attachment = new MemoryStream())
{
    ((MimePart)readBack.Attachments.Single()).Content!.DecodeTo(attachment);
    Check(Encoding.UTF8.GetString(attachment.ToArray()) == "附件内容", "加密缓存附件字节无损");
}
Check(reopened.Read(diskRow with { AccountId = "other" }, CancellationToken.None) is null && reopened.Read(diskRow with { Validity = 99 }, CancellationToken.None) is null, "账户及 UIDVALIDITY 隔离缓存");
Check(Path.GetFullPath(disk.MessagePath(diskRow with { AccountId = "../../escape" })).StartsWith(Path.GetFullPath(diskRoot) + Path.DirectorySeparatorChar), "账户标识不可穿越存储目录");
var usageBefore = disk.Measure("test", CancellationToken.None);
Check(usageBefore.Messages == 1 && usageBefore.Bytes == persisted.Length && usageBefore.AccountBytes == persisted.Length, "占用统计来自实际加密文件长度");
disk.Write(diskRow, diskMessage, CancellationToken.None);
Check(disk.Measure("test", CancellationToken.None).Messages == 1 && !Directory.EnumerateFiles(diskRoot,"*.tmp",SearchOption.AllDirectories).Any(), "重复写入原子覆盖，不积累临时文件");
using (var offline = new MailService(diskRoot))
{
    dbField.SetValue(offline, new MailDatabase([account], [diskRow], new()));
    using var loaded = await offline.LoadMessage(diskRow, CancellationToken.None);
    Check(loaded.TextBody == "离线测试正文", "全新 MailService 无网络即可读取已有磁盘邮件");
    var cacheFolder = typeof(MailService).GetMethod("CacheFolder", BindingFlags.Instance | BindingFlags.NonPublic)!;
    fake.Validity = diskRow.Validity; fake.Message = diskMessage; fake.Downloads = 0;
    var rows = new[] { diskRow, diskRow with { Uid = 102 }, diskRow with { Uid = 103, Size = 20971521 }, diskRow with { Uid = 104, Size = 0 }, diskRow with { Uid = 105, Validity = 99 } };
    await (Task)cacheFolder.Invoke(offline, [account, folder, rows, CancellationToken.None])!;
    Check(fake.Downloads == 1 && disk.Contains(rows[1]) && !disk.Contains(rows[2]) && !disk.Contains(rows[3]) && !disk.Contains(rows[4]), "后台自动下载缺失邮件，跳过已有、超限、未知大小及失效 UID");
    await (Task)cacheFolder.Invoke(offline, [account, folder, rows, CancellationToken.None])!;
    Check(fake.Downloads == 1, "重复同步不重复下载已缓存邮件");
    offline.Remove("test");
    Check(disk.Contains(diskRow), "移除账号不自动淘汰加密邮件文件");
    try { await offline.LoadMessage(diskRow, CancellationToken.None); throw new Exception("移除账户不应读取磁盘缓存"); }
    catch (InvalidOperationException) { Check(true, "账户移除后不能通过服务读取磁盘缓存"); }
}
File.WriteAllBytes(disk.MessagePath(diskRow), [1, 2, 3]);
Check(disk.Read(diskRow, CancellationToken.None) is null && File.ReadAllBytes(disk.MessagePath(diskRow)).Length == 3, "损坏缓存回退且保留原件");
disk.Write(diskRow, diskMessage, CancellationToken.None);
Check(disk.Read(diskRow, CancellationToken.None)!.Subject == "离线邮件", "在线副本可替换损坏缓存");
var blockedRoot = Path.Combine(root, "blocked-cache");
Directory.CreateDirectory(blockedRoot);
File.WriteAllText(Path.Combine(blockedRoot, "messages"), "synthetic", Encoding.UTF8);
using (var blockedService = new MailService(blockedRoot))
{
    dbField.SetValue(blockedService, new MailDatabase([account], [], new()));
    var saveMessage = typeof(MailService).GetMethod("SaveMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
    Check(!(bool)saveMessage.Invoke(blockedService, [diskRow, diskMessage, CancellationToken.None])! && (await blockedService.StorageUsageAsync(account.Id, CancellationToken.None)).Error.Contains("写入失败"), "磁盘不可写不会抛出阅读错误，设置显示缓存失败");
}
using (var stopped = new CancellationTokenSource())
{
    stopped.Cancel();
    try { disk.Write(diskRow with { Uid = 888 }, diskMessage, stopped.Token); throw new Exception("取消后不应写入"); }
    catch (OperationCanceledException) { Check(!disk.Contains(diskRow with { Uid = 888 }), "取消写入不产生已完成缓存"); }
}
Console.WriteLine($"通过 {passed} 项；合成测试证据：{root}");

public class MailFolderDouble : DispatchProxy
{
    public uint Validity = 1;
    public bool OmitLast;
    public MimeMessage? Message;
    public int Downloads;
    private Task<MimeMessage> Download()
    {
        Downloads++;
        using var stream = new MemoryStream();
        Message!.WriteTo(stream); stream.Position = 0;
        return Task.FromResult(MimeMessage.Load(stream));
    }
    public IList<IMessageSummary> Rows = new List<IMessageSummary>();
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_Count" => Rows.Count,
        "get_UidValidity" => Validity,
        "SearchAsync" => Task.FromResult<IList<UniqueId>>(Rows.Select(r => r.UniqueId).ToList()),
        "FetchAsync" => Task.FromResult<IList<IMessageSummary>>(Rows.Where(r => ((IEnumerable<UniqueId>)args![0]!).Contains(r.UniqueId)).SkipLast(OmitLast ? 1 : 0).ToList()),
        "GetMessageAsync" => Download(),
        _ => throw new NotSupportedException(method.Name)
    };
}

public class TranslationDouble : ScreenshotApp.Translation.ITranslationEngine
{
    public bool IsReady => true;
    public string UnavailableReason => "";
    public List<string> Inputs = new();
    public bool Fail;
    public Task<ScreenshotApp.Translation.TranslationResult> TranslateAsync(ScreenshotApp.Translation.TranslationRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Inputs.Add(request.SourceText);
        return Task.FromResult(new ScreenshotApp.Translation.TranslationResult(request.SourceText, Fail ? "<unk> <unk>" : "译文：" + request.SourceText, TimeSpan.Zero, "synthetic"));
    }
}
