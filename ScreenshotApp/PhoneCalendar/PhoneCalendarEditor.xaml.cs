using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ScreenshotApp.PhoneCalendar;

public partial class PhoneCalendarEditor : Window
{
    private readonly CalendarSnapshot _snapshot;
    private readonly CalendarEntry? _entry;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly int[] _common = [0, 5, 15, 30, 60, 1440];
    private string? _commandId;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    public PhoneCalendarEditor(CalendarSnapshot snapshot, CalendarEntry? entry)
    {
        _snapshot = snapshot; _entry = entry;
        InitializeComponent();
        Heading.Text = entry is null ? "新建日程" : "编辑日程";
        Title = Caption.Text = Heading.Text + " · X-Tool";
        CalendarBox.ItemsSource = snapshot.Calendars?.Where(c=>c.CanWrite).ToArray() ?? [];
        CalendarBox.SelectedValue = entry?.CalendarId ?? snapshot.Calendars?.FirstOrDefault(c=>c.CanWrite)?.Id;
        CalendarBox.IsEnabled = entry is null;
        TitleBox.Text = entry?.Title ?? "";
        LocationBox.Text = entry?.Location ?? "";
        DescriptionBox.Text = entry?.Description ?? "";
        var start = entry is null ? DateTime.Now.AddHours(1) :
            entry.AllDay ? DateTimeOffset.FromUnixTimeMilliseconds(entry.Begin).UtcDateTime.Date :
            DateTimeOffset.FromUnixTimeMilliseconds(entry.Begin).LocalDateTime;
        var end = entry is null ? start.AddHours(1) :
            entry.AllDay ? DateTimeOffset.FromUnixTimeMilliseconds(entry.End).UtcDateTime.Date.AddDays(-1) :
            DateTimeOffset.FromUnixTimeMilliseconds(entry.End).LocalDateTime;
        StartDate.SelectedDate = start.Date; EndDate.SelectedDate = end.Date;
        StartTime.Text = start.ToString("HH:mm"); EndTime.Text = end.ToString("HH:mm");
        AllDayBox.IsChecked = entry?.AllDay == true;
        var selected = entry?.Reminders.Select(r=>r.Minutes).ToHashSet() ?? [5];
        foreach (var minutes in _common)
            ReminderChoices.Children.Add(new CheckBox { Content = minutes == 0 ? "开始时" : minutes < 60 ? $"前 {minutes} 分钟" :
                minutes == 60 ? "前 1 小时" : "前 1 天", Tag = minutes, IsChecked = selected.Contains(minutes), Margin = new Thickness(0,0,7,8), Style = (Style)FindResource("ReminderChip") });
        OtherReminders.Text = string.Join(", ", selected.Except(_common));
        if (entry is not null) {
            RepeatBox.IsEnabled = false;
            var match = RepeatBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == entry.Rrule);
            if (match is not null) RepeatBox.SelectedItem = match;
            else if (entry.Recurring) {
                var preserved = new ComboBoxItem { Content = "保留手机原有重复规则", Tag = entry.Rrule };
                RepeatBox.Items.Add(preserved); RepeatBox.SelectedItem = preserved;
            }
        }
        if (entry?.Recurring == true) {
            TimeFields.IsEnabled = false; AllDayBox.IsEnabled = false; RecurringHint.Visibility = Visibility.Visible;
        }
        AllDayChanged(this, new RoutedEventArgs());
        if (entry?.UnresolvedReminder == true) {
            SaveButton.IsEnabled = false; Feedback.Text = "包含无法解析的系统提醒，请先在手机上编辑，避免丢失原提醒设置。";
        }
        _timer.Tick += (_,_) => ObserveResult();
        _statusTimer.Tick += (_,_) => UpdateConnectionStatus();
        Loaded += (_,_) => {
            // 适应高缩放屏幕，正文滚动，底部操作始终可见。
            MaxHeight = SystemParameters.WorkArea.Height;
            Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32));
            UpdateConnectionStatus(); _statusTimer.Start();
        };
        Closed += (_,_) => { _timer.Stop(); _statusTimer.Stop(); };
    }
    private void AllDayChanged(object sender, RoutedEventArgs e)
    {
        if (StartTime is null || EndTime is null || TimeHint is null) return;
        var allDay = AllDayBox.IsChecked == true;
        StartTime.Visibility = EndTime.Visibility = allDay ? Visibility.Collapsed : Visibility.Visible;
        TimeHint.Text = allDay ? "全天结束日期包含所选当天；全天提醒按手机时区计算。" : "时间按电脑当前时区填写： " + TimeZoneInfo.Local.DisplayName;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CalendarBox.SelectedItem is not CalendarCatalog calendar) throw new ArgumentException("请选择可写的日历。");
            var allDay = AllDayBox.IsChecked == true;
            var first = StartDate.SelectedDate ?? throw new ArgumentException("请选择开始日期。");
            var last = EndDate.SelectedDate ?? throw new ArgumentException("请选择结束日期。");
            long begin, end;
            if (allDay) {
                begin = new DateTimeOffset(DateTime.SpecifyKind(first.Date, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                end = new DateTimeOffset(DateTime.SpecifyKind(last.Date.AddDays(1), DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            } else {
                if (!TimeOnly.TryParseExact(StartTime.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startTime) ||
                    !TimeOnly.TryParseExact(EndTime.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endTime))
                    throw new ArgumentException("请输入 24 小时制时间，例如 09:30。");
                var startLocal = first.Date.Add(startTime.ToTimeSpan()); var endLocal = last.Date.Add(endTime.ToTimeSpan());
                if (TimeZoneInfo.Local.IsInvalidTime(startLocal) || TimeZoneInfo.Local.IsInvalidTime(endLocal) ||
                    TimeZoneInfo.Local.IsAmbiguousTime(startLocal) || TimeZoneInfo.Local.IsAmbiguousTime(endLocal))
                    throw new ArgumentException("该时间处于夏令时切换区间，请在手机上确认时间。");
                begin = new DateTimeOffset(startLocal).ToUnixTimeMilliseconds(); end = new DateTimeOffset(endLocal).ToUnixTimeMilliseconds();
            }
            var reminders = ReminderChoices.Children.OfType<CheckBox>().Where(c=>c.IsChecked == true).Select(c=>(int)c.Tag).ToList();
            foreach (var part in OtherReminders.Text.Replace('，', ',').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
                if (!int.TryParse(part, out var minute)) throw new ArgumentException("其他提醒请填写逗号分隔的整数分钟。");
                reminders.Add(minute);
            }
            var command = new CalendarWriteCommand(Guid.NewGuid().ToString(), _snapshot.DeviceId, _entry is null ? "create" : "update",
                _entry?.EventId ?? 0, calendar.Id, _entry?.Version ?? "", TitleBox.Text.Trim(), LocationBox.Text.Trim(),
                DescriptionBox.Text, begin, end, allDay, reminders.Distinct().ToArray(),
                _entry?.Rrule ?? (RepeatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "", DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds());
            var error = PhoneCalendarService.Instance.Queue(command);
            if (error is not null) throw new ArgumentException(error);
            _commandId = command.Id;
            Fields.IsEnabled = false; SaveButton.IsEnabled = false; SaveText.Text = "等待手机确认";
            BackText.Text = "返回列表"; Feedback.Text = "已发送保存请求。请等待手机回执，关闭此窗口不会撤销已发送的修改。";
            _timer.Start();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { Feedback.Text = error.Message; }
    }
    private void ObserveResult()
    {
        var write = PhoneCalendarService.Instance.Writes().FirstOrDefault(w=>w.Command.Id == _commandId);
        if (write is null) { Feedback.Text = "修改状态已清除，请先核对手机。"; _timer.Stop(); return; }
        if (write.Status == "pending") {
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > write.Command.ExpiresAt)
                Feedback.Text = "等待超时，暂未确认结果；请先核对手机，避免重复创建。";
            return;
        }
        _timer.Stop();
        Feedback.Text = PhoneCalendarService.WriteStatus(write.Status);
        if (write.Status == "applied") { DialogResult = true; return; }
        BackText.Text = "关闭";
        Feedback.Text += "。请关闭后重新打开最新日程；本次没有自动覆盖手机修改。";
    }
    private void UpdateConnectionStatus()
    {
        var cache = PhoneCalendarService.Instance.Read();
        var online = cache.Snapshot?.DeviceId == _snapshot.DeviceId &&
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cache.ReceivedAt < 15000;
        ConnectionStatus.Text = online ? "手机已连接" : "等待手机连接";
        StatusDot.Fill = online ? System.Windows.Media.Brushes.MediumSeaGreen : System.Windows.Media.Brushes.SlateGray;
        FooterStatus.Text = online ? cache.Snapshot?.CanWrite == true ? "已允许电脑写入日历" : "请在手机允许日历写入" : "连接后才能保存到手机";
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
