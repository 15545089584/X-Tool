using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace ScreenshotApp.DesktopPet;

internal enum PetAlarmRepeatMode
{
    Once,
    Daily,
    Custom
}

internal sealed class PetAlarmItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "提醒";

    public int Hour { get; set; }

    public int Minute { get; set; }

    public PetAlarmRepeatMode RepeatMode { get; set; }

    public List<DayOfWeek> Weekdays { get; set; } = [];

    public List<int> AdvanceMinutes { get; set; } = [];

    public bool DeleteAfterDismiss { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime? OnceDate { get; set; }

    internal PetAlarmItem Clone() => new()
    {
        Id = Id,
        Name = Name,
        Hour = Hour,
        Minute = Minute,
        RepeatMode = RepeatMode,
        Weekdays = [.. Weekdays],
        AdvanceMinutes = [.. AdvanceMinutes],
        DeleteAfterDismiss = DeleteAfterDismiss,
        IsEnabled = IsEnabled,
        OnceDate = OnceDate
    };
}

internal sealed record PetAlarmTrigger(PetAlarmItem Alarm, DateTime Occurrence, int? AdvanceMinutes)
{
    internal bool IsDue => AdvanceMinutes is null;

    internal string Title => IsDue ? $"{Alarm.Name} · 时间到了" : $"{Alarm.Name} · 提前提醒";

    internal string Detail => IsDue
        ? $"设定时间 {Occurrence:HH:mm}，本提醒没有声音"
        : $"距离 {Occurrence:HH:mm} 还有 {FormatAdvance(AdvanceMinutes!.Value)}";

    private static string FormatAdvance(int minutes) => minutes >= 60 ? "1 小时" : $"{minutes} 分钟";
}

/// <summary>负责桌宠闹钟的持久化与运行期消息调度；不播放任何声音。</summary>
internal sealed class PetAlarmService : IDisposable
{
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromMinutes(2);
    private readonly object _syncRoot = new();
    private readonly string _storagePath;
    private readonly Dispatcher _dispatcher;
    private readonly System.Threading.Timer _timer;
    private readonly HashSet<string> _firedKeys = new(StringComparer.Ordinal);
    private List<PetAlarmItem> _alarms;
    private DateTime _lastScanAt;
    private bool _disposed;

    internal PetAlarmService()
    {
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "X-Tool",
            "DesktopPet",
            "alarms.json");
        _alarms = Load();
        _lastScanAt = DateTime.Now;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new System.Threading.Timer(Timer_Tick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ScheduleNextWakeUp();
    }

    internal event Action? ItemsChanged;

    internal event Action<PetAlarmTrigger>? ReminderTriggered;

    internal IReadOnlyList<PetAlarmItem> Items
    {
        get
        {
            lock (_syncRoot)
            {
                return _alarms.Select(item => item.Clone()).ToArray();
            }
        }
    }

    internal void Add(PetAlarmItem alarm)
    {
        alarm.Name = string.IsNullOrWhiteSpace(alarm.Name) ? "提醒" : alarm.Name.Trim();
        alarm.Hour = Math.Clamp(alarm.Hour, 0, 23);
        alarm.Minute = Math.Clamp(alarm.Minute, 0, 59);
        alarm.Weekdays = alarm.Weekdays.Distinct().OrderBy(day => day).ToList();
        alarm.AdvanceMinutes = alarm.AdvanceMinutes.Where(value => value is 5 or 15 or 30 or 60)
            .Distinct().OrderByDescending(value => value).ToList();
        if (alarm.RepeatMode == PetAlarmRepeatMode.Once)
        {
            var today = DateTime.Today.AddHours(alarm.Hour).AddMinutes(alarm.Minute);
            alarm.OnceDate = today > DateTime.Now ? today.Date : today.Date.AddDays(1);
        }

        lock (_syncRoot)
        {
            _alarms.Add(alarm.Clone());
            SaveLocked();
        }
        ItemsChanged?.Invoke();
        ScheduleNextWakeUp();
    }

    internal void SetEnabled(string id, bool enabled)
    {
        lock (_syncRoot)
        {
            var alarm = _alarms.FirstOrDefault(item => item.Id == id);
            if (alarm is null)
            {
                return;
            }

            alarm.IsEnabled = enabled;
            if (enabled && alarm.RepeatMode == PetAlarmRepeatMode.Once)
            {
                var today = DateTime.Today.AddHours(alarm.Hour).AddMinutes(alarm.Minute);
                if (alarm.OnceDate is null || alarm.OnceDate.Value.Date < DateTime.Today || today <= DateTime.Now)
                {
                    alarm.OnceDate = today > DateTime.Now ? today.Date : today.Date.AddDays(1);
                }
            }
            SaveLocked();
        }
        ItemsChanged?.Invoke();
        ScheduleNextWakeUp();
    }

    internal void Remove(string id)
    {
        lock (_syncRoot)
        {
            if (_alarms.RemoveAll(item => item.Id == id) == 0)
            {
                return;
            }
            SaveLocked();
        }
        ItemsChanged?.Invoke();
        ScheduleNextWakeUp();
    }

