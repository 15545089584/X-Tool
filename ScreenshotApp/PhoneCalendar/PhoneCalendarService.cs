using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ScreenshotApp.Collaboration;

namespace ScreenshotApp.PhoneCalendar;

public sealed record CalendarReminder(int Minutes, long At);
public sealed record CalendarEntry(string Id, string Calendar, string Title, string Location,
    long Begin, long End, bool AllDay, bool UnresolvedReminder, CalendarReminder[] Reminders, long EventId = 0, string CalendarId = "",
    string Description = "", string Color = "#1999FF", string Version = "", bool Recurring = false,
    string Rrule = "", bool CanEdit = false)
{
    public string When => AllDay
        ? DateTimeOffset.FromUnixTimeMilliseconds(Begin).UtcDateTime.ToString("yyyy-MM-dd") + " · 全天"
        : DateTimeOffset.FromUnixTimeMilliseconds(Begin).ToLocalTime().ToString("yyyy-MM-dd HH:mm") +
            " — " + DateTimeOffset.FromUnixTimeMilliseconds(End).ToLocalTime().ToString("MM-dd HH:mm");
    public string ReminderText => Reminders.Length == 0
        ? (UnresolvedReminder ? "系统默认提醒未公开准确时间，请以手机为准" : "未设置弹出提醒")
        : "提醒：" + string.Join("、", Reminders.OrderBy(r => r.At).Select(r =>
            DateTimeOffset.FromUnixTimeMilliseconds(r.At).ToLocalTime().ToString("MM-dd HH:mm"))) +
            (UnresolvedReminder ? " · 另有系统默认提醒未解析" : "");
}
public sealed record CalendarSnapshot(int Schema, string DeviceId, long Revision, bool Enabled,
    string Zone, long RangeStart, long RangeEnd, CalendarEntry[] Events,
    bool CanWrite = false, CalendarCatalog[]? Calendars = null, CalendarWriteResult[]? Results = null);
public sealed record CalendarCatalog(string Id, string Name, string Color, bool CanWrite);
public sealed record CalendarWriteResult(string Id, string Status, long EventId);
public sealed record CalendarCache(CalendarSnapshot? Snapshot, long ReceivedAt, bool Paused, Dictionary<string, long> Fired);

