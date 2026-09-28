using System.IO;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using ScreenshotApp.PhoneCalendar;
using ScreenshotApp.Collaboration;

var root = Path.GetFullPath(Path.Combine("artifacts", "phone-calendar-validation", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
var basis = now;
var revision = 1L;
var assertions = 0;
var delivered = new List<CalendarEntry>();
using var service = new PhoneCalendarService(root, () => now);
service.Start(false);
service.Due += items => delivered.AddRange(items);
void Check(bool value, string name) { if (!value) throw new Exception(name); assertions++; Console.WriteLine("PASS " + name); }
CalendarEntry Entry(string id, long at) => new(id, "验证日历", "合成日程", "合成地点", basis + 3600000, basis + 7200000, false, false, [new(60, at)]);
CalendarSnapshot Snap(params CalendarEntry[] entries) => new(1, "synthetic-device", revision++, true, "Asia/Shanghai", basis - 86400000, basis + 120L * 86400000, entries);
bool Receive(CalendarSnapshot snapshot) => service.Receive(JsonSerializer.SerializeToUtf8Bytes(snapshot));
var first = Snap(Entry("a", now + 1000));
Check(Receive(first), "valid snapshot accepted");
now += 1001; service.Tick();
Check(delivered.Count == 1, "scheduled reminder delivered");
service.Tick(); Receive(first);
Check(delivered.Count == 1, "duplicate snapshot and tick do not replay");
Check(!service.Receive(Encoding.UTF8.GetBytes("{}")), "malformed snapshot rejected");
Check(service.Read().Snapshot!.Events.Length == 1, "invalid snapshot preserves prior state");
Check(Receive(Snap(Entry("b", now + 1000))), "replacement accepted");
Receive(Snap()); now += 1001; service.Tick();
Check(delivered.Count == 1, "deleted event cancels scheduled reminder");
var latest = Snap(Entry("c", now + 1000));
Receive(latest);
var stale = latest with { Revision = latest.Revision - 1, Events = [] };
Receive(stale);
Check(service.Read().Snapshot!.Events.Length == 1, "out of order snapshot cannot remove new event");
now += 1001;
Receive(latest with { Revision = revision++ });
service.Tick();
Check(delivered.Count == 2, "snapshot arriving at deadline does not swallow queued reminder");
Receive(Snap(Entry("past", now - 500)));
service.Tick();
Check(delivered.Count == 2, "first received past reminder is not replayed");
Receive(Snap(Entry("locked", now + 1000)));
PhoneNotificationHub.Instance.Locked = true;
now += 1001; service.Tick(); PhoneNotificationHub.Instance.Locked = false; service.Tick();
Check(delivered.Count == 2, "locked reminder not exposed or replayed on unlock");
Receive(Snap(Entry("paused", now + 1000)));
service.SetPaused(true); now += 1001; service.Tick(); service.SetPaused(false); service.Tick();
Check(delivered.Count == 2, "paused reminder not replayed");
Receive(Snap(Entry("sleep", now + 1000)));
now += 120000; service.Tick();
Check(delivered.Count == 2, "long sleep does not burst old reminders");
Receive(Snap(Entry("cleared", now + 1000)) with { Enabled = false, Events = [] });
now += 1001; service.Tick();
Check(service.Read().Snapshot?.Enabled == false && delivered.Count == 2, "phone disabling clears reminders");
var future = Snap(Entry("offline", now + 1000));
Receive(future);
using (var reopened = new PhoneCalendarService(root, () => now))
{
    reopened.Start(false);
    Check(reopened.Read().Snapshot!.Events[0].Id == "offline", "encrypted cache survives restart");
    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "calendar.bin"))).Contains("synthetic-device"), "cache does not store plaintext payload");
    var count = 0; reopened.Due += _ => count++;
    now += 1001; reopened.Tick();
    Check(count == 1, "cached future reminder fires without phone");
}
using (var reopened = new PhoneCalendarService(root, () => now))
{
    reopened.Start(false);
    var count = 0; reopened.Due += _ => count++;
    now -= 500; reopened.Tick(); now += 501; reopened.Tick();
    Check(count == 0, "persisted dedupe survives restart and clock rewind");
    reopened.Clear();
    Check(reopened.Read().Snapshot is null, "revocation clears calendar");
}
var duplicate = Snap(Entry("same", now + 1000), Entry("same", now + 2000));
Check(!Receive(duplicate), "duplicate instance IDs rejected");
var valid = Snap(Entry("id", now + 1000));
Check(!Receive(valid with { RangeEnd = basis + 500L * 86400000 }), "unbounded snapshot rejected");
Console.WriteLine($"Calendar: {assertions} assertions passed; synthetic encrypted artifacts: {root}");

