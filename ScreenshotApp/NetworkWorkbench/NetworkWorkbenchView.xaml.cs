using ScreenshotApp.SystemTools;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ScreenshotApp.NetworkWorkbench;

public partial class NetworkWorkbenchView : UserControl
{
    private readonly ObservableCollection<NetworkDiagnosticResult> _diagnostics = new();
    private readonly ObservableCollection<NetworkAdapterEntry> _adapters = new();
    private readonly ObservableCollection<NetworkConnectionRow> _connections = new();
    private readonly ObservableCollection<NetworkProfile> _profiles = new();
    private readonly ObservableCollection<NetworkTimelineEvent> _timelineEvents = new();
    private readonly Queue<double> _downloadHistory = new();
    private readonly Queue<double> _uploadHistory = new();
    private readonly List<double> _historicalDownload = new();
    private readonly List<double> _historicalUpload = new();
    private readonly NetworkHistoryStore _historyStore;
    private readonly NetworkMonitorCoordinator _monitorCoordinator;
    private readonly NetworkEtwTrafficClient _exactTrafficClient = new();
    private IReadOnlyList<NetworkConnectionRow> _allConnections = Array.Empty<NetworkConnectionRow>();
    private NetworkOverviewSnapshot? _previousOverview;
    private ProxySettingsSnapshot _proxySnapshot = new(false, string.Empty, string.Empty, string.Empty, true);
    private CancellationTokenSource? _diagnosticCancellation;
    private bool _initialized;
    private bool _refreshingConnections;
    private string _connectionSortKey = "LocalAddress";
    private bool _connectionsAscending = true;
    private bool _connectionHeaderSortActive;
    private string _activeTab = "Overview";
    private string _trafficSessionAdapterId = string.Empty;
    private double _sessionDownloadedBytes;
    private double _sessionUploadedBytes;
    private double _peakDownloadRate;
    private double _peakUploadRate;
    private double _downloadRateTotal;
    private double _uploadRateTotal;
    private int _trafficSampleCount;
    private int _connectionRefreshTicks;
    private readonly Dictionary<int, double> _processTrafficTotals = new();
    private DateTime _lastConnectionSampleAt = DateTime.Now;
    private NetworkProfile? _profileRestorePoint;
    private string _trafficRange = "Realtime";
    private bool _historyControlsInitialized;
    private int _historyRefreshTicks;
    private DateTime _lastAdaptersRefreshAt = DateTime.MinValue;
    private DateTime _lastConnectionsRefreshAt = DateTime.MinValue;

    public NetworkWorkbenchView()
    {
        _historyStore = new NetworkHistoryStore();
        _monitorCoordinator = new NetworkMonitorCoordinator(_historyStore);
        InitializeComponent();
        DiagnosticResultsListBox.ItemsSource = _diagnostics;
        AdaptersListBox.ItemsSource = _adapters;
        ConnectionsListBox.ItemsSource = _connections;
        ProfilesListBox.ItemsSource = _profiles;
        NetworkEventsListBox.ItemsSource = _timelineEvents;
        _monitorCoordinator.SampleAvailable += MonitorCoordinator_SampleAvailable;
        _monitorCoordinator.TimelineEventAvailable += MonitorCoordinator_TimelineEventAvailable;
        _monitorCoordinator.MonitorFailed += MonitorCoordinator_MonitorFailed;
        TrafficCanvas.SizeChanged += (_, _) => UpdateTrafficChart();
        Loaded += NetworkWorkbenchView_Loaded;
        Unloaded += NetworkWorkbenchView_Unloaded;
        SelectTab("Overview");
    }

    private void NetworkWorkbenchView_Loaded(object sender, RoutedEventArgs e)
    {
        _monitorCoordinator.AutoRefreshEnabled = AutoRefreshCheckBox.IsChecked == true;
        if (IsVisible) _monitorCoordinator.Start();
    }

    private async void NetworkWorkbenchView_Unloaded(object sender, RoutedEventArgs e)
    {
        await _monitorCoordinator.StopAsync();
        _exactTrafficClient.Dispose();
    }