/// <summary>完整快照替换、加密缓存和本机调度；不写手机、不从通知正文推测日程。</summary>
public sealed partial class PhoneCalendarService : IDisposable
{
    public static PhoneCalendarService Instance { get; } = new();
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _path;
    private readonly Func<long> _clock;
    public PhoneCalendarService(string? storageRoot = null, Func<long>? clock = null)
    {
        _path = Path.Combine(storageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "PhoneCalendar"), "calendar.bin");
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
    private CalendarCache _cache = new(null, 0, false, new());
    private System.Threading.Timer? _timer;
    private long _lastTick = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string StorageError { get; private set; } = "";
    public event Action<CalendarEntry[]>? Due;
    public CalendarCache Read() { lock (_gate) return _cache; }
    public void Start(bool runTimer = true)
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            try
            {
                if (File.Exists(_path))
                {
                    var bytes = File.ReadAllBytes(_path);
                    if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException();
                    var cache = JsonSerializer.Deserialize<CalendarCache>(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser), _json);
                    if (cache is not null && cache.Fired is not null && cache.Fired.Count <= 40000 &&
                        (cache.Snapshot is null || Valid(cache.Snapshot, false))) _cache = cache;
                }
            }
            catch { StorageError = "本地日程副本无法读取，等待手机重新同步"; }
            LoadCommands();
            _lastTick = Now();
            if (runTimer) _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
        }
    }
    public bool Receive(byte[] bytes)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<CalendarSnapshot>(bytes, _json);
            if (snapshot is null || !Valid(snapshot, true)) return false;
            lock (_gate)
            {
                var prior = _cache.Snapshot;
                if (prior?.DeviceId == snapshot.DeviceId && snapshot.Revision <= prior.Revision) return true;
                var now = Now();
                var fired = new Dictionary<string, long>(_cache.Fired.Where(p => p.Value > now - 172800000).ToDictionary(p => p.Key, p => p.Value));
                // 保留已排定但刚到点、尚待 Tick 发出的提醒；首次补入的过去提醒不追发。
                var scheduled = prior is null ? new HashSet<string>() : prior.Events
                    .SelectMany(e => e.Reminders.Select(r => Key(prior, e, r))).ToHashSet();
                foreach (var item in snapshot.Events)
                    foreach (var reminder in item.Reminders.Where(r => r.At <= now))
                        if (!scheduled.Contains(Key(snapshot, item, reminder)))
                            fired.TryAdd(Key(snapshot, item, reminder), reminder.At);
                var next = new CalendarCache(snapshot, now, _cache.Paused, fired);
                if (!Save(next)) return false;
                _cache = next;
                return true;
            }
        }
        catch { return false; }
    }
    private long Now() => _clock();
    private static string Key(CalendarSnapshot snapshot, CalendarEntry entry, CalendarReminder reminder)
        => snapshot.DeviceId + "/" + entry.Id + "/" + reminder.At;
    public bool SetPaused(bool paused)
    {
        lock (_gate)
        {
            var next = _cache with { Paused = paused };
            if (!Save(next)) return false;
            _cache = next; _lastTick = Now(); return true;
        }
    }
    public void Clear()
    {
        lock (_gate)
        {
            // 撤销配对即停止当前运行中的提醒；磁盘错误不能保留活动提醒。
            _cache = new(null, 0, _cache.Paused, new());
            Save(_cache);
            _writes.Clear(); SaveCommands();
        }
    }
    private bool Save(CalendarCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(cache, _json), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_path + ".tmp", encrypted);
            File.Move(_path + ".tmp", _path, true);
            StorageError = ""; return true;
        }
        catch { StorageError = "日程本地保存失败，本轮提醒已暂缓"; return false; }
    }
    private bool Valid(CalendarSnapshot s, bool live)
    {
        const long earliest = 946684800000;
        const long latest = 4102444800000;
        if (s.Schema is not (1 or 2) || string.IsNullOrWhiteSpace(s.DeviceId) || s.DeviceId.Length > 100 ||
            s.Zone is null || s.Zone.Length > 100 || s.Revision <= 0 || s.Events is null || s.Events.Length > 1000 ||
            (!s.Enabled && s.Events.Length != 0) || s.RangeStart < earliest || s.RangeEnd > latest ||
            s.RangeEnd <= s.RangeStart || s.RangeEnd - s.RangeStart > 123L * 86400000) return false;
        if (live && (s.RangeEnd < Now() || Math.Abs(s.RangeStart - Now()) > 3L * 86400000)) return false;
        if ((s.Calendars?.Length ?? 0) > 100 || (s.Results?.Length ?? 0) > 256) return false;
        if (s.Calendars?.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 40 ||
            c.Name is null || c.Name.Length > 160 || !ValidColor(c.Color)) == true) return false;
        if (s.Results?.Any(r => r is null || !Guid.TryParse(r.Id, out _) ||
            r.Status is not ("applied" or "conflict" or "forbidden" or "failed")) == true) return false;
        var ids = new HashSet<string>();
        foreach (var e in s.Events)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.Id) || e.Id.Length > 160 || !ids.Add(e.Id) ||
                e.Title is null || e.Title.Length > 300 || e.Calendar is null || e.Calendar.Length > 160 ||
                e.Location is null || e.Location.Length > 300 || e.Description is null || e.Description.Length > 2000 ||
                e.CalendarId is null || e.CalendarId.Length > 40 || e.Version is null || e.Version.Length > 64 ||
                e.Rrule is null || e.Rrule.Length > 500 || !ValidColor(e.Color) || e.Begin < earliest || e.End > latest || e.End < e.Begin ||
                e.End < s.RangeStart || e.Begin > s.RangeEnd || e.Reminders is null || e.Reminders.Length > 32) return false;
            if (e.Reminders.Any(r => r is null || r.At < earliest || r.At > latest || r.Minutes is < -10080 or > 525600 ||
                Math.Abs(r.At - e.Begin) > 367L * 86400000)) return false;
        }
        return true;
    }
    private static bool ValidColor(string? color) => color is not null &&
        System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$");

    internal void Tick()
    {
        CalendarEntry[] due = [];
        try
        {
            lock (_gate)
            {
                var now = Now();
                var from = Math.Max(_lastTick, now - 60000);
                _lastTick = now;
                var s = _cache.Snapshot;
                if (s is null || !s.Enabled || now > s.RangeEnd) return;
                var fired = new Dictionary<string, long>(_cache.Fired);
                var list = new List<CalendarEntry>();
                foreach (var e in s.Events)
                {
                    var any = false;
                    foreach (var r in e.Reminders.Where(r => r.At > from && r.At <= now))
                        if (fired.TryAdd(Key(s, e, r), r.At)) any = true;
                    if (any) list.Add(e);
                }
                if (list.Count == 0) return;
                var next = _cache with { Fired = fired };
                if (!Save(next)) return;
                _cache = next;
                // 锁屏、暂停及休眠超过一分钟的提醒不补弹，日程仍可在列表查看。
                if (!_cache.Paused && !PhoneNotificationHub.Instance.Locked) due = list.ToArray();
            }
            if (due.Length > 0) Due?.Invoke(due);
        }
        catch { /* 后台提醒失败不能结束桌面应用，也不输出日程正文。 */ }
    }
    public void Dispose() { _timer?.Dispose(); _timer = null; }
}
