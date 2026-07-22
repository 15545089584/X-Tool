using ScreenshotApp.SystemTools;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScreenshotApp.NetworkWorkbench;

public partial class NetworkWorkbenchView : UserControl
{
    private readonly ObservableCollection<NetworkDiagnosticResult> _diagnostics = new();
    private readonly ObservableCollection<NetworkAdapterEntry> _adapters = new();
    private readonly ObservableCollection<NetworkConnectionRow> _connections = new();
    private readonly ObservableCollection<NetworkProfile> _profiles = new();
    private readonly Queue<double> _downloadHistory = new();
    private readonly Queue<double> _uploadHistory = new();
    private readonly DispatcherTimer _overviewTimer;
    private IReadOnlyList<NetworkConnectionRow> _allConnections = Array.Empty<NetworkConnectionRow>();
    private NetworkOverviewSnapshot? _previousOverview;
    private ProxySettingsSnapshot _proxySnapshot = new(false, string.Empty, string.Empty, string.Empty, true);
    private CancellationTokenSource? _diagnosticCancellation;
    private bool _initialized;
    private readonly SemaphoreSlim _overviewRefreshGate = new(1, 1);
    private bool _refreshingConnections;
    private string _connectionSortKey = "LocalAddress";
    private bool _connectionsAscending = true;
    private bool _connectionHeaderSortActive;
    private string _activeTab = "Overview";

    public NetworkWorkbenchView()
    {
        InitializeComponent();
        DiagnosticResultsListBox.ItemsSource = _diagnostics;
        AdaptersListBox.ItemsSource = _adapters;
        ConnectionsListBox.ItemsSource = _connections;
        ProfilesListBox.ItemsSource = _profiles;
        _overviewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _overviewTimer.Tick += async (_, _) => await RefreshOverviewAsync();
        TrafficCanvas.SizeChanged += (_, _) => UpdateTrafficChart();
        SelectTab("Overview");
    }