// 真正的本地 TLS 请求，确认新路由仍需要固定证书及既有配对密钥。
using (var hub = new PhoneNotificationHub(Path.Combine(root, "transport")))
{
    hub.Start(0);
    using var pairing = JsonDocument.Parse(hub.PairingPayload("127.0.0.1", "test-server"));
    var secret = pairing.RootElement.GetProperty("secret").GetString()!;
    var fingerprint = pairing.RootElement.GetProperty("fingerprint").GetString()!;
    using var handler = new HttpClientHandler { UseProxy = false };
    handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null &&
        Convert.ToHexString(SHA256.HashData(cert.RawData)) == fingerprint;
    using var client = new HttpClient(handler);
    var calls = 0;
    hub.CalendarSnapshotReceived = bytes => { calls++; return Encoding.UTF8.GetString(bytes) == """{"schema":1}"""; };
    async Task<HttpStatusCode> Post(string key, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{hub.BoundPort}/api/v2/calendar/snapshot");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }
    Check(await Post("wrong-key", "{}") == HttpStatusCode.Unauthorized && calls == 0, "calendar route rejects invalid pairing token");
    hub.Paused = true;
    Check(await Post(secret, """{"schema":1}""") == HttpStatusCode.OK && calls == 1, "calendar route independent of ordinary notification pause");
    Check(await Post(secret, "{}") == HttpStatusCode.BadRequest, "rejected calendar payload is not acknowledged");
    var revoked = false; hub.PairingRevoked += () => revoked = true; hub.Revoke();
    Check(revoked && await Post(secret, """{"schema":1}""") == HttpStatusCode.Unauthorized, "revocation clears subscriber and rejects previous token");
}
Console.WriteLine($"Total: {assertions} assertions passed.");

// 双向同步只使用合成快照与本地队列，不写真机日历。
using (var writes = new PhoneCalendarService(Path.Combine(root, "writes"), () => now))
{
    writes.Start(false);
    var source = Entry("write-event", now + 3600000) with { EventId = 123, CalendarId = "1", Version = "original-version", CanEdit = true };
    var writable = Snap(source) with { Schema = 2, CanWrite = true, Calendars = [new("1", "个人", "#1999FF", true)] };
    Check(writes.Exchange(JsonSerializer.SerializeToUtf8Bytes(writable)) is not null, "v2 snapshot accepted");
    CalendarWriteCommand Command(string kind = "create") => new(Guid.NewGuid().ToString(), "synthetic-device",
        kind, kind == "update" ? 123 : 0, "1", kind == "update" ? "original-version" : "",
        "合成新建", "合成地点", "合成备注", now + 3600000, now + 7200000, false, [5,15], "", now + 120000);
    var create = Command();
    Check(writes.Queue(create) is null, "authorized create queued");
    var next = writable with { Revision = revision++ };
    using var response = JsonDocument.Parse(writes.Exchange(JsonSerializer.SerializeToUtf8Bytes(next))!);
    Check(response.RootElement.GetProperty("commands").GetArrayLength() == 1, "command returned to authorized phone");
    using var retry = JsonDocument.Parse(writes.Exchange(JsonSerializer.SerializeToUtf8Bytes(next with { Revision = revision++ }))!);
    Check(retry.RootElement.GetProperty("commands")[0].GetProperty("id").GetString() == create.Id, "retry preserves idempotency key");
    writes.Exchange(JsonSerializer.SerializeToUtf8Bytes(next with { Revision = revision++, Results = [new(create.Id,"applied",124)] }));
    Check(writes.Writes().Last().Status == "applied", "phone receipt required for success");
    using var afterAck = JsonDocument.Parse(writes.Exchange(JsonSerializer.SerializeToUtf8Bytes(next with { Revision = revision++ }))!);
    Check(afterAck.RootElement.GetProperty("commands").GetArrayLength() == 0, "acknowledged mutation not replayed");
    Check(writes.Queue(Command("update") with { BaseVersion = "stale" }) is not null, "stale desktop edit rejected before queuing");
    var update = Command("update");
    Check(writes.Queue(update) is null, "current-version edit queued");
    writes.Exchange(JsonSerializer.SerializeToUtf8Bytes(next with { Revision = revision++, Results = [new(update.Id,"conflict",0)] }));
    Check(writes.Writes().Last().Status == "conflict", "phone-side conflict surfaced without automatic overwrite");
    Check(writes.Queue(Command() with { CalendarId = "unauthorized" }) is not null, "unselected calendar cannot be written");
    writes.Exchange(JsonSerializer.SerializeToUtf8Bytes(next with { Revision = revision++, CanWrite = false }));
    Check(writes.Queue(Command()) is not null, "read-only phone cannot receive writes");
    writes.Exchange(JsonSerializer.SerializeToUtf8Bytes(next with { Revision = revision++, CanWrite = true }));
    var expiring = Command();
    writes.Queue(expiring);
    now += 121000;
    Check(writes.Writes().Last().Status == "expired", "offline pending operation visibly expires");
    Check(writes.Queue(Command()) is not null, "offline writes not silently queued");
    using var expired = JsonDocument.Parse(writes.Exchange(JsonSerializer.SerializeToUtf8Bytes(next with { Revision = revision++ }))!);
    Check(expired.RootElement.GetProperty("commands").GetArrayLength() == 0, "expired command not sent after reconnect");
}
Console.WriteLine($"Total with bidirectional checks: {assertions} assertions passed.");