    internal void CompleteReminder(PetAlarmTrigger trigger)
    {
        if (!trigger.IsDue)
        {
            return;
        }

        lock (_syncRoot)
        {
            var alarm = _alarms.FirstOrDefault(item => item.Id == trigger.Alarm.Id);
            if (alarm is null)
            {
                return;
            }

            if (alarm.DeleteAfterDismiss)
            {
                _alarms.Remove(alarm);
            }
            else if (alarm.RepeatMode == PetAlarmRepeatMode.Once)
            {
                alarm.IsEnabled = false;
            }
            SaveLocked();
        }
        ItemsChanged?.Invoke();
        ScheduleNextWakeUp();
    }

    internal DateTime? GetNextOccurrence(PetAlarmItem alarm, DateTime from)
    {
        for (var offset = 0; offset <= 14; offset++)
        {
            var date = from.Date.AddDays(offset);
            if (!MatchesDate(alarm, date))
            {
                continue;
            }

            var occurrence = date.AddHours(alarm.Hour).AddMinutes(alarm.Minute);
            if (occurrence >= from)
            {
                return occurrence;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Dispose();
    }

    private void Timer_Tick(object? state)
    {
        var now = DateTime.Now;
        List<PetAlarmTrigger> triggers = [];
        lock (_syncRoot)
        {
            var scanFrom = _lastScanAt < now - CatchUpWindow ? now - CatchUpWindow : _lastScanAt;
            _lastScanAt = now;
            foreach (var alarm in _alarms.Where(item => item.IsEnabled))
            {
                for (var offset = -1; offset <= 1; offset++)
                {
                    var date = now.Date.AddDays(offset);
                    if (!MatchesDate(alarm, date))
                    {
                        continue;
                    }

                    var occurrence = date.AddHours(alarm.Hour).AddMinutes(alarm.Minute);
                    foreach (var advance in alarm.AdvanceMinutes.Cast<int?>().Append(null))
                    {
                        var fireAt = occurrence.AddMinutes(-(advance ?? 0));
                        if (fireAt <= scanFrom || fireAt > now)
                        {
                            continue;
                        }

                        var key = CreateFireKey(alarm.Id, occurrence, advance);
                        if (_firedKeys.Add(key))
                        {
                            triggers.Add(new PetAlarmTrigger(alarm.Clone(), occurrence, advance));
                        }
                    }
                }
            }
        }

        foreach (var trigger in triggers)
        {
            _dispatcher.BeginInvoke(() => ReminderTriggered?.Invoke(trigger), DispatcherPriority.Send);
        }
        ScheduleNextWakeUp();
    }

    /// <summary>只为最近的一个提醒设置单次计时器，避免固定轮询带来的数秒延迟。</summary>
    private void ScheduleNextWakeUp()
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTime.Now;
        DateTime? nextFireAt = null;
        lock (_syncRoot)
        {
            foreach (var alarm in _alarms.Where(item => item.IsEnabled))
            {
                for (var offset = -1; offset <= 14; offset++)
                {
                    var date = now.Date.AddDays(offset);
                    if (!MatchesDate(alarm, date))
                    {
                        continue;
                    }

                    var occurrence = date.AddHours(alarm.Hour).AddMinutes(alarm.Minute);
                    foreach (var advance in alarm.AdvanceMinutes.Cast<int?>().Append(null))
                    {
                        var fireAt = occurrence.AddMinutes(-(advance ?? 0));
                        if (_firedKeys.Contains(CreateFireKey(alarm.Id, occurrence, advance)))
                        {
                            continue;
                        }

                        if (fireAt <= now - CatchUpWindow)
                        {
                            continue;
                        }

                        var candidate = fireAt <= now ? now.AddMilliseconds(10) : fireAt;
                        if (nextFireAt is null || candidate < nextFireAt)
                        {
                            nextFireAt = candidate;
                        }
                    }
                }
            }
        }

        try
        {
            var dueTime = nextFireAt is null
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMilliseconds(Math.Max(1, (nextFireAt.Value - now).TotalMilliseconds));
            _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 应用退出与最后一次调度并发时无需继续唤醒。
        }
    }

    private static string CreateFireKey(string alarmId, DateTime occurrence, int? advanceMinutes) =>
        $"{alarmId}|{occurrence:O}|{advanceMinutes?.ToString() ?? "due"}";

    private static bool MatchesDate(PetAlarmItem alarm, DateTime date) => alarm.RepeatMode switch
    {
        PetAlarmRepeatMode.Once => alarm.OnceDate?.Date == date.Date,
        PetAlarmRepeatMode.Daily => true,
        PetAlarmRepeatMode.Custom => alarm.Weekdays.Contains(date.DayOfWeek),
        _ => false
    };

    private List<PetAlarmItem> Load()
    {
        try
        {
            if (!File.Exists(_storagePath))
            {
                return [];
            }

            var json = File.ReadAllText(_storagePath);
            return JsonSerializer.Deserialize<List<PetAlarmItem>>(json) ?? [];
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"桌宠闹钟数据读取失败：{exception.GetBaseException().Message}");
            return [];
        }
    }

    private void SaveLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_storagePath)!;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(_alarms, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"桌宠闹钟数据保存失败：{exception.GetBaseException().Message}");
        }
    }
}