    private async void NetworkWorkbenchView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            _overviewTimer.Stop();
            return;
        }

        _overviewTimer.Start();
        if (!_initialized)
        {
            _initialized = true;
            await Task.WhenAll(RefreshOverviewAsync(), RefreshAdaptersAsync(), RefreshConnectionsAsync(), LoadProxyAsync());
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
        if (tab == "Adapters") await RefreshAdaptersAsync();
        if (tab == "Connections") await RefreshConnectionsAsync();
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
        if (force) await _overviewRefreshGate.WaitAsync();
        else if (!await _overviewRefreshGate.WaitAsync(0)) return;
        try
        {
            var snapshot = await Task.Run(NetworkWorkbenchService.GetOverview);
            var elapsed = _previousOverview is null ? 0 : Math.Max(0.1, (snapshot.CapturedAt - _previousOverview.CapturedAt).TotalSeconds);
            var sameAdapter = snapshot.HasPhysicalConnection && _previousOverview is not null &&
                              string.Equals(snapshot.ActiveAdapterId, _previousOverview.ActiveAdapterId, StringComparison.OrdinalIgnoreCase);
            var download = sameAdapter ? Math.Max(0, snapshot.BytesReceived - _previousOverview!.BytesReceived) / elapsed : 0;
            var upload = sameAdapter ? Math.Max(0, snapshot.BytesSent - _previousOverview!.BytesSent) / elapsed : 0;

            if (!sameAdapter)
            {
                _downloadHistory.Clear();
                _uploadHistory.Clear();
            }

            HeaderStatusText.Text = snapshot.ConnectivityText;
            HeaderStatusDot.Fill = BrushFrom(snapshot.IsInternetAvailable ? "#61C995" : "#F0B15A");
            OverviewNetworkText.Text = snapshot.ConnectivityText;
            OverviewAdapterText.Text = snapshot.ActiveAdapterName;
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
            OverviewWifiSignalText.Text = $"{snapshot.WifiSignal} / {snapshot.WifiChannel}";
            OverviewProxyText.Text = snapshot.ProxyText;
            OverviewIpv6Text.Text = snapshot.IPv6Address;
            TrafficUpdatedText.Text = $"{snapshot.ActiveAdapterName} · 更新于 {snapshot.CapturedAt:HH:mm:ss}";
            ProxyDnsText.Text = snapshot.DnsServers;

            AddHistory(_downloadHistory, download);
            AddHistory(_uploadHistory, upload);
            UpdateTrafficChart();
            _previousOverview = snapshot;
        }
        catch (Exception exception)
        {
            HeaderStatusText.Text = $"读取失败：{exception.Message}";
            HeaderStatusDot.Fill = BrushFrom("#EF7E83");
        }
        finally
        {
            _overviewRefreshGate.Release();
        }
    }

    private static void AddHistory(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > 60) history.Dequeue();
    }

    private void UpdateTrafficChart()
    {
        var width = TrafficCanvas.ActualWidth;
        var height = TrafficCanvas.ActualHeight;
        if (width <= 1 || height <= 1) return;
        var max = Math.Max(1024d, _downloadHistory.Concat(_uploadHistory).DefaultIfEmpty(0).Max());
        TrafficAxisMaxText.Text = FormatByteRate(max);
        TrafficAxisMidText.Text = FormatByteRate(max / 2d);
        DownloadPolyline.Points = BuildPoints(_downloadHistory, width, height, max);
        UploadPolyline.Points = BuildPoints(_uploadHistory, width, height, max);
    }

    private static PointCollection BuildPoints(IEnumerable<double> source, double width, double height, double max)
    {
        var values = source.ToArray();
        var points = new PointCollection();
        if (values.Length == 0) return points;
        for (var index = 0; index < values.Length; index++)
        {
            var x = values.Length == 1 ? width : index * width / (values.Length - 1);
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
            _allConnections = portsTask.Result
                .Select(item => new NetworkConnectionRow(item, trafficByProcess.GetValueOrDefault(item.ProcessId)))
                .ToArray();
            ApplyConnectionFilter();
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
            "State" => Sort(filtered, item => item.State, _connectionsAscending),
            _ => Sort(filtered, item => item.LocalAddress, _connectionsAscending)
        };
        _connections.Clear();
        foreach (var item in filtered) _connections.Add(item);
        ConnectionsSummaryText.Text = $"显示 {_connections.Count} / {_allConnections.Count} 项";
    }

    private void ConnectionFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ConnectionsListBox is not null) ApplyConnectionFilter();
    }

    private async void RefreshConnections_Click(object sender, RoutedEventArgs e) => await RefreshConnectionsAsync();

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
                     (TrafficSortHeader, "Traffic", "进程流量"),
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
        try
        {
            var result = await NetworkWorkbenchService.RunDiagnosticAsync(kind, target, port, _diagnosticCancellation.Token);
            _diagnostics.Insert(0, result);
        }
        finally
        {
            _diagnosticCancellation.Dispose();
            _diagnosticCancellation = null;
            CancelDiagnosticButton.IsEnabled = false;
        }
    }

    private void CancelDiagnostic_Click(object sender, RoutedEventArgs e) => _diagnosticCancellation?.Cancel();
    private void ClearDiagnostics_Click(object sender, RoutedEventArgs e) => _diagnostics.Clear();

    private async Task LoadProxyAsync(bool updateSnapshot = true)
    {
        var proxy = await Task.Run(NetworkWorkbenchService.GetCurrentUserProxy);
        var winHttp = await Task.Run(NetworkWorkbenchService.GetWinHttpProxyText);
        if (updateSnapshot) _proxySnapshot = proxy;
        WriteProxyEditor(proxy);
        WinHttpProxyText.Text = string.IsNullOrWhiteSpace(winHttp) ? "未读取到 WinHTTP 代理信息" : winHttp.Trim();
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

    private void ReloadProfiles()
    {
        _profiles.Clear();
        foreach (var profile in NetworkWorkbenchService.LoadProfiles().OrderByDescending(item => item.UpdatedAt)) _profiles.Add(profile);
        ProfilesSummaryText.Text = $"{_profiles.Count} 个方案";
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
        _profiles.Insert(0, new NetworkProfile(name, ReadProxyEditor(), DateTime.Now));
        NetworkWorkbenchService.SaveProfiles(_profiles);
        ProfilesSummaryText.Text = $"{_profiles.Count} 个方案";
    }

    private void ReloadProfiles_Click(object sender, RoutedEventArgs e) => ReloadProfiles();

    private async void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NetworkProfile profile) return;
        if (MessageBox.Show($"应用网络方案“{profile.Name}”吗？", "应用网络方案", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!NetworkWorkbenchService.TrySaveCurrentUserProxy(profile.Proxy, out var error))
        {
            MessageBox.Show(error ?? "应用失败。", "应用网络方案失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        WriteProxyEditor(profile.Proxy);
        SelectTab("Proxy");
        ProxyChangeHintText.Text = $"已应用网络方案“{profile.Name}”。";
        await RefreshOverviewAsync(force: true);
    }

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
    private sealed record NetworkConnectionRow(PortEntry Entry, double TrafficBitsPerSecond)
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
        public string TrafficText => TrafficBitsPerSecond switch
        {
            >= 1_000_000_000 => $"{TrafficBitsPerSecond / 1_000_000_000d:F2} Gbps",
            >= 1_000_000 => $"{TrafficBitsPerSecond / 1_000_000d:F2} Mbps",
            >= 1_000 => $"{TrafficBitsPerSecond / 1_000d:F1} Kbps",
            > 0 => $"{TrafficBitsPerSecond:F0} bps",
            _ => "0 bps"
        };
    }
}
