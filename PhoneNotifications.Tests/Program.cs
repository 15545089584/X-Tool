using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenshotApp.Collaboration;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Contains("--screenshot-shelf"))
{
    Exception? error = null;
    var thread = new Thread(() => { try { ScreenshotShelfValidation.Run(); } catch (Exception ex) { error = ex; } });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    if (error is not null) throw error;
    return;
}
if (args.Contains("--ui"))
{
    Exception? error = null;
    var thread = new Thread(() => { try { UiPreview.Run(); } catch (Exception ex) { error = ex; } });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    if (error is not null) throw error;
    return;
}
var count = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("通过：" + name); count++; }
void Reject(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception(name); }
var pixels = new byte[] { 240, 120, 50, 255, 240, 120, 50, 255, 240, 120, 50, 255, 240, 120, 50, 255 };
var sampleBitmap = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 8);
var pngEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); pngEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(sampleBitmap));
using var pngStream = new MemoryStream(); pngEncoder.Save(pngStream);
var avatar = Convert.ToBase64String(pngStream.ToArray());
Check(PhoneNotificationImage.Decode(avatar) is { IsFrozen: true, PixelWidth: 2 }, "头像安全解码并冻结");
Check(PhoneNotificationImage.Decode(null) is null, "旧版无头像通知兼容");
Reject(() => PhoneNotificationImage.Validate(new string('A', 22001)), "拒绝过大头像");
Reject(() => PhoneNotificationImage.Validate("https://example.invalid/avatar.png"), "拒绝远程头像地址");
var oversizedPng = pngStream.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(oversizedPng.AsSpan(16, 4), 10000);
Reject(() => PhoneNotificationImage.Validate(Convert.ToBase64String(oversizedPng)), "拒绝异常像素尺寸");
var epoch = Guid.NewGuid().ToString();
PhoneNotificationSnapshot Snapshot(long sequence, params PhoneNotificationItem[] items) => new("test-phone", epoch, sequence, true, items);
var first = new PhoneNotificationItem("key", "test.app", "测试应用", "标题", "测试正文", 1);
var state = new PhoneNotificationState();
Check(state.Apply(Snapshot(1, first)), "新增通知");
Check(!state.Apply(Snapshot(1, first)), "重试幂等");
Check(!state.Apply(Snapshot(0, first)), "乱序不覆盖");
state.Apply(Snapshot(2, first with { Text = "已更新" }));
Check(state.Items.Single().Text == "已更新", "同键更新");
state.Apply(Snapshot(3)); Check(state.Items.Length == 0, "移除对账");
Reject(() => state.Apply(Snapshot(4, first, first)), "拒绝重复键");
Reject(() => state.Apply(Snapshot(4, first with { Text = new string('x', 2001) })), "正文长度限制");
Reject(() => state.Apply(Snapshot(4, Enumerable.Range(0, 201).Select(i => first with { Key = i.ToString() }).ToArray())), "条目容量限制");
Reject(() => state.Apply(Snapshot(4, first) with { DeviceId = "other" }), "设备隔离");
state.Apply(Snapshot(4, first) with { Enabled = false }); Check(state.Items.Length == 0, "暂停同步清空状态");
state.Apply(Snapshot(5, first)); state.Expire(DateTime.UtcNow.AddMinutes(11)); Check(state.Items.Length == 0, "断线正文十分钟过期");
state.Apply(Snapshot(0, first) with { Epoch = Guid.NewGuid().ToString() });
Reject(() => state.Apply(Snapshot(6, first)), "拒绝已结束会话重放");

var folder = Path.Combine(Path.GetTempPath(), "XTool-NotifyTests-" + Guid.NewGuid().ToString("N"));
using var hub = new PhoneNotificationHub(folder);
try
{
    hub.Start(0);
    using var pairing = JsonDocument.Parse(hub.PairingPayload("127.0.0.1", "test"));
    var secret = pairing.RootElement.GetProperty("secret").GetString()!;
    var pin = pairing.RootElement.GetProperty("fingerprint").GetString()!;
    var combined = new Uri(hub.CollaborationPairingPayload("127.0.0.1", 18120, "123456", "test"));
    Check(combined.Query == "?pin=123456" && !combined.Query.Contains(secret), "通知凭据不进入 HTTP 配对查询");
    var fragment = combined.Fragment["#xtool-notify=".Length..].Replace('-', '+').Replace('_', '/');
    using var decoded = JsonDocument.Parse(Convert.FromBase64String(fragment.PadRight((fragment.Length + 3) / 4 * 4, '=')));
    Check(decoded.RootElement.GetProperty("fingerprint").GetString() == pin && decoded.RootElement.GetProperty("secret").GetString() == secret, "主二维码完整携带通知安全身份");
    using var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && Convert.ToHexString(SHA256.HashData(cert.RawData)) == pin };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
    var url = $"https://127.0.0.1:{hub.BoundPort}/api/v2/notifications/snapshot";
    async Task<int> Send(string authorization, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + authorization);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (body.Length > 512 * 1024) request.Headers.ExpectContinue = true;
        using var response = await client.SendAsync(request); return (int)response.StatusCode;
    }
    var payload = JsonSerializer.Serialize(Snapshot(1, first with { Avatar = avatar, AvatarKind = "sender" }));
    var alerts = 0;
    hub.AlertsEnabled = true;
    hub.NotificationsArrived += items => Interlocked.Add(ref alerts, items.Length);
    Check(await Send(secret, payload) == 200 && hub.State.Items.Length == 1, "实际 TLS 传输与状态接收");
    Check(hub.State.Items.Single().Avatar == avatar && hub.State.Items.Single().AvatarKind == "sender", "头像随 TLS 快照完整到达");
    Check(alerts == 0, "首次对账不弹旧通知");
    Check(await Send(secret, payload) == 200 && alerts == 0, "重试不重复提醒");
    Check(await Send("wrong", payload) == 401, "拒绝错误凭据");
    Check(await Send(secret, new string('x', 513 * 1024)) == 413, "请求字节上限");
    hub.Paused = true; Check(await Send(secret, payload) == 409, "电脑暂停接收"); hub.Paused = false;
    hub.Locked = true;
    Check(await Send(secret, JsonSerializer.Serialize(Snapshot(2, first with { Title = "锁屏测试" }))) == 200 && alerts == 0, "电脑锁屏抑制提醒");
    hub.Locked = false;
    using (var wrongHandler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && Convert.ToHexString(SHA256.HashData(cert.RawData)) == new string('0', 64) })
    using (var wrongClient = new HttpClient(wrongHandler))
    {
        var failed = false; try { await wrongClient.PostAsync(url, new StringContent(payload)); } catch (HttpRequestException) { failed = true; }
        Check(failed, "不匹配的证书不能建立 TLS 通道");
    }
    using (var second = new PhoneNotificationHub(folder)) Reject(() => second.Start(0), "第二实例不能接管相同身份");
    hub.Revoke(); Check(await Send(secret, payload) == 401 && hub.State.Items.Length == 0, "撤销后旧凭据立即失效");
    Console.WriteLine($"全部通过：{count} 项；仅使用合成通知及临时测试身份。");
}
finally
{
    hub.Dispose();
    // 仅回收本次生成且位于临时目录下的测试身份，不触碰真实配对。
    if (Directory.GetParent(Path.GetFullPath(folder))?.FullName == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) && Path.GetFileName(folder).StartsWith("XTool-NotifyTests-")) Directory.Delete(folder, true);
}
