using System.IO;

namespace ScreenshotApp.Collaboration;

public sealed record PhoneNotificationItem(string Key, string Package, string App, string Title, string Text, long PostedAt, string Avatar = "", string AvatarKind = "")
{
    public string TimeText => PostedAt is >= 0 and <= 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds(PostedAt).LocalDateTime.ToString("MM-dd HH:mm") : "";
}
public sealed record PhoneNotificationSnapshot(string DeviceId, string Epoch, long Sequence, bool Enabled, PhoneNotificationItem[] Items);

/// <summary>完整快照实现更新、移除与重连对账；正文仅保存在有界内存中。</summary>
public sealed class PhoneNotificationState
{
    private readonly object _gate = new();
    private string? _device, _epoch;
    private long _sequence = -1;
    private readonly HashSet<string> _retired = new();
    private PhoneNotificationItem[] _items = [];
    public DateTime LastReceivedUtc { get; private set; }
    public bool Enabled { get; private set; }
    public PhoneNotificationItem[] Items { get { lock (_gate) return _items.ToArray(); } }
    public bool Apply(PhoneNotificationSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.DeviceId) || snapshot.DeviceId.Length > 100 ||
            !Guid.TryParse(snapshot.Epoch, out _) || snapshot.Sequence < 0 || snapshot.Items is null || snapshot.Items.Length > 200)
            throw new InvalidDataException("通知快照格式无效");
        if (snapshot.Items.Any(i => i is null || string.IsNullOrEmpty(i.Key) || i.Key.Length > 512 ||
            i.Package is null || i.Package.Length > 200 || i.App is null || i.App.Length > 100 ||
            i.Title is null || i.Title.Length > 256 || i.Text is null || i.Text.Length > 2000) ||
            snapshot.Items.Select(i => i.Key).Distinct(StringComparer.Ordinal).Count() != snapshot.Items.Length)
            throw new InvalidDataException("通知条目格式无效");
        foreach (var item in snapshot.Items)
        {
            if (item.AvatarKind is not (null or "" or "sender" or "notification" or "app")) throw new InvalidDataException("通知图片来源无效");
            PhoneNotificationImage.Validate(item.Avatar);
        }
        lock (_gate)
        {
            if (_device is not null && _device != snapshot.DeviceId) throw new UnauthorizedAccessException();
            if (_retired.Contains(snapshot.Epoch)) throw new InvalidDataException("旧会话已结束");
            if (_epoch == snapshot.Epoch && snapshot.Sequence <= _sequence) return false;
            if (_epoch is not null && _epoch != snapshot.Epoch)
            {
                if (_retired.Count >= 256) throw new InvalidDataException("请重新绑定通知设备");
                _retired.Add(_epoch);
            }
            _device = snapshot.DeviceId; _epoch = snapshot.Epoch; _sequence = snapshot.Sequence;
            _items = snapshot.Enabled ? snapshot.Items.ToArray() : [];
            Enabled = snapshot.Enabled; LastReceivedUtc = DateTime.UtcNow;
            return true;
        }
    }
    public void Reset()
    {
        lock (_gate) { _device = _epoch = null; _sequence = -1; _retired.Clear(); _items = []; Enabled = false; LastReceivedUtc = default; }
    }
    public void Expire(DateTime nowUtc)
    {
        lock (_gate) { if (LastReceivedUtc != default && nowUtc - LastReceivedUtc > TimeSpan.FromMinutes(10)) _items = []; }
    }
}