    private async void NetworkWorkbenchView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            await _monitorCoordinator.StopAsync();
            return;
        }

        _monitorCoordinator.AutoRefreshEnabled = AutoRefreshCheckBox.IsChecked == true;
        _monitorCoordinator.Start();
        if (!_initialized)
        {
            _initialized = true;
            await Task.WhenAll(RefreshOverviewAsync(force: true), RefreshAdaptersAsync(), RefreshConnectionsAsync(), LoadProxyAsync(), LoadHistoryControlsAsync());
            ReloadProfiles();
        }
        else
        {
            await RefreshOverviewAsync(force: true);
        }
    }

    private async void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tab) return;
        SelectTab(tab);
        if (tab == "Overview") UpdateTrafficChart();
        if (tab == "Adapters" && DateTime.UtcNow - _lastAdaptersRefreshAt > TimeSpan.FromSeconds(5)) await RefreshAdaptersAsync();
        if (tab == "Connections")
        {
            // 用户主动切换到连接页后才按需请求 ETW 管理员授权，避免后台或启动时弹出 UAC。
            await EnsureExactTrafficEnabledAsync();
            if (DateTime.UtcNow - _lastConnectionsRefreshAt > TimeSpan.FromSeconds(3)) await RefreshConnectionsAsync();
        }
        if (tab == "Proxy") await LoadProxyAsync(updateSnapshot: false);
        if (tab == "Profiles") ReloadProfiles();
    }

    private void SelectTab(string tab)
    {
        _activeTab = tab;
        OverviewPanel.Visibility = tab == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = tab == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        ProxyPanel.Visibility = tab == "Proxy" ? Visibility.Visible : Visibility.Collapsed;
        AdaptersPanel.Visibility = tab == "Adapters" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPanel.Visibility = tab == "Connections" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPanel.Visibility = tab == "Profiles" ? Visibility.Visible : Visibility.Collapsed;

        foreach (var button in new[] { OverviewTabButton, DiagnosticsTabButton, ProxyTabButton, AdaptersTabButton, ConnectionsTabButton, ProfilesTabButton })
        {
            var active = string.Equals(button.Tag?.ToString(), tab, StringComparison.Ordinal);
            button.Background = BrushFrom(active ? "#4D7CFE" : "#86FFFFFF");
            button.Foreground = BrushFrom(active ? "#FFFFFF" : "#41607D");
            button.BorderBrush = BrushFrom(active ? "#4D7CFE" : "#8CC8E6F8");
        }
    }

    private async Task RefreshOverviewAsync(bool force = false)
    {
        if (!IsVisible || !force && AutoRefreshCheckBox.IsChecked != true && _previousOverview is not null) return;
        await _monitorCoordinator.RefreshNowAsync(force);
    }

    private void MonitorCoordinator_SampleAvailable(object? sender, NetworkMonitorSample sample)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (!IsVisible) return;
            ApplyOverviewSample(sample);
            if (_activeTab == "Connections" && ++_connectionRefreshTicks % 3 == 0) await RefreshConnectionsAsync();
            if (_trafficRange != "Realtime" && ++_historyRefreshTicks % 30 == 0) await RefreshTrafficHistoryAsync();
        });
    }

    private void ApplyOverviewSample(NetworkMonitorSample sample)
    {
        var snapshot = sample.Snapshot;
        var download = sample.DownloadRate;
        var upload = sample.UploadRate;
        var sessionAdapterKey = string.IsNullOrWhiteSpace(snapshot.ActiveAdapterId)
            ? snapshot.ActiveAdapterName
            : snapshot.ActiveAdapterId;
        if (!sample.SameAdapter || string.IsNullOrWhiteSpace(_trafficSessionAdapterId) ||
            !string.Equals(_trafficSessionAdapterId, sessionAdapterKey, StringComparison.OrdinalIgnoreCase))
        {
            _downloadHistory.Clear();
            _uploadHistory.Clear();
            ResetTrafficSession(sessionAdapterKey);
        }
        else
        {
            UpdateTrafficSession(sample.ReceivedDelta, sample.SentDelta, download, upload);
        }

        try
        {
            HeaderStatusText.Text = snapshot.ConnectivityText;
            HeaderStatusDot.Fill = BrushFrom(snapshot.IsInternetAvailable ? "#61C995" : snapshot.HasPhysicalConnection ? "#F0B15A" : "#EF7E83");
            OverviewNetworkText.Text = snapshot.ConnectivityText;
            OverviewAdapterText.Text = snapshot.ActivePhysicalAdapterCount > 1
                ? $"{snapshot.ActiveAdapterName} · {snapshot.ActivePhysicalAdapterCount} 条物理链路"
                : snapshot.ActiveAdapterName;
            OverviewAdapterText.ToolTip = snapshot.ConnectionDetail;
            OverviewIpv4Text.Text = snapshot.IPv4Address;
            OverviewGatewayText.Text = $"网关 {snapshot.Gateway}";
            OverviewDownloadText.Text = FormatByteRate(download);
            OverviewUploadText.Text = FormatByteRate(upload);
            OverviewLinkSpeedText.Text = FormatBitRate(snapshot.LinkSpeedBitsPerSecond);
            OverviewDetailGatewayText.Text = snapshot.Gateway;
            OverviewDnsText.Text = snapshot.DnsServers;
            OverviewDhcpText.Text = snapshot.DhcpText;
            OverviewActiveAdapterText.Text = $"{snapshot.ActiveAdapterName} · {snapshot.ActiveAdapterType}";
            OverviewWifiText.Text = snapshot.WifiSsid;
            OverviewWifiSignalText.Text = $"{snapshot.WifiSignal} / 信道 {snapshot.WifiChannel} · {snapshot.WifiRadioType}";
            OverviewWifiSignalText.ToolTip = $"BSSID {snapshot.WifiBssid}\n安全 {snapshot.WifiAuthentication}\n信道宽度 {snapshot.WifiChannelWidth}";
            OverviewProxyText.Text = snapshot.ProxyText;
            OverviewIpv6Text.Text = snapshot.IPv6Address;
            OverviewProbeText.Text = snapshot.ConnectivityProbeText;
            var probeAge = snapshot.ConnectivityProbeCapturedAt == DateTime.MinValue
                ? 0
                : Math.Max(0, (snapshot.CapturedAt - snapshot.ConnectivityProbeCapturedAt).TotalSeconds);
            OverviewProbeTimeText.Text = snapshot.ConnectivityProbeCapturedAt == DateTime.MinValue
                ? "尚未探测"
                : $"{snapshot.ConnectivityProbeCapturedAt:HH:mm:ss} · {probeAge:F0} 秒前";
            TrafficUpdatedText.Text = $"{snapshot.ActiveAdapterName} · 更新于 {snapshot.CapturedAt:HH:mm:ss}";
            if (_trafficRange == "Realtime") TrafficStatsText.Text = BuildTrafficStatsText();
            ProxyDnsText.Text = snapshot.DnsServers;

            AddHistory(_downloadHistory, download);
            AddHistory(_uploadHistory, upload);
            if (_trafficRange == "Realtime" && _activeTab == "Overview") UpdateTrafficChart();
            _previousOverview = snapshot;
        }
        catch (Exception exception)
        {
            HeaderStatusText.Text = $"读取失败：{exception.Message}";
            HeaderStatusDot.Fill = BrushFrom("#EF7E83");
        }
    }

    private void MonitorCoordinator_TimelineEventAvailable(object? sender, NetworkTimelineEvent entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _timelineEvents.Insert(0, entry);
            while (_timelineEvents.Count > 50) _timelineEvents.RemoveAt(_timelineEvents.Count - 1);
            HistoryStatusText.Visibility = Visibility.Collapsed;
        });
    }

    private void MonitorCoordinator_MonitorFailed(object? sender, Exception exception)
    {
        Dispatcher.BeginInvoke(() =>
        {
            HeaderStatusText.Text = $"监测失败：{exception.Message}";
            HeaderStatusDot.Fill = BrushFrom("#EF7E83");
        });
    }

    private static void AddHistory(Queue<double> history, double value)
    {
        history.Enqueue(double.IsFinite(value) && value >= 0 ? value : 0);
        while (history.Count > 60) history.Dequeue();
    }

    private void ResetTrafficSession(string adapterId)
    {
        _trafficSessionAdapterId = adapterId;
        _sessionDownloadedBytes = 0;
        _sessionUploadedBytes = 0;
        _peakDownloadRate = 0;
        _peakUploadRate = 0;
        _downloadRateTotal = 0;
        _uploadRateTotal = 0;
        _trafficSampleCount = 0;
    }

    private void UpdateTrafficSession(double receivedDelta, double sentDelta, double downloadRate, double uploadRate)
    {
        _sessionDownloadedBytes += receivedDelta;
        _sessionUploadedBytes += sentDelta;
        _peakDownloadRate = Math.Max(_peakDownloadRate, downloadRate);
        _peakUploadRate = Math.Max(_peakUploadRate, uploadRate);
        _downloadRateTotal += downloadRate;
        _uploadRateTotal += uploadRate;
        _trafficSampleCount++;
    }

    private string BuildTrafficStatsText()
    {
        if (string.IsNullOrWhiteSpace(_trafficSessionAdapterId)) return "当前无物理链路，流量采样已暂停";
        var averageDownload = _trafficSampleCount == 0 ? 0 : _downloadRateTotal / _trafficSampleCount;
        var averageUpload = _trafficSampleCount == 0 ? 0 : _uploadRateTotal / _trafficSampleCount;
        return $"平均 ↓ {FormatByteRate(averageDownload)}  ↑ {FormatByteRate(averageUpload)}    峰值 ↓ {FormatByteRate(_peakDownloadRate)}  ↑ {FormatByteRate(_peakUploadRate)}    会话 ↓ {FormatBytes(_sessionDownloadedBytes)}  ↑ {FormatBytes(_sessionUploadedBytes)}";
    }

    private async Task LoadHistoryControlsAsync()
    {
        if (_historyControlsInitialized) return;
        try
        {
            var retentionDays = await _monitorCoordinator.GetRetentionDaysAsync();
            RetentionButton.Tag = retentionDays;
            RetentionButton.Content = $"保留 {retentionDays} 天";
            var events = await _monitorCoordinator.GetTimelineEventsAsync();
            _timelineEvents.Clear();
            foreach (var entry in events) _timelineEvents.Add(entry);
            HistoryStatusText.Text = events.Count == 0 ? "尚无网络变化记录" : string.Empty;
            HistoryStatusText.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _historyControlsInitialized = true;
            UpdateTrafficRangeButtons();
        }
        catch (Exception exception)
        {
            HistoryStatusText.Text = $"历史记录不可用：{exception.Message}";
            HistoryStatusText.Visibility = Visibility.Visible;
        }
    }

    private void AutoRefreshOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_monitorCoordinator is null) return;
        _monitorCoordinator.AutoRefreshEnabled = AutoRefreshCheckBox.IsChecked == true;
        if (AutoRefreshCheckBox.IsChecked == true && IsVisible) _ = _monitorCoordinator.RefreshNowAsync();
    }

    private async void TrafficRange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string range) return;
        _trafficRange = range;
        UpdateTrafficRangeButtons();
        if (range == "Realtime")
        {
            TrafficAxisStartText.Text = "60 秒前";
            TrafficAxisCenterText.Text = "30 秒前";
            TrafficAxisEndText.Text = "现在";
            TrafficStatsText.Text = BuildTrafficStatsText();
            UpdateTrafficChart();
            return;
        }
        await RefreshTrafficHistoryAsync();
    }

    private void UpdateTrafficRangeButtons()
    {
        foreach (var button in new[] { RealtimeRangeButton, Minutes15RangeButton, Hour1RangeButton, Hours24RangeButton, Days7RangeButton, Days30RangeButton, Days90RangeButton })
        {
            var selected = string.Equals(button.Tag?.ToString(), _trafficRange, StringComparison.Ordinal);
            button.Background = BrushFrom(selected ? "#4D7CFE" : "#70FFFFFF");
            button.Foreground = BrushFrom(selected ? "#FFFFFF" : "#52708E");
            button.BorderBrush = BrushFrom(selected ? "#4D7CFE" : "#8CC8E6F8");
        }
    }

    private async Task RefreshTrafficHistoryAsync()
    {
        var range = _trafficRange switch
        {
            "15m" => TimeSpan.FromMinutes(15),
            "1h" => TimeSpan.FromHours(1),
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            "30d" => TimeSpan.FromDays(30),
            "90d" => TimeSpan.FromDays(90),
            _ => TimeSpan.FromMinutes(1)
        };
        try
        {
            var samples = await _monitorCoordinator.GetTrafficHistoryAsync(range);
            var grouped = AggregateTrafficHistory(samples, 60);
            _historicalDownload.Clear();
            _historicalUpload.Clear();
            _historicalDownload.AddRange(grouped.Select(item => item.DownloadRate));
            _historicalUpload.AddRange(grouped.Select(item => item.UploadRate));
            var totalDownload = samples.Sum(item => item.DownloadedBytes);
            var totalUpload = samples.Sum(item => item.UploadedBytes);
            var averageDownload = samples.Count == 0 ? 0 : samples.Average(item => item.AverageDownloadRate);
            var averageUpload = samples.Count == 0 ? 0 : samples.Average(item => item.AverageUploadRate);
            var peakDownload = samples.Count == 0 ? 0 : samples.Max(item => item.PeakDownloadRate);
            var peakUpload = samples.Count == 0 ? 0 : samples.Max(item => item.PeakUploadRate);
            TrafficStatsText.Text = samples.Count == 0
                ? "该时间范围尚无已落盘的分钟数据，持续监测后会自动出现"
                : $"平均 ↓ {FormatByteRate(averageDownload)}  ↑ {FormatByteRate(averageUpload)}    峰值 ↓ {FormatByteRate(peakDownload)}  ↑ {FormatByteRate(peakUpload)}    累计 ↓ {FormatBytes(totalDownload)}  ↑ {FormatBytes(totalUpload)}";
            UpdateHistoryAxisLabels(range);
            UpdateTrafficChart();
        }
        catch (Exception exception)
        {
            TrafficStatsText.Text = $"读取历史失败：{exception.Message}";
        }
    }

    private static IReadOnlyList<(double DownloadRate, double UploadRate)> AggregateTrafficHistory(IReadOnlyList<NetworkTrafficHistoryPoint> samples, int maximumPoints)
    {
        if (samples.Count == 0) return Array.Empty<(double, double)>();
        var groupSize = Math.Max(1, (int)Math.Ceiling(samples.Count / (double)maximumPoints));
        var result = new List<(double, double)>();
        for (var index = 0; index < samples.Count; index += groupSize)
        {
            var group = samples.Skip(index).Take(groupSize).ToArray();
            result.Add((group.Average(item => item.AverageDownloadRate), group.Average(item => item.AverageUploadRate)));
        }
        return result;
    }

    private void UpdateHistoryAxisLabels(TimeSpan range)
    {
        TrafficAxisStartText.Text = range.TotalDays >= 1 ? $"{range.TotalDays:F0} 天前" : range.TotalHours >= 1 ? $"{range.TotalHours:F0} 小时前" : $"{range.TotalMinutes:F0} 分钟前";
        var half = TimeSpan.FromTicks(range.Ticks / 2);
        TrafficAxisCenterText.Text = half.TotalDays >= 1 ? $"{half.TotalDays:F1} 天前" : half.TotalHours >= 1 ? $"{half.TotalHours:F1} 小时前" : $"{half.TotalMinutes:F0} 分钟前";
        TrafficAxisEndText.Text = "现在";
    }

    private async void RetentionButton_Click(object sender, RoutedEventArgs e)
    {
        var current = RetentionButton.Tag is int days ? days : 7;
        var next = current switch { 1 => 7, 7 => 30, 30 => 90, _ => 1 };
        RetentionButton.IsEnabled = false;
        try
        {
            await _monitorCoordinator.SetRetentionDaysAsync(next);
            RetentionButton.Tag = next;
            RetentionButton.Content = $"保留 {next} 天";
            HistoryStatusText.Text = $"已更新保留期，并清理 {next} 天以前的数据";
            HistoryStatusText.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            MessageBox.Show($"更新历史保留期失败：{exception.Message}", "网络历史", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RetentionButton.IsEnabled = true; }
    }

    private async void ClearNetworkHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定清除全部网络流量历史与网络事件吗？此操作不会修改系统网络配置。", "清除网络历史", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _monitorCoordinator.ClearHistoryAsync();
            _timelineEvents.Clear();
            _historicalDownload.Clear();
            _historicalUpload.Clear();
            HistoryStatusText.Text = "历史数据已清除；实时监测仍在继续";
            HistoryStatusText.Visibility = Visibility.Visible;
            if (_trafficRange != "Realtime") await RefreshTrafficHistoryAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"清除网络历史失败：{exception.Message}", "网络历史", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ExportNetworkHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var days = RetentionButton.Tag is int value ? value : 7;
            var samples = await _monitorCoordinator.GetTrafficHistoryAsync(TimeSpan.FromDays(days));
            var events = await _monitorCoordinator.GetTimelineEventsAsync(5000);
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = $"XTool_网络历史_{DateTime.Now:yyyyMMdd_HHmm}.csv" };
            if (dialog.ShowDialog() != true) return;
            var builder = new StringBuilder("类型,时间,网卡,下载字节,上传字节,平均下载Bps,平均上传Bps,事件,详情\r\n");
            foreach (var item in samples) builder.AppendLine($"流量,{item.BucketTime:O},\"{EscapeCsv(item.AdapterName)}\",{item.DownloadedBytes},{item.UploadedBytes},{item.AverageDownloadRate:F2},{item.AverageUploadRate:F2},,");
            foreach (var item in events) builder.AppendLine($"事件,{item.Time:O},\"{EscapeCsv(item.AdapterName)}\",,,,,\"{EscapeCsv(item.Title)}\",\"{EscapeCsv(item.Detail)}\"");
            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
            MessageBox.Show("网络历史已导出。", "导出历史", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, "导出历史失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void AlertSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = await _monitorCoordinator.GetAlertSettingsAsync();
            var window = new NetworkAlertSettingsWindow(settings) { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true && window.Result is not null)
            {
                await _monitorCoordinator.SetAlertSettingsAsync(window.Result);
                HistoryStatusText.Text = "告警阈值与每日流量预算已保存"; HistoryStatusText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, "保存告警设置失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string EscapeCsv(string value) => value.Replace("\"", "\"\"");

    private void UpdateTrafficChart()
    {
        var width = TrafficCanvas.ActualWidth;
        var height = TrafficCanvas.ActualHeight;
        if (width <= 1 || height <= 1) return;
        var downloadSource = _trafficRange == "Realtime" ? _downloadHistory.AsEnumerable() : _historicalDownload;
        var uploadSource = _trafficRange == "Realtime" ? _uploadHistory.AsEnumerable() : _historicalUpload;
        var max = Math.Max(1024d, downloadSource.Concat(uploadSource).Where(double.IsFinite).DefaultIfEmpty(0).Max());
        TrafficAxisMaxText.Text = FormatByteRate(max);
        TrafficAxisMidText.Text = FormatByteRate(max / 2d);
        var downloadPoints = BuildPoints(downloadSource, width, height, max);
        var uploadPoints = BuildPoints(uploadSource, width, height, max);
        TrafficCanvas.Children.Clear();
        DrawTrafficSeries(downloadPoints, BrushFrom("#4D7CFE"), 2.8);
        DrawTrafficSeries(uploadPoints, BrushFrom("#A55FEF"), 2.5);
        TrafficCanvas.InvalidateVisual();
    }

    private void DrawTrafficSeries(PointCollection points, Brush stroke, double thickness)
    {
        for (var index = 1; index < points.Count; index++)
        {
            TrafficCanvas.Children.Add(new Line
            {
                X1 = points[index - 1].X,
                Y1 = points[index - 1].Y,
                X2 = points[index].X,
                Y2 = points[index].Y,
                Stroke = stroke,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                SnapsToDevicePixels = true
            });
        }
    }

    private static PointCollection BuildPoints(IEnumerable<double> source, double width, double height, double max)
    {
        var values = source.ToArray();
        var points = new PointCollection();
        if (values.Length == 0) return points;
        if (values.Length == 1)
        {
            var y = height - Math.Min(height, values[0] / max * (height - 5)) - 2;
            points.Add(new Point(0, y));
            points.Add(new Point(width, y));
            return points;
        }
        for (var index = 0; index < values.Length; index++)
        {
            var x = index * width / (values.Length - 1);
            var y = height - Math.Min(height, values[index] / max * (height - 5)) - 2;
            points.Add(new Point(x, y));
        }
        return points;
    }

    private async Task RefreshAdaptersAsync()
    {
        var items = await Task.Run(NetworkWorkbenchService.GetAdapters);
        _adapters.Clear();
        foreach (var item in items) _adapters.Add(item);
        _lastAdaptersRefreshAt = DateTime.UtcNow;
    }

    private async void RefreshAdapters_Click(object sender, RoutedEventArgs e)
    {
        RefreshAdaptersButton.IsEnabled = false;
        RefreshAdaptersButton.Content = "刷新中…";
        AdapterRefreshStatusText.Text = "正在重新枚举网卡与网络状态";
        try
        {
            NetworkWorkbenchService.InvalidateNetworkCaches();
            await Task.WhenAll(RefreshAdaptersAsync(), RefreshOverviewAsync(force: true));
            AdapterRefreshStatusText.Text = $"已于 {DateTime.Now:HH:mm:ss} 完成刷新 · {_adapters.Count} 个接口";
        }
        catch (Exception exception)
        {
            AdapterRefreshStatusText.Text = $"刷新失败：{exception.Message}";
        }
        finally
        {
            RefreshAdaptersButton.Content = "刷新网卡";
            RefreshAdaptersButton.IsEnabled = true;
        }
    }

    private void OpenNetworkDeepTools_Click(object sender, RoutedEventArgs e)
    {
        new NetworkDeepToolsWindow { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private async Task RefreshConnectionsAsync()
    {
        if (_refreshingConnections) return;
        _refreshingConnections = true;
        try
        {
            var portsTask = Task.Run(SystemToolsService.GetPorts);
            var processesTask = Task.Run(SystemToolsService.GetProcesses);
            await Task.WhenAll(portsTask, processesTask);
            var trafficByProcess = processesTask.Result
                .GroupBy(item => item.ProcessId)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.NetworkBitsPerSecond));
            var now = DateTime.Now;
            var elapsedSeconds = Math.Clamp((now - _lastConnectionSampleAt).TotalSeconds, 0, 30);
            foreach (var (processId, bitsPerSecond) in trafficByProcess)
            {
                _processTrafficTotals[processId] = _processTrafficTotals.GetValueOrDefault(processId) + Math.Max(0, bitsPerSecond) / 8d * elapsedSeconds;
            }
            _lastConnectionSampleAt = now;
            _allConnections = portsTask.Result
                .Select(item =>
                {
                    var exact = _exactTrafficClient.GetMeasurement(item.ProcessId, item.Protocol, item.LocalAddress, item.RemoteAddress);
                    return new NetworkConnectionRow(item,
                        _exactTrafficClient.IsRunning ? exact.BitsPerSecond : trafficByProcess.GetValueOrDefault(item.ProcessId),
                        _exactTrafficClient.IsRunning ? exact.TotalBytes : _processTrafficTotals.GetValueOrDefault(item.ProcessId),
                        _exactTrafficClient.IsRunning);
                })
                .ToArray();
            ApplyConnectionFilter();
            _lastConnectionsRefreshAt = DateTime.UtcNow;
        }
        finally
        {
            _refreshingConnections = false;
        }
    }

    private void ApplyConnectionFilter()
    {
        var keyword = ConnectionFilterTextBox?.Text.Trim() ?? string.Empty;
        var activeOnly = ActiveConnectionsOnlyCheckBox?.IsChecked == true;
        var showListening = ShowListeningConnectionsCheckBox?.IsChecked != false;
        var showIpv6 = ShowIpv6ConnectionsCheckBox?.IsChecked != false;
        IEnumerable<NetworkConnectionRow> filtered = _allConnections.Where(item =>
                (!activeOnly || item.IsActiveConnection) &&
                (activeOnly || showListening || !item.IsListening) &&
                (showIpv6 || !item.IsIpv6) &&
                (string.IsNullOrWhiteSpace(keyword) ||
                item.Protocol.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.LocalAddress.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.RemoteAddress.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.ProcessName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.ProcessId.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.TrafficText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.State.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        filtered = _connectionSortKey switch
        {
            "Protocol" => Sort(filtered, item => item.Protocol, _connectionsAscending),
            "RemoteAddress" => Sort(filtered, item => item.RemoteAddress, _connectionsAscending),
            "Process" => Sort(filtered, item => item.ProcessName, _connectionsAscending),
            "ProcessId" => Sort(filtered, item => item.ProcessId, _connectionsAscending),
            "Traffic" => Sort(filtered, item => item.TrafficBitsPerSecond, _connectionsAscending),
            "TotalTraffic" => Sort(filtered, item => item.TotalTrafficBytes, _connectionsAscending),
            "State" => Sort(filtered, item => item.State, _connectionsAscending),
            _ => Sort(filtered, item => item.LocalAddress, _connectionsAscending)
        };
        _connections.Clear();
        foreach (var item in filtered) _connections.Add(item);
        ConnectionsSummaryText.Text = $"显示 {_connections.Count} / {_allConnections.Count} 项";
        if (ExactTrafficStatusText is not null)
        {
            ExactTrafficStatusText.Text = _exactTrafficClient.IsRunning
                ? "ETW 逐连接精确数据"
                : $"{_exactTrafficClient.StatusText} · 系统 I/O 估算（非逐连接）";
        }
    }

    private void ConnectionFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ConnectionsListBox is not null) ApplyConnectionFilter();
    }

    private async void RefreshConnections_Click(object sender, RoutedEventArgs e) => await RefreshConnectionsAsync();

    private async Task EnsureExactTrafficEnabledAsync()
    {
        if (_exactTrafficClient.IsRunning) return;
        ExactTrafficStatusText.Text = "正在请求管理员授权以启用 ETW 精确监测…";
        var result = await _exactTrafficClient.StartAsync();
        ExactTrafficStatusText.Text = result.Message;
    }

    private void ConnectionDisplayOption_Changed(object sender, RoutedEventArgs e)
    {
        if (ConnectionsListBox is not null) ApplyConnectionFilter();
    }

    private void ConnectionColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        _connectionsAscending = string.Equals(_connectionSortKey, key, StringComparison.OrdinalIgnoreCase)
            ? !_connectionsAscending
            : key != "Traffic";
        _connectionSortKey = key;
        _connectionHeaderSortActive = true;
        UpdateConnectionHeaderIndicators();
        ApplyConnectionFilter();
    }

    private void UpdateConnectionHeaderIndicators()
    {
        foreach (var (button, key, title) in new[]
                 {
                     (ProtocolSortHeader, "Protocol", "协议"),
                     (LocalAddressSortHeader, "LocalAddress", "本地端点"),
                     (RemoteAddressSortHeader, "RemoteAddress", "远程端点"),
                     (ProcessSortHeader, "Process", "进程"),
                     (PidSortHeader, "ProcessId", "PID"),
                     (TrafficSortHeader, "Traffic", "实时速率"),
                     (TotalTrafficSortHeader, "TotalTraffic", "会话累计"),
                     (StateSortHeader, "State", "状态")
                 })
        {
            button.Content = title + (_connectionHeaderSortActive && string.Equals(_connectionSortKey, key, StringComparison.OrdinalIgnoreCase)
                ? (_connectionsAscending ? " ↑" : " ↓")
                : string.Empty);
        }
    }

    private async void RunDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string kind) return;
        if (_diagnosticCancellation is not null)
        {
            MessageBox.Show("已有诊断任务正在执行，请先取消或等待完成。", "网络诊断", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var target = DiagnosticTargetTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show("请输入域名、IP 或 HTTP 地址。", "网络诊断", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (kind != "HTTP" && Uri.TryCreate(target, UriKind.Absolute, out var uri)) target = uri.Host;
        var port = int.TryParse(DiagnosticPortTextBox.Text, out var parsedPort) && parsedPort is > 0 and <= 65535 ? parsedPort : 443;
        _diagnosticCancellation = new CancellationTokenSource();
        CancelDiagnosticButton.IsEnabled = true;
        NetworkDiagnosticResult? pendingResult = null;
        if (kind == "Full")
        {
            button.IsEnabled = false;
            button.Content = "诊断中…";
            pendingResult = new NetworkDiagnosticResult(DateTime.Now, "完整诊断", target, true, "正在分层检查默认路由、DNS、TCP、HTTP 与当前代理…", 0);
            _diagnostics.Insert(0, pendingResult);
        }
        try
        {
            if (kind == "Full")
            {
                var results = await NetworkWorkbenchService.RunFullDiagnosticAsync(target, port, _diagnosticCancellation.Token);
                foreach (var result in results.Reverse()) _diagnostics.Insert(0, result);
            }
            else
            {
                var result = await NetworkWorkbenchService.RunDiagnosticAsync(kind, target, port, _diagnosticCancellation.Token);
                _diagnostics.Insert(0, result);
            }
        }
        catch (OperationCanceledException)
        {
            _diagnostics.Insert(0, new NetworkDiagnosticResult(DateTime.Now, kind == "Full" ? "完整诊断" : kind, target, false, "已取消", 0));
        }
        catch (Exception exception)
        {
            _diagnostics.Insert(0, new NetworkDiagnosticResult(DateTime.Now, kind == "Full" ? "完整诊断" : kind, target, false, exception.Message, 0));
        }
        finally
        {
            if (pendingResult is not null) _diagnostics.Remove(pendingResult);
            if (kind == "Full")
            {
                button.Content = "完整诊断";
                button.IsEnabled = true;
            }
            _diagnosticCancellation.Dispose();
            _diagnosticCancellation = null;
            CancelDiagnosticButton.IsEnabled = false;
        }
    }

    private void CancelDiagnostic_Click(object sender, RoutedEventArgs e) => _diagnosticCancellation?.Cancel();
    private void ClearDiagnostics_Click(object sender, RoutedEventArgs e) => _diagnostics.Clear();

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnostics.Count == 0)
        {
            MessageBox.Show("当前没有可导出的诊断记录。", "导出诊断报告", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "文本报告 (*.txt)|*.txt", FileName = $"X-Tool-网络诊断-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, NetworkWorkbenchService.BuildDiagnosticReport(_diagnostics), new UTF8Encoding(false));
        MessageBox.Show("诊断报告已导出。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportConnections_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = $"X-Tool-网络连接-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog() != true) return;
        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        var lines = new List<string> { "协议,本地端点,远程端点,进程,PID,进程估算速率,会话估算累计,状态" };
        lines.AddRange(_connections.Select(item => string.Join(",", new[] { Csv(item.Protocol), Csv(item.LocalAddress), Csv(item.RemoteAddress), Csv(item.ProcessName), item.ProcessId.ToString(), Csv(item.TrafficText), Csv(item.TotalTrafficText), Csv(item.State) })));
        File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
    }

    private async Task LoadProxyAsync(bool updateSnapshot = true)
    {
        var proxy = await Task.Run(NetworkWorkbenchService.GetCurrentUserProxy);
        var winHttp = await Task.Run(NetworkWorkbenchService.GetWinHttpProxyText);
        if (updateSnapshot) _proxySnapshot = proxy;
        WriteProxyEditor(proxy);
        WinHttpProxyText.Text = string.IsNullOrWhiteSpace(winHttp) ? "未读取到 WinHTTP 代理信息" : winHttp.Trim();
        var adapter = (await Task.Run(NetworkWorkbenchService.GetAdapters)).FirstOrDefault(item => item.IsPrimary);
        var ipv4Dns = (adapter?.DnsServers ?? string.Empty).Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => IPAddress.TryParse(value, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Take(2).ToArray();
        PrimaryDnsTextBox.Text = ipv4Dns.ElementAtOrDefault(0) ?? string.Empty;
        SecondaryDnsTextBox.Text = ipv4Dns.ElementAtOrDefault(1) ?? string.Empty;
        ProxyChangeHintText.Text = updateSnapshot ? "已保留当前页面快照；保存后的修改可在本次页面中恢复。" : "已重新读取当前用户代理设置。";
    }

    private ProxySettingsSnapshot ReadProxyEditor() => new(
        ProxyEnabledCheckBox.IsChecked == true,
        ProxyServerTextBox.Text.Trim(),
        ProxyBypassTextBox.Text.Trim(),
        ProxyPacTextBox.Text.Trim(),
        ProxyAutoDetectCheckBox.IsChecked == true);

    private void WriteProxyEditor(ProxySettingsSnapshot proxy)
    {
        ProxyEnabledCheckBox.IsChecked = proxy.Enabled;
        ProxyServerTextBox.Text = proxy.Server;
        ProxyBypassTextBox.Text = proxy.BypassList;
        ProxyPacTextBox.Text = proxy.PacUrl;
        ProxyAutoDetectCheckBox.IsChecked = proxy.AutoDetect;
    }

    private async void ReloadProxy_Click(object sender, RoutedEventArgs e)
    {
        await LoadProxyAsync(updateSnapshot: false);
        await RefreshOverviewAsync(force: true);
    }

    private async void SaveProxy_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定保存当前用户代理设置吗？这会立即影响遵循 Windows 代理的应用。", "保存代理", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var settings = ReadProxyEditor();
        if (!NetworkWorkbenchService.TrySaveCurrentUserProxy(settings, out var error))
        {
            MessageBox.Show(error ?? "保存失败。", "保存代理失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        ProxyChangeHintText.Text = $"已于 {DateTime.Now:HH:mm:ss} 保存并广播代理变更。";
        await RefreshOverviewAsync(force: true);
    }

    private async void RestoreProxy_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("恢复到进入本页面时保留的代理快照吗？", "恢复代理", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!NetworkWorkbenchService.TrySaveCurrentUserProxy(_proxySnapshot, out var error))
        {
            MessageBox.Show(error ?? "恢复失败。", "恢复代理失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        WriteProxyEditor(_proxySnapshot);
        ProxyChangeHintText.Text = "已恢复页面快照并广播代理变更。";
        await RefreshOverviewAsync(force: true);
    }

    private async void TestProxy_Click(object sender, RoutedEventArgs e)
    {
        var result = await NetworkWorkbenchService.TestCurrentProxyAsync(CancellationToken.None);
        _diagnostics.Insert(0, result);
        MessageBox.Show(result.Detail, result.Succeeded ? "代理检测成功" : "代理检测失败", MessageBoxButton.OK,
            result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void TestDns_Click(object sender, RoutedEventArgs e)
    {
        var result = await NetworkWorkbenchService.RunDiagnosticAsync("DNS", "www.microsoft.com", 443, CancellationToken.None);
        _diagnostics.Insert(0, result);
        MessageBox.Show(result.Detail, result.Succeeded ? "DNS 检测成功" : "DNS 检测失败", MessageBoxButton.OK,
            result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void SyncWinHttpProxy_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("将当前用户代理同步到机器级 WinHTTP 吗？这会影响使用 WinHTTP 的系统组件，并请求管理员授权。", "同步 WinHTTP", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var error = await Task.Run(() => SystemToolsService.TrySyncWinHttpProxyWithElevation(false, out var value) ? null : value);
        if (error is not null) MessageBox.Show(error, "同步 WinHTTP 失败", MessageBoxButton.OK, MessageBoxImage.Error);
        await LoadProxyAsync(updateSnapshot: false);
    }

    private async void ResetWinHttpProxy_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("清除机器级 WinHTTP 代理并恢复直连吗？这会请求管理员授权。", "清除 WinHTTP", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var error = await Task.Run(() => SystemToolsService.TrySyncWinHttpProxyWithElevation(true, out var value) ? null : value);
        if (error is not null) MessageBox.Show(error, "清除 WinHTTP 失败", MessageBoxButton.OK, MessageBoxImage.Error);
        await LoadProxyAsync(updateSnapshot: false);
    }

    private async void SaveDns_Click(object sender, RoutedEventArgs e)
    {
        var adapter = NetworkWorkbenchService.GetAdapters().FirstOrDefault(item => item.IsPrimary);
        if (adapter is null) { MessageBox.Show("当前没有可配置的主用物理网卡。", "保存 DNS", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var primary = PrimaryDnsTextBox.Text.Trim();
        var secondary = SecondaryDnsTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(primary)) { MessageBox.Show("请输入主 DNS，或使用“自动获取”。", "保存 DNS", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (MessageBox.Show($"将网卡“{adapter.Name}”的 IPv4 DNS 设置为 {primary}{(string.IsNullOrWhiteSpace(secondary) ? string.Empty : $" / {secondary}")} 吗？", "保存 DNS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await ApplyDnsAsync(adapter.Name, primary, secondary, "保存 DNS");
    }

    private async void ResetDns_Click(object sender, RoutedEventArgs e)
    {
        var adapter = NetworkWorkbenchService.GetAdapters().FirstOrDefault(item => item.IsPrimary);
        if (adapter is null) { MessageBox.Show("当前没有可配置的主用物理网卡。", "自动 DNS", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show($"让网卡“{adapter.Name}”恢复自动获取 IPv4 DNS 吗？", "自动 DNS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await ApplyDnsAsync(adapter.Name, string.Empty, string.Empty, "恢复自动 DNS");
    }

    private async Task ApplyDnsAsync(string adapterName, string primary, string secondary, string title)
    {
        var error = await Task.Run(() => SystemToolsService.TrySetNetworkAdapterDnsWithElevation(adapterName, primary, secondary, out var value) ? null : value);
        if (error is not null) MessageBox.Show(error, $"{title}失败", MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show($"{title}已完成。", title, MessageBoxButton.OK, MessageBoxImage.Information);
        NetworkWorkbenchService.InvalidateNetworkCaches();
        await Task.WhenAll(LoadProxyAsync(updateSnapshot: false), RefreshOverviewAsync(force: true), RefreshAdaptersAsync());
    }

    private NetworkAdapterEntry? GetAdapterFromMenu(object sender)
    {
        if (sender is not MenuItem item) return null;
        if (item.DataContext is NetworkAdapterEntry adapter) return adapter;
        var menu = ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu;
        return (menu?.PlacementTarget as FrameworkElement)?.DataContext as NetworkAdapterEntry;
    }

    private async void EnableAdapter_Click(object sender, RoutedEventArgs e) => await SetAdapterStateAsync(GetAdapterFromMenu(sender), true);
    private async void DisableAdapter_Click(object sender, RoutedEventArgs e) => await SetAdapterStateAsync(GetAdapterFromMenu(sender), false);

    private async Task SetAdapterStateAsync(NetworkAdapterEntry? adapter, bool enabled)
    {
        if (adapter is null) return;
        if (!enabled && adapter.IsPrimary && MessageBox.Show("这是当前主用物理网卡，禁用后网络会立即断开。仍要继续吗？", "禁用主用网卡", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (enabled && adapter.IsUp || !enabled && !adapter.IsUp) { MessageBox.Show($"网卡当前已经{(enabled ? "启用" : "禁用")}。", "网卡管理", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var error = await Task.Run(() => SystemToolsService.TrySetNetworkAdapterStateWithElevation(adapter.Name, enabled, out var value) ? null : value);
        if (error is not null) MessageBox.Show(error, "网卡操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        await RefreshNetworkStateAfterActionAsync();
    }

    private async void RenewAdapterDhcp_Click(object sender, RoutedEventArgs e)
    {
        var adapter = GetAdapterFromMenu(sender); if (adapter is null) return;
        if (MessageBox.Show($"释放并重新获取网卡“{adapter.Name}”的 DHCP 地址吗？网络会短暂中断。", "续租 DHCP", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var error = await Task.Run(() => SystemToolsService.TryRenewNetworkAdapterDhcpWithElevation(adapter.Name, out var value) ? null : value);
        if (error is not null) MessageBox.Show(error, "续租 DHCP 失败", MessageBoxButton.OK, MessageBoxImage.Error);
        await RefreshNetworkStateAfterActionAsync();
    }

    private async void ResetAdapterDns_Click(object sender, RoutedEventArgs e)
    {
        var adapter = GetAdapterFromMenu(sender); if (adapter is null) return;
        if (MessageBox.Show($"让网卡“{adapter.Name}”恢复自动获取 IPv4 DNS 吗？", "恢复自动 DNS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await ApplyDnsAsync(adapter.Name, string.Empty, string.Empty, "恢复自动 DNS");
    }

    private async Task RefreshNetworkStateAfterActionAsync()
    {
        NetworkWorkbenchService.InvalidateNetworkCaches();
        await Task.Delay(500);
        await Task.WhenAll(RefreshAdaptersAsync(), RefreshOverviewAsync(force: true));
    }

    private void ReloadProfiles()
    {
        _profiles.Clear();
        foreach (var profile in NetworkWorkbenchService.LoadProfiles().OrderByDescending(item => item.UpdatedAt)) _profiles.Add(profile);
        ProfilesSummaryText.Text = $"{_profiles.Count} 个方案";
        _profileRestorePoint = NetworkWorkbenchService.LoadProfileRestorePoint();
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("请输入方案名称。", "保存网络方案", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var existing = _profiles.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) _profiles.Remove(existing);
        _profiles.Insert(0, NetworkWorkbenchService.CaptureProfile(name, ReadProxyEditor()));
        NetworkWorkbenchService.SaveProfiles(_profiles);
        ProfilesSummaryText.Text = $"{_profiles.Count} 个方案";
    }

    private void ReloadProfiles_Click(object sender, RoutedEventArgs e) => ReloadProfiles();

    private async void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NetworkProfile profile) return;
        var applyAdapterFields = ApplyProfileDnsCheckBox.IsChecked == true || ApplyProfileAddressCheckBox.IsChecked == true || ApplyProfileMtuCheckBox.IsChecked == true;
        if (applyAdapterFields && profile.Adapter is not null && !NetworkWorkbenchService.GetAdapters().Any(item =>
                item.Id.Equals(profile.Adapter.Id, StringComparison.OrdinalIgnoreCase) ||
                item.Name.Equals(profile.Adapter.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"方案绑定的网卡“{profile.Adapter.Name}”当前不存在。为避免误改其他接口，已阻止应用。", "网卡不匹配", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var difference = NetworkWorkbenchService.BuildProfileDifference(profile);
        if (MessageBox.Show($"应用网络方案“{profile.Name}”吗？\n\n将发生以下变化：\n{difference}\n\n应用前会创建本次恢复点；DNS 修改可能请求管理员授权。", "应用网络方案", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _profileRestorePoint = NetworkWorkbenchService.CaptureProfile("应用前恢复点", NetworkWorkbenchService.GetCurrentUserProxy());
        NetworkWorkbenchService.SaveProfileRestorePoint(_profileRestorePoint, TimeSpan.FromHours(24));
        string? error = null;
        if (ApplyProfileProxyCheckBox.IsChecked == true && !NetworkWorkbenchService.TrySaveCurrentUserProxy(profile.Proxy, out error))
        {
            MessageBox.Show(error ?? "应用失败。", "应用网络方案失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (profile.Adapter is not null && ApplyProfileDnsCheckBox.IsChecked == true)
        {
            var dns = GetIpv4Dns(profile.Adapter.DnsServers);
            error = await Task.Run(() => SystemToolsService.TrySetNetworkAdapterDnsWithElevation(profile.Adapter.Name, dns.ElementAtOrDefault(0) ?? string.Empty, dns.ElementAtOrDefault(1) ?? string.Empty, out var value) ? null : value);
            if (error is not null)
            {
                NetworkWorkbenchService.TrySaveCurrentUserProxy(_profileRestorePoint.Proxy, out _);
                MessageBox.Show($"DNS 应用失败，已回退当前用户代理：\n{error}", "应用网络方案失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        if (profile.Adapter is not null && ApplyProfileAddressCheckBox.IsChecked == true)
        {
            var useDhcp = profile.Adapter.DhcpText.Contains("自动", StringComparison.Ordinal);
            error = await Task.Run(() => SystemToolsService.TrySetNetworkAdapterAddressWithElevation(profile.Adapter.Name, useDhcp,
                profile.Adapter.IPv4Address, profile.Adapter.IPv4PrefixLength, profile.Adapter.Gateway, out var value) ? null : value);
            if (error is not null) { MessageBox.Show($"IPv4 地址应用失败：\n{error}", "应用网络方案失败", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        }
        if (profile.Adapter is not null && ApplyProfileMtuCheckBox.IsChecked == true && profile.Adapter.Mtu > 0)
        {
            error = await Task.Run(() => SystemToolsService.TrySetNetworkAdapterMtuWithElevation(profile.Adapter.Name, profile.Adapter.Mtu, out var value) ? null : value);
            if (error is not null) { MessageBox.Show($"MTU 应用失败：\n{error}", "应用网络方案失败", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            if (profile.Adapter.InterfaceMetric > 0)
            {
                error = await Task.Run(() => SystemToolsService.TrySetNetworkAdapterMetricWithElevation(profile.Adapter.Name, profile.Adapter.InterfaceMetric, out var value) ? null : value);
                if (error is not null) { MessageBox.Show($"接口跃点应用失败：\n{error}", "应用网络方案失败", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            }
        }
        if (ApplyProfileProxyCheckBox.IsChecked == true) WriteProxyEditor(profile.Proxy);
        ProxyChangeHintText.Text = $"已应用网络方案“{profile.Name}”，可在网络方案页恢复应用前配置。";
        NetworkWorkbenchService.InvalidateNetworkCaches();
        await RefreshOverviewAsync(force: true);
        await Task.Delay(650);
        var verification = await NetworkWorkbenchService.GetOverviewAsync();
        if (!verification.IsInternetAvailable && _profileRestorePoint is not null &&
            MessageBox.Show($"方案已写入，但联网复核未通过：\n{verification.ConnectivityProbeText}\n\n是否立即回滚代理与 DNS？", "应用后复核失败", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            NetworkWorkbenchService.TrySaveCurrentUserProxy(_profileRestorePoint.Proxy, out _);
            if (_profileRestorePoint.Adapter is not null)
            {
                var restoreDns = GetIpv4Dns(_profileRestorePoint.Adapter.DnsServers);
                await Task.Run(() => SystemToolsService.TrySetNetworkAdapterDnsWithElevation(_profileRestorePoint.Adapter.Name,
                    restoreDns.ElementAtOrDefault(0) ?? string.Empty, restoreDns.ElementAtOrDefault(1) ?? string.Empty, out _));
            }
            NetworkWorkbenchService.InvalidateNetworkCaches();
            await Task.WhenAll(LoadProxyAsync(updateSnapshot: false), RefreshOverviewAsync(force: true), RefreshAdaptersAsync());
        }
    }

    private async void RestoreProfileSnapshot_Click(object sender, RoutedEventArgs e)
    {
        _profileRestorePoint ??= NetworkWorkbenchService.LoadProfileRestorePoint();
        if (_profileRestorePoint is null)
        {
            MessageBox.Show("本次运行尚未应用网络方案，没有可恢复的应用前配置。", "恢复网络配置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show("恢复最近一次应用网络方案前的代理与 DNS 配置吗？", "恢复网络配置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!NetworkWorkbenchService.TrySaveCurrentUserProxy(_profileRestorePoint.Proxy, out var error))
        {
            MessageBox.Show(error ?? "恢复代理失败。", "恢复网络配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_profileRestorePoint.Adapter is not null)
        {
            var dns = GetIpv4Dns(_profileRestorePoint.Adapter.DnsServers);
            error = await Task.Run(() => SystemToolsService.TrySetNetworkAdapterDnsWithElevation(_profileRestorePoint.Adapter.Name, dns.ElementAtOrDefault(0) ?? string.Empty, dns.ElementAtOrDefault(1) ?? string.Empty, out var value) ? null : value);
        }
        if (error is not null) MessageBox.Show(error, "恢复 DNS 失败", MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show("已恢复应用前的代理与 DNS 配置。", "恢复完成", MessageBoxButton.OK, MessageBoxImage.Information);
        NetworkWorkbenchService.InvalidateNetworkCaches();
        await Task.WhenAll(LoadProxyAsync(updateSnapshot: false), RefreshOverviewAsync(force: true), RefreshAdaptersAsync());
    }

    private void ImportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "X-Tool 网络方案|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var imported = NetworkWorkbenchService.ImportProfiles(dialog.FileName);
            foreach (var profile in imported)
            {
                var existing = _profiles.FirstOrDefault(item => item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null) _profiles.Remove(existing);
                _profiles.Add(profile);
            }
            NetworkWorkbenchService.SaveProfiles(_profiles); ProfilesSummaryText.Text = $"{_profiles.Count} 个方案";
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, "导入方案失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ExportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "X-Tool 网络方案|*.json", FileName = $"XTool_网络方案_{DateTime.Now:yyyyMMdd}.json" };
        if (dialog.ShowDialog() != true) return;
        try { NetworkWorkbenchService.ExportProfiles(dialog.FileName, _profiles); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "导出方案失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string[] GetIpv4Dns(string dnsText) => dnsText
        .Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(value => IPAddress.TryParse(value, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        .Take(2).ToArray();

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NetworkProfile profile) return;
        if (MessageBox.Show($"删除网络方案“{profile.Name}”吗？", "删除网络方案", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _profiles.Remove(profile);
        NetworkWorkbenchService.SaveProfiles(_profiles);
        ProfilesSummaryText.Text = $"{_profiles.Count} 个方案";
    }

    private static Brush BrushFrom(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;

    private static string FormatByteRate(double bytes)
    {
        if (bytes >= 1024 * 1024 * 1024) return $"{bytes / 1024 / 1024 / 1024:F1} GB/s";
        if (bytes >= 1024 * 1024) return $"{bytes / 1024 / 1024:F1} MB/s";
        if (bytes >= 1024) return $"{bytes / 1024:F1} KB/s";
        return $"{bytes:F0} B/s";
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes >= 1024 * 1024 * 1024) return $"{bytes / 1024 / 1024 / 1024:F2} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / 1024 / 1024:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024:F1} KB";
        return $"{bytes:F0} B";
    }

    private static string FormatBitRate(long bits)
    {
        if (bits >= 1_000_000_000) return $"{bits / 1_000_000_000d:F1} Gbps";
        if (bits >= 1_000_000) return $"{bits / 1_000_000d:F0} Mbps";
        if (bits >= 1_000) return $"{bits / 1_000d:F0} Kbps";
        return bits <= 0 ? "—" : $"{bits} bps";
    }

    private static IEnumerable<T> Sort<T, TKey>(IEnumerable<T> values, Func<T, TKey> selector, bool ascending)
        => ascending ? values.OrderBy(selector) : values.OrderByDescending(selector);

    /// <summary>把端点记录与同 PID 的实时流量采样合并，端口表本身不提供逐连接字节计数。</summary>
    private sealed record NetworkConnectionRow(PortEntry Entry, double TrafficBitsPerSecond, double TotalTrafficBytes, bool IsExact)
    {
        public string Protocol => Entry.Protocol;
        public string LocalAddress => Entry.LocalAddress;
        public string RemoteAddress => Entry.RemoteAddress;
        public string State => Entry.State;
        public int ProcessId => Entry.ProcessId;
        public string ProcessName => Entry.ProcessName;
        public bool IsIpv6 => Entry.IsIpv6;
        public bool IsListening => State.Contains("LISTEN", StringComparison.OrdinalIgnoreCase) || State.Contains("监听", StringComparison.OrdinalIgnoreCase);
        public bool IsActiveConnection => !IsListening && !string.IsNullOrWhiteSpace(RemoteAddress) && RemoteAddress is not "0.0.0.0:0" and not "[::]:0" and not "*:*";
        public string TrafficText => (IsExact ? "精确 " : "估算 ") + (TrafficBitsPerSecond switch
        {
            >= 1_000_000_000 => $"{TrafficBitsPerSecond / 1_000_000_000d:F2} Gbps",
            >= 1_000_000 => $"{TrafficBitsPerSecond / 1_000_000d:F2} Mbps",
            >= 1_000 => $"{TrafficBitsPerSecond / 1_000d:F1} Kbps",
            > 0 => $"{TrafficBitsPerSecond:F0} bps",
            _ => "0 bps"
        });
        public string TotalTrafficText => FormatBytes(TotalTrafficBytes);
    }
}
