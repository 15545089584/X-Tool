using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenshotApp.PhoneCalendar;

public sealed record CalendarWriteCommand(string Id, string DeviceId, string Kind, long EventId,
    string CalendarId, string BaseVersion, string Title, string Location, string Description,
    long Begin, long End, bool AllDay, int[] ReminderMinutes, string Rrule, long ExpiresAt);
public sealed record CalendarWriteState(CalendarWriteCommand Command, string Status, long EventId = 0);

public sealed partial class PhoneCalendarService
{
    private readonly List<CalendarWriteState> _writes = [];
    private bool _refreshRequested;
    public void RequestRefresh() { lock (_gate) _refreshRequested = true; }
    private string CommandPath => Path.Combine(Path.GetDirectoryName(_path)!, "commands.bin");
    public CalendarWriteState[] Writes() { lock (_gate) return _writes.Select(w => w.Status == "pending" && w.Command.ExpiresAt < Now() ? w with { Status = "expired" } : w).ToArray(); }
    private void LoadCommands()
    {
        try
        {
            if (!File.Exists(CommandPath)) return;
            var data = File.ReadAllBytes(CommandPath);
            if (data.Length > 1024 * 1024) return;
            var states = JsonSerializer.Deserialize<CalendarWriteState[]>(ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser), _json);
            if (states is null || states.Length > 100) return;
            _writes.Clear();
            _writes.AddRange(states.Where(s => s.Command is not null && Guid.TryParse(s.Command.Id, out _)));
        }
        catch { StorageError = "待发送修改无法读取，请核对手机是否已保存"; }
    }
    private bool SaveCommands()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CommandPath)!);
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_writes, _json), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(CommandPath + ".tmp", bytes); File.Move(CommandPath + ".tmp", CommandPath, true);
            return true;
        }
        catch { StorageError = "修改记录未保存，未加入发送队列"; return false; }
    }
    public string? Queue(CalendarWriteCommand command)
    {
        lock (_gate)
        {
            var s = _cache.Snapshot;
            if (s is null || !s.Enabled || !s.CanWrite || Now() - _cache.ReceivedAt > 15000 ||
                s.DeviceId != command.DeviceId) return "手机未连接或未开启写入授权，请在手机日程同步设置中开启。";
            if (s.Calendars?.Any(c => c.Id == command.CalendarId && c.CanWrite) != true) return "该日历未授权写入。";
            if (!Guid.TryParse(command.Id, out _) || command.Title.Trim().Length is < 1 or > 300 ||
                command.Location.Length > 300 || command.Description.Length > 2000 ||
                command.Begin < 946684800000 || command.End <= command.Begin || command.End > 4102444800000 ||
                command.ReminderMinutes.Length > 10 || command.ReminderMinutes.Any(m => m is < -10080 or > 525600 or -1) ||
                command.ExpiresAt <= Now() || command.ExpiresAt > Now() + 300000)
                return "请检查标题、起止时间和提醒设置。";
            if (command.Kind == "update")
            {
                if (!s.Events.Any(e => e.EventId == command.EventId && e.CalendarId == command.CalendarId &&
                    e.Version == command.BaseVersion && e.CanEdit)) return "手机内容已变化，请关闭编辑框后重新打开。";
            }
            else if (command.Kind != "create" || command.Rrule is not ("" or "FREQ=DAILY" or "FREQ=WEEKLY" or "FREQ=MONTHLY" or "FREQ=YEARLY"))
                return "不支持的操作或重复规则。";
            if (_writes.Count(w => w.Status == "pending" && w.Command.ExpiresAt > Now()) >= 10) return "仍有修改等待手机处理，请稍候。";
            while (_writes.Count >= 60) {
                var prune = _writes.FindIndex(w => w.Status != "pending" || w.Command.ExpiresAt < Now());
                if (prune < 0) return "仍有修改等待确认，请稍后再试。";
                _writes.RemoveAt(prune);
            }
            _writes.Add(new(command, "pending"));
            if (!SaveCommands()) { _writes.RemoveAt(_writes.Count - 1); return StorageError; }
            return null;
        }
    }
    public byte[]? Exchange(byte[] body)
    {
        // 与旧版本的快照接收器共用全部校验和保存规则。
        if (!Receive(body)) return null;
        var incoming = JsonSerializer.Deserialize<CalendarSnapshot>(body, _json)!;
        lock (_gate)
        {
            if (_cache.Snapshot?.DeviceId != incoming.DeviceId || _cache.Snapshot.Revision != incoming.Revision)
                return JsonSerializer.SerializeToUtf8Bytes(new { commands = Array.Empty<CalendarWriteCommand>() }, _json);
            var dirty = false;
            for (var i = 0; i < _writes.Count; i++)
            {
                var state = _writes[i];
                if (state.Status != "pending") continue;
                var result = incoming.DeviceId == state.Command.DeviceId
                    ? incoming.Results?.FirstOrDefault(r => r.Id == state.Command.Id) : null;
                if (result is not null) { _writes[i] = state with { Status = result.Status, EventId = result.EventId }; dirty = true; }
                else if (state.Command.ExpiresAt < Now()) { _writes[i] = state with { Status = "expired" }; dirty = true; }
                else if (incoming.DeviceId == state.Command.DeviceId && (!incoming.Enabled || !incoming.CanWrite))
                { _writes[i] = state with { Status = "forbidden" }; dirty = true; }
            }
            if (dirty && !SaveCommands()) return null;
            var commands = incoming.Enabled && incoming.CanWrite
                ? _writes.Where(w => w.Status == "pending" && w.Command.DeviceId == incoming.DeviceId && w.Command.ExpiresAt > Now())
                    .Take(10).Select(w => w.Command).ToArray() : [];
            var refresh = _refreshRequested; _refreshRequested = false;
            return JsonSerializer.SerializeToUtf8Bytes(new { commands, refresh }, _json);
        }
    }
    public static string WriteStatus(string status) => status switch {
        "pending" => "等待手机保存…", "applied" => "手机已保存", "conflict" => "手机已修改，请重新打开编辑",
        "forbidden" => "手机未允许写入", "expired" => "未及时收到回执，请先核对手机", _ => "保存未完成，请核对手机后重试"
    };
}
