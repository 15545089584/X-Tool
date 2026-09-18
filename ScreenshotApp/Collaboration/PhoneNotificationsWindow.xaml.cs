using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ZXing;
using ZXing.Common;

namespace ScreenshotApp.Collaboration;

public partial class PhoneNotificationsWindow : Window
{
    private static PhoneNotificationsWindow? _current;
    private readonly PhoneNotificationHub _hub = PhoneNotificationHub.Instance;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, PhoneNotificationItem> _hidden = new();
    private bool _ready;
    private PhoneNotificationItem[] _displayed = [];
    public static void Open(Window? owner)
    {
        if (_current is not null) { _current.Activate(); return; }
        _current = new PhoneNotificationsWindow { Owner = owner };
        _current.Show();
    }
    public PhoneNotificationsWindow() : this(false) { }
    internal PhoneNotificationsWindow(bool preview)
    {
        InitializeComponent();
        if (preview) return;
        AlertsCheckBox.IsChecked = _hub.AlertsEnabled;
        PreviewCheckBox.IsChecked = ScreenshotApp.Settings.AppPreferences.Load().PhoneNotificationPreviewEnabled;
        _ready = true;
        PauseButton.Content = _hub.Paused ? "恢复接收" : "暂停接收";
        try { _hub.Start(); } catch { StatusText.Text = "通知服务未启动：端口被占用或已有通知接收实例。请退出旧实例后重试。"; }
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        SystemEvents.SessionSwitch += SessionSwitch;
        Closed += (_, _) => { _timer.Stop(); SystemEvents.SessionSwitch -= SessionSwitch; _current = null; QrImage.Source = null; };
        Refresh();
    }
    private void SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _hub.Locked = true;
        if (e.Reason == SessionSwitchReason.SessionUnlock) _hub.Locked = false;
        Dispatcher.BeginInvoke(() => { HidePair_Click(this, new RoutedEventArgs()); Refresh(); });
    }
    private void Refresh()
    {
        if (NotificationList is null || !_hub.Running) return;
        var last = _hub.State.LastReceivedUtc;
        StatusDot.Fill = new SolidColorBrush(!_hub.Paused && _hub.State.Enabled && DateTime.UtcNow - last < TimeSpan.FromSeconds(60)
            ? Color.FromRgb(32,185,154) : Color.FromRgb(173,186,202));
        StatusText.Text = _hub.Paused ? "已暂停接收；手机不会清除原通知。" : last == default ? "等待手机绑定并开启通知同步。" :
            $"{(_hub.State.Enabled ? "通知同步已启用" : "手机已暂停或未授权监听")} · 最近收到 {last.ToLocalTime():HH:mm:ss}" +
            (DateTime.UtcNow - last > TimeSpan.FromSeconds(60) ? " · 连接可能已中断" : "");
        var items = _hub.State.Items;
        foreach (var key in _hidden.Keys.Where(k => !items.Any(i => i.Key == k)).ToArray()) _hidden.Remove(key);
        var displayed = _hub.Locked ? Array.Empty<PhoneNotificationItem>() : items
            .Where(i => (!_hidden.TryGetValue(i.Key, out var hidden) || hidden != i) && i.App.Contains(FilterBox.Text, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.PostedAt).ToArray();
        CountText.Text = $"{displayed.Length} 条";
        EmptyPanel.Visibility = displayed.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_displayed.SequenceEqual(displayed)) { _displayed = displayed; NotificationList.ItemsSource = displayed; }
    }
    private void Pair_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var service = CollaborationService.Instance;
            var host = service.LocalIpAddress;
            if (string.IsNullOrWhiteSpace(host)) { StatusText.Text = "先在协作中心建立电脑网络连接。"; return; }
            var writer = new BarcodeWriterPixelData { Format = BarcodeFormat.QR_CODE, Options = new EncodingOptions { Width = 360, Height = 360, Margin = 1 } };
            var pixels = writer.Write(_hub.CollaborationPairingPayload(host, service.Port, service.Pin, service.ServerId));
            var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Pixels, pixels.Width * 4);
            bitmap.Freeze(); QrImage.Source = bitmap;
            AddressText.Text = $"加密通知通道：{host}:{PhoneNotificationHub.Port}";
            PairPanel.Visibility = Visibility.Visible;
        }
        catch { StatusText.Text = "无法打开通知绑定，请确认只有一个通知接收实例。"; }
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void HidePair_Click(object sender, RoutedEventArgs e) { PairPanel.Visibility = Visibility.Collapsed; QrImage.Source = null; }
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _hub.Paused = !_hub.Paused;
        if (_hub.Paused && Application.Current is App app) app.DismissPhoneAlertDisplays();
        PauseButton.Content = _hub.Paused ? "恢复接收" : "暂停接收";
        Refresh();
    }
    private void Clear_Click(object sender, RoutedEventArgs e) { foreach (var item in _hub.State.Items) _hidden[item.Key] = item; Refresh(); }
    private void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if (!_hub.Running || MessageBox.Show(this, "撤销后手机需重新扫描协作中心配对码以恢复通知。文件传输配对不受影响。", "撤销通知绑定", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _hub.Revoke(); _hidden.Clear();
        if (Application.Current is App app) app.DismissPhoneAlertDisplays();
        HidePair_Click(sender, e); Refresh();
    }
    private void Filter_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();
    private void Alerts_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _hub.AlertsEnabled = AlertsCheckBox.IsChecked == true;
        var preferences = ScreenshotApp.Settings.AppPreferences.Load();
        preferences.PhoneNotificationAlertsEnabled = _hub.AlertsEnabled; preferences.Save();
        if (!_hub.AlertsEnabled && Application.Current is App app) app.DismissPhoneAlertDisplays();
    }
    private void Preview_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var preferences = ScreenshotApp.Settings.AppPreferences.Load();
        preferences.PhoneNotificationPreviewEnabled = PreviewCheckBox.IsChecked == true; preferences.Save();
        if (!preferences.PhoneNotificationPreviewEnabled && Application.Current is App app) app.DismissPhoneAlertDisplays();
    }
}
