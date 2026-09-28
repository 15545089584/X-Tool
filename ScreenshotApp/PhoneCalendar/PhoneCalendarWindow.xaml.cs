using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScreenshotApp.Collaboration;

namespace ScreenshotApp.PhoneCalendar;

public partial class PhoneCalendarWindow : Window
{
    private static PhoneCalendarWindow? _current;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _category = "";
    private string _renderKey = "";
    private string _categoryKey = "";
    private long _refreshAt;
    public static void Open()
    {
        if (_current is not null) { _current.Show(); _current.WindowState = WindowState.Normal; _current.Activate(); return; }
        _current = new(); _current.Show();
    }
    public PhoneCalendarWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); _current = null; };
    }
    private void Refresh()
    {
        if (Items is null || FilterBox is null) return;
        var service = PhoneCalendarService.Instance;
        var cache = service.Read(); var snapshot = cache.Snapshot;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var online = snapshot is not null && now - cache.ReceivedAt < 15000;
        var locked = PhoneNotificationHub.Instance.Locked;
        PauseText.Text = cache.Paused ? "恢复提醒" : "暂停提醒";
        ConnectionText.Text = online ? "手机已连接" : "离线副本";
        ConnectionDot.Fill = new SolidColorBrush(online ? Color.FromRgb(24, 184, 142) : Color.FromRgb(169, 183, 200));
        Subtitle.Text = "来自手机系统日历 · " + (snapshot?.CanWrite == true ? "双向同步已开启" : "手机 → 电脑同步") + $" · {DateTime.Today:yyyy-MM-dd}";
        var entries = locked ? [] : snapshot?.Events ?? [];
        CountText.Text = entries.Count(e => Card.End(e) > DateTime.Now) + " 项未结束";
        StatusText.Text = snapshot is null ? "尚未同步，请在手机设置中开启并选择日历" :
            !snapshot.Enabled ? "手机已关闭同步或撤权，电脑副本已清空" :
            $"共 {entries.Length} 项 · 最近同步 {DateTimeOffset.FromUnixTimeMilliseconds(cache.ReceivedAt).ToLocalTime():HH:mm:ss}" +
            (online ? "" : " · 离线期间手机修改尚未同步") + (cache.Paused ? " · 提醒已暂停" : "") +
            "\n显示近期至未来 120 天；电脑休眠期间不补弹旧提醒";
        if (!string.IsNullOrEmpty(service.StorageError)) StatusText.Text += "\n" + service.StorageError;
        if (_refreshAt > 0 && cache.ReceivedAt <= _refreshAt) StatusText.Text = "已请求刷新，等待手机连接并返回最新日程…";
        else _refreshAt = 0;
        var writes = service.Writes();
        var last = writes.LastOrDefault();
        WriteStatusText.Visibility = last is null || locked ? Visibility.Collapsed : Visibility.Visible;
        if (last is not null) WriteStatusText.Text = PhoneCalendarService.WriteStatus(last.Status) + " · " + last.Command.Title;
        var categories = entries.GroupBy(e => e.CalendarId.Length == 0 ? e.Calendar : e.CalendarId).ToArray();
        var categoryKey = string.Join("|", categories.Select(c => $"{c.Key}:{c.Count()}:{c.First().Calendar}:{c.First().Color}")) + _category;
        if (_categoryKey != categoryKey)
        {
            _categoryKey = categoryKey; CategoryPanel.Children.Clear();
            AddCategory("", "全部", entries.Length, "#26364C");
            foreach (var group in categories) AddCategory(group.Key, FriendlyName(group.First().Calendar), group.Count(), group.First().Color);
        }
        var query = FilterBox.Text.Trim();
        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var key = string.Join("|", entries.Select(e => e.Id + e.Version + e.Color)) + _category + query + (now / 60000);
        if (key == _renderKey) return;
        _renderKey = key;
        var visible = entries.Where(e => (_category.Length == 0 || (e.CalendarId.Length == 0 ? e.Calendar : e.CalendarId) == _category) &&
            (e.Title + " " + e.Location + " " + e.Description + " " + FriendlyName(e.Calendar)).Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => Card.End(e) <= DateTime.Now ? 1 : 0).ThenBy(e => e.Begin).Select(e => new Card(e)).ToArray();
        Items.ItemsSource = visible;
        EmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = locked ? "电脑已锁定，日程内容已隐藏" :
            snapshot?.Enabled == true ? "当前筛选下没有日程" : "在手机设置 → 日程同步中选择日历";
    }
    private void AddCategory(string id, string name, int count, string color)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (id.Length > 0) content.Children.Add(new Ellipse { Width = 7, Height = 7,
            Fill = (Brush)new BrushConverter().ConvertFromString(color)!, Margin = new Thickness(0,0,7,0) });
        content.Children.Add(new TextBlock { Text = $"{name}  {count}", FontSize = 12,
            Foreground = id == _category ? Brushes.White : new SolidColorBrush(Color.FromRgb(91,111,139)) });
        var button = new Button { Content = content, Padding = new Thickness(12,7,12,7), Margin = new Thickness(0,0,7,6),
            Background = id == _category ? new SolidColorBrush(Color.FromRgb(38,54,76)) : new SolidColorBrush(Color.FromRgb(245,248,252)) };
        button.Click += (_,_) => { _category = id; Refresh(); };
        CategoryPanel.Children.Add(button);
    }
    public static string FriendlyName(string name) => name.ToLowerInvariant() switch {
        "local calendar" => "个人", "vivo work" => "工作", "birthday" => "生日", "vivo days matter" => "倒数日",
        "vivo anniversary" => "纪念日", "assistant" => "日程助手", _ => name
    };
    private void OpenEditor(CalendarEntry? entry)
    {
        var cache = PhoneCalendarService.Instance.Read();
        if (PhoneNotificationHub.Instance.Locked) return;
        if (cache.Snapshot?.CanWrite != true || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cache.ReceivedAt > 15000) {
            MessageBox.Show(this, "请连接手机，并在手机“设置 → 日程同步”中开启“允许电脑新建与编辑”。", "日程写入"); return;
        }
        if (entry is not null && (!entry.CanEdit || entry.Version.Length == 0)) {
            MessageBox.Show(this, "该日程暂不可编辑，请等待手机同步最新版本，或在手机上编辑。", "日程写入"); return;
        }
        new PhoneCalendarEditor(cache.Snapshot, entry) { Owner = this }.ShowDialog();
        Refresh();
    }
    private void FilterChanged(object sender, TextChangedEventArgs e) => Refresh();
    private void Edit_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: CalendarEntry entry }) OpenEditor(entry); }
    private void New_Click(object sender, RoutedEventArgs e) => OpenEditor(null);
    private void Refresh_Click(object sender, RoutedEventArgs e) { _refreshAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); PhoneCalendarService.Instance.RequestRefresh(); _renderKey = ""; Refresh(); }
    private void Pause_Click(object sender, RoutedEventArgs e) { var s = PhoneCalendarService.Instance; s.SetPaused(!s.Read().Paused); Refresh(); }
    private void Pin_Click(object sender, RoutedEventArgs e) { Topmost = !Topmost; PinIcon.Foreground = Topmost ? Brushes.DodgerBlue : Brushes.SlateGray; }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class Card
    {
        public CalendarEntry Entry { get; }
        public Card(CalendarEntry entry) { Entry = entry; }
        public static DateTime End(CalendarEntry e) => e.AllDay ? DateTimeOffset.FromUnixTimeMilliseconds(e.End).UtcDateTime.Date : DateTimeOffset.FromUnixTimeMilliseconds(e.End).LocalDateTime;
        private DateTime Start => Entry.AllDay ? DateTimeOffset.FromUnixTimeMilliseconds(Entry.Begin).UtcDateTime.Date : DateTimeOffset.FromUnixTimeMilliseconds(Entry.Begin).LocalDateTime;
        private bool Finished => End(Entry) <= DateTime.Now;
        private bool Soon => !Finished && Start > DateTime.Now && Start < DateTime.Now.AddHours(1);
        public string Accent => Entry.Color;
        public string Outline => Soon ? "#73DDBB" : "#DFE8F3";
        public string Surface => Finished ? "#F8FAFD" : "#FFFFFF";
        public double Opacity => Finished ? 0.8 : 1;
        public string Category => FriendlyName(Entry.Calendar);
        public string TimeText => Entry.AllDay ? $"{Start:MM-dd} · 全天" :
            (Start.Date == DateTime.Today ? $"{Start:HH:mm} — " : $"{Start:MM-dd HH:mm} — ") +
            (End(Entry).Date == Start.Date ? $"{End(Entry):HH:mm}" : $"{End(Entry):MM-dd HH:mm}");
        public string StateText => Finished ? "已结束" : Entry.AllDay && Start <= DateTime.Today ? "今天 · 全天" :
            Start <= DateTime.Now ? "进行中" : Soon ? $"即将开始 · {Math.Max(1,(int)Math.Ceiling((Start-DateTime.Now).TotalMinutes))} 分钟后" :
            Start.Date == DateTime.Today ? "今天" : $"{Math.Max(1,(Start.Date-DateTime.Today).Days)} 天后";
        public string StateInk => Soon ? "#16866B" : "#8193AC";
        public string StateSurface => Soon ? "#EAFBF5" : "#F0F4F9";
        public string LocationText => "地点：" + Entry.Location;
        public string DescriptionText => "备注：" + Entry.Description;
        public Visibility LocationVisibility => string.IsNullOrWhiteSpace(Entry.Location) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility DescriptionVisibility => string.IsNullOrWhiteSpace(Entry.Description) ? Visibility.Collapsed : Visibility.Visible;
        public string[] ReminderLabels => Entry.Reminders.OrderBy(r=>r.At).Select(r =>
            DateTimeOffset.FromUnixTimeMilliseconds(r.At).ToLocalTime().ToString(
                DateTimeOffset.FromUnixTimeMilliseconds(r.At).LocalDateTime.Date == Start.Date ? "HH:mm" : "MM-dd HH:mm") +
            (r.Minutes >= 0 ? r.Minutes == 0 ? "（开始时）" : r.Minutes < 60 ? $"（前 {r.Minutes} 分钟）" :
                r.Minutes % 1440 == 0 ? $"（前 {r.Minutes/1440} 天）" : r.Minutes % 60 == 0 ? $"（前 {r.Minutes/60} 小时）" : $"（前 {r.Minutes} 分钟）" : "（日内提醒）"))
            .Concat(Entry.UnresolvedReminder ? ["另有系统默认提醒"] : [])
            .DefaultIfEmpty("未设置通知提醒").ToArray();
    }
}
