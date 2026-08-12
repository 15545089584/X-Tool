using System.Net.NetworkInformation;
using System.Collections.Concurrent;

namespace ScreenshotApp.NetworkWorkbench;

internal sealed class NetworkMonitorCoordinator : IAsyncDisposable
{
    private readonly NetworkHistoryStore _historyStore;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _lifecycleSync = new();
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private NetworkOverviewSnapshot? _previousSnapshot;
    private DateTime _currentBucket;
    private string _bucketAdapterId = string.Empty;
    private string _bucketAdapterName = string.Empty;
    private long _bucketDownloadedBytes;
    private long _bucketUploadedBytes;
    private double _bucketDownloadRateTotal;
    private double _bucketUploadRateTotal;
    private double _bucketPeakDownloadRate;
    private double _bucketPeakUploadRate;
    private int _bucketSampleCount;
    private int _persistTicks;
    private bool _networkEventsRegistered;
    private int _dnsFailureSamples;
    private int _gatewayFailureSamples;
    private int _proxyFailureSamples;
    private int _highUploadSamples;
    private DateTime _lastDnsAlert;
    private DateTime _lastGatewayAlert;
    private DateTime _lastProxyAlert;
    private DateTime _lastUploadAlert;
    private DateTime _lastHighLatencyAlert;
    private int _probePersistTicks;
    private NetworkAlertSettings _alertSettings = NetworkAlertSettings.Default;
    private bool _alertSettingsLoaded;
    private int _budgetCheckTicks;
    private DateTime _lastBudgetAlertDate;
    private readonly ConcurrentDictionary<Task, byte> _pendingPersistence = new();

    public NetworkMonitorCoordinator(NetworkHistoryStore historyStore) => _historyStore = historyStore;

    public event EventHandler<NetworkMonitorSample>? SampleAvailable;
    public event EventHandler<NetworkTimelineEvent>? TimelineEventAvailable;
    public event EventHandler<Exception>? MonitorFailed;

    public bool AutoRefreshEnabled { get; set; } = true;

    public void Start()
    {
        lock (_lifecycleSync)
        {
            if (_monitorTask is { IsCompleted: false }) return;
            RegisterNetworkEvents();
            _monitorCancellation = new CancellationTokenSource();
            var cancellationToken = _monitorCancellation.Token;
            _monitorTask = Task.Run(() => MonitorLoopAsync(cancellationToken));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task? monitorTask;
        lock (_lifecycleSync)
        {
            UnregisterNetworkEvents();
            cancellation = _monitorCancellation;
            monitorTask = _monitorTask;
            _monitorCancellation = null;
            _monitorTask = null;
        }
        if (cancellation is null) return;
        cancellation.Cancel();
        try { if (monitorTask is not null) await monitorTask; } catch (OperationCanceledException) { }
        await PersistBucketAsync(CancellationToken.None);
        await AwaitPendingPersistenceAsync();
        cancellation.Dispose();
    }

    public Task RefreshNowAsync(bool invalidateCaches = false, CancellationToken cancellationToken = default)
        => CollectAsync(invalidateCaches, cancellationToken);

    public Task<IReadOnlyList<NetworkTrafficHistoryPoint>> GetTrafficHistoryAsync(TimeSpan range, CancellationToken cancellationToken = default)
        => _historyStore.GetTrafficAsync(range, cancellationToken);

    public Task<IReadOnlyList<NetworkTimelineEvent>> GetTimelineEventsAsync(int limit = 50, CancellationToken cancellationToken = default)
        => _historyStore.GetEventsAsync(limit, cancellationToken);

    public Task<int> GetRetentionDaysAsync(CancellationToken cancellationToken = default)
        => _historyStore.GetRetentionDaysAsync(cancellationToken);

    public Task SetRetentionDaysAsync(int days, CancellationToken cancellationToken = default)
        => _historyStore.SetRetentionDaysAsync(days, cancellationToken);

    public Task ClearHistoryAsync(CancellationToken cancellationToken = default)
        => _historyStore.ClearAsync(cancellationToken);

    public Task<NetworkAlertSettings> GetAlertSettingsAsync(CancellationToken cancellationToken = default)
        => _historyStore.GetAlertSettingsAsync(cancellationToken);

    public async Task SetAlertSettingsAsync(NetworkAlertSettings settings, CancellationToken cancellationToken = default)
    {
        _alertSettings = settings; _alertSettingsLoaded = true;
        await _historyStore.SetAlertSettingsAsync(settings, cancellationToken);
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (AutoRefreshEnabled) await CollectAsync(false, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task CollectAsync(bool invalidateCaches, CancellationToken cancellationToken)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            if (!_alertSettingsLoaded) { _alertSettings = await _historyStore.GetAlertSettingsAsync(cancellationToken); _alertSettingsLoaded = true; }
            if (invalidateCaches) NetworkWorkbenchService.InvalidateNetworkCaches();
            var snapshot = await NetworkWorkbenchService.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
            var previous = _previousSnapshot;
            var sameAdapter = snapshot.HasPhysicalConnection && previous is not null &&
                              string.Equals(snapshot.ActiveAdapterId, previous.ActiveAdapterId, StringComparison.OrdinalIgnoreCase);
            var elapsed = previous is null ? 0 : Math.Max(0.1, (snapshot.CapturedAt - previous.CapturedAt).TotalSeconds);
            var receivedDelta = sameAdapter ? Math.Max(0, snapshot.BytesReceived - previous!.BytesReceived) : 0;
            var sentDelta = sameAdapter ? Math.Max(0, snapshot.BytesSent - previous!.BytesSent) : 0;
            var downloadRate = sameAdapter && elapsed > 0 ? receivedDelta / elapsed : 0;
            var uploadRate = sameAdapter && elapsed > 0 ? sentDelta / elapsed : 0;

            var sample = new NetworkMonitorSample(snapshot, previous, sameAdapter, receivedDelta, sentDelta, downloadRate, uploadRate);
            DetectTimelineEvents(previous, snapshot);
            DetectOperationalAlerts(sample);
            if (++_probePersistTicks % 5 == 0)
            {
                TrackPersistence(_historyStore.AddProbeAsync(new NetworkProbeHistoryPoint(snapshot.CapturedAt, snapshot.ActiveAdapterId,
                    snapshot.ConnectivityProbeText.Contains("网关 可达", StringComparison.Ordinal),
                    snapshot.ConnectivityProbeText.Contains("DNS 正常", StringComparison.Ordinal),
                    snapshot.ConnectivityProbeText.Contains("HTTP 正常", StringComparison.Ordinal),
                    snapshot.GatewayLatencyMs, snapshot.DnsLatencyMs, snapshot.HttpLatencyMs)).AsTask());
            }
            if (++_budgetCheckTicks % 60 == 0) TrackPersistence(CheckDailyBudgetAsync(snapshot));
            AccumulateTraffic(sample);
            _previousSnapshot = snapshot;
            SampleAvailable?.Invoke(this, sample);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            MonitorFailed?.Invoke(this, exception);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void AccumulateTraffic(NetworkMonitorSample sample)
    {
        var bucket = new DateTime(sample.Snapshot.CapturedAt.Year, sample.Snapshot.CapturedAt.Month, sample.Snapshot.CapturedAt.Day,
            sample.Snapshot.CapturedAt.Hour, sample.Snapshot.CapturedAt.Minute, 0, DateTimeKind.Local);
        if (_currentBucket != default && (bucket != _currentBucket || !string.Equals(_bucketAdapterId, sample.Snapshot.ActiveAdapterId, StringComparison.OrdinalIgnoreCase)))
        {
            var completedPoint = CreateBucketPoint();
            ResetBucket(bucket, sample.Snapshot);
            if (completedPoint is not null) TrackPersistence(PersistPointAsync(completedPoint, CancellationToken.None));
        }
        else if (_currentBucket == default)
        {
            ResetBucket(bucket, sample.Snapshot);
        }

        _bucketDownloadedBytes += sample.ReceivedDelta;
        _bucketUploadedBytes += sample.SentDelta;
        _bucketDownloadRateTotal += sample.DownloadRate;
        _bucketUploadRateTotal += sample.UploadRate;
        _bucketPeakDownloadRate = Math.Max(_bucketPeakDownloadRate, sample.DownloadRate);
        _bucketPeakUploadRate = Math.Max(_bucketPeakUploadRate, sample.UploadRate);
        _bucketSampleCount++;
        if (++_persistTicks % 10 == 0) TrackPersistence(PersistBucketAsync(CancellationToken.None));
    }

    private void ResetBucket(DateTime bucket, NetworkOverviewSnapshot snapshot)
    {
        _currentBucket = bucket;
        _bucketAdapterId = string.IsNullOrWhiteSpace(snapshot.ActiveAdapterId)
            ? snapshot.ActiveAdapterName ?? string.Empty
            : snapshot.ActiveAdapterId;
        _bucketAdapterName = snapshot.ActiveAdapterName ?? string.Empty;
        _bucketDownloadedBytes = 0;
        _bucketUploadedBytes = 0;
        _bucketDownloadRateTotal = 0;
        _bucketUploadRateTotal = 0;
        _bucketPeakDownloadRate = 0;
        _bucketPeakUploadRate = 0;
        _bucketSampleCount = 0;
        _persistTicks = 0;
    }

    private async Task PersistBucketAsync(CancellationToken cancellationToken)
    {
        var point = CreateBucketPoint();
        if (point is null) return;
        await PersistPointAsync(point, cancellationToken);
    }

    private NetworkTrafficHistoryPoint? CreateBucketPoint()
    {
        if (_currentBucket == default || _bucketSampleCount == 0) return null;
        var snapshot = _previousSnapshot;
        return new NetworkTrafficHistoryPoint(
            _currentBucket, _bucketAdapterId, _bucketAdapterName, _bucketDownloadedBytes, _bucketUploadedBytes, _bucketSampleCount,
            _bucketDownloadRateTotal / _bucketSampleCount, _bucketUploadRateTotal / _bucketSampleCount,
            _bucketPeakDownloadRate, _bucketPeakUploadRate,
            snapshot?.IsInternetAvailable == true, snapshot?.HasPhysicalConnection == true);
    }

    private async Task PersistPointAsync(NetworkTrafficHistoryPoint point, CancellationToken cancellationToken)
    {
        try { await _historyStore.UpsertTrafficAsync(point); }
        catch (Exception exception) { MonitorFailed?.Invoke(this, exception); }
    }

    private void DetectTimelineEvents(NetworkOverviewSnapshot? previous, NetworkOverviewSnapshot current)
    {
        if (previous is null)
        {
            PublishEvent(new NetworkTimelineEvent(current.CapturedAt, "MonitorStarted", "Info", "开始网络监测",
                current.HasPhysicalConnection ? $"主用网卡：{current.ActiveAdapterName}" : "当前没有可用物理链路",
                current.ActiveAdapterId, current.ActiveAdapterName));
            return;
        }

        if (previous.IsInternetAvailable != current.IsInternetAvailable)
        {
            PublishEvent(new NetworkTimelineEvent(current.CapturedAt, current.IsInternetAvailable ? "InternetRestored" : "InternetLost",
                current.IsInternetAvailable ? "Success" : "Error", current.IsInternetAvailable ? "互联网连接已恢复" : "互联网连接已中断",
                current.ConnectivityProbeText, current.ActiveAdapterId, current.ActiveAdapterName));
        }
        if (previous.HasPhysicalConnection != current.HasPhysicalConnection)
        {
            PublishEvent(new NetworkTimelineEvent(current.CapturedAt, current.HasPhysicalConnection ? "LinkConnected" : "LinkDisconnected",
                current.HasPhysicalConnection ? "Success" : "Warning", current.HasPhysicalConnection ? "物理链路已连接" : "物理链路已断开",
                current.HasPhysicalConnection ? current.ActiveAdapterName : previous.ActiveAdapterName,
                current.ActiveAdapterId, current.ActiveAdapterName));
        }
        if (current.HasPhysicalConnection && !string.Equals(previous.ActiveAdapterId, current.ActiveAdapterId, StringComparison.OrdinalIgnoreCase))
        {
            PublishEvent(new NetworkTimelineEvent(current.CapturedAt, "AdapterChanged", "Info", "主用网卡已切换",
                $"{previous.ActiveAdapterName} → {current.ActiveAdapterName}", current.ActiveAdapterId, current.ActiveAdapterName));
        }
        if (!string.Equals(previous.ProxyText, current.ProxyText, StringComparison.OrdinalIgnoreCase))
        {
            PublishEvent(new NetworkTimelineEvent(current.CapturedAt, "ProxyChanged", "Warning", "代理配置已变化",
                $"{previous.ProxyText} → {current.ProxyText}", current.ActiveAdapterId, current.ActiveAdapterName));
        }
        if (current.HasPhysicalConnection && !string.Equals(previous.DnsServers, current.DnsServers, StringComparison.OrdinalIgnoreCase))
        {
            PublishEvent(new NetworkTimelineEvent(current.CapturedAt, "DnsChanged", "Info", "DNS 配置已变化",
                current.DnsServers, current.ActiveAdapterId, current.ActiveAdapterName));
        }
        if (current.WifiSsid != "—" && !string.Equals(previous.WifiBssid, current.WifiBssid, StringComparison.OrdinalIgnoreCase))
        {
            PublishEvent(new NetworkTimelineEvent(current.CapturedAt, "WifiRoamed", "Info", "Wi-Fi 接入点已变化",
                $"{previous.WifiBssid} → {current.WifiBssid} · 信号 {current.WifiSignal}", current.ActiveAdapterId, current.ActiveAdapterName));
        }
    }

    private void DetectOperationalAlerts(NetworkMonitorSample sample)
    {
        var now = sample.Snapshot.CapturedAt;
        _dnsFailureSamples = sample.Snapshot.ConnectivityProbeText.Contains("DNS 失败", StringComparison.Ordinal) ? _dnsFailureSamples + 1 : 0;
        _gatewayFailureSamples = sample.Snapshot.GatewayLatencyMs < 0 ? _gatewayFailureSamples + 1 : 0;
        var proxyEnabled = sample.Snapshot.ProxyText.Contains("手动代理", StringComparison.Ordinal) || sample.Snapshot.ProxyText.Contains("PAC", StringComparison.Ordinal);
        _proxyFailureSamples = proxyEnabled && !sample.Snapshot.EffectiveHttpSucceeded ? _proxyFailureSamples + 1 : 0;
        _highUploadSamples = sample.UploadRate >= _alertSettings.HighUploadMegabytesPerSecond * 1024 * 1024 ? _highUploadSamples + 1 : 0;
        if (IsQuietHour(now.Hour)) return;
        if (_gatewayFailureSamples >= 3 && now - _lastGatewayAlert > TimeSpan.FromMinutes(10))
        {
            _lastGatewayAlert = now;
            PublishEvent(new NetworkTimelineEvent(now, "GatewayProbeFailure", "Warning", "网关连续未响应",
                "已连续 3 次探测不到默认网关；请检查 Wi-Fi、网线或路由器状态。", sample.Snapshot.ActiveAdapterId, sample.Snapshot.ActiveAdapterName));
        }
        if (_dnsFailureSamples >= 3 && now - _lastDnsAlert > TimeSpan.FromMinutes(10))
        {
            _lastDnsAlert = now;
            PublishEvent(new NetworkTimelineEvent(now, "DnsProbeFailure", "Warning", "DNS 连续探测失败",
                "已连续 3 次探测失败；建议先检查 DNS 配置或执行清理 DNS 缓存。", sample.Snapshot.ActiveAdapterId, sample.Snapshot.ActiveAdapterName));
        }
        if (_proxyFailureSamples >= 3 && now - _lastProxyAlert > TimeSpan.FromMinutes(10))
        {
            _lastProxyAlert = now;
            PublishEvent(new NetworkTimelineEvent(now, "ProxyProbeFailure", "Warning", "代理路径连续不可达",
                "已启用代理，但连续 3 次代理 HTTP 探测失败；可检查代理软件与节点状态。", sample.Snapshot.ActiveAdapterId, sample.Snapshot.ActiveAdapterName));
        }
        if (_highUploadSamples >= 3 && now - _lastUploadAlert > TimeSpan.FromMinutes(10))
        {
            _lastUploadAlert = now;
            PublishEvent(new NetworkTimelineEvent(now, "HighUpload", "Warning", "持续高上传速率",
                $"上传已连续 3 秒超过 {_alertSettings.HighUploadMegabytesPerSecond:F1} MB/s，当前 {sample.UploadRate / 1024 / 1024:F1} MB/s。", sample.Snapshot.ActiveAdapterId, sample.Snapshot.ActiveAdapterName));
        }
        if (sample.Snapshot.HttpLatencyMs >= _alertSettings.HighLatencyMilliseconds && now - _lastHighLatencyAlert > TimeSpan.FromMinutes(10))
        {
            _lastHighLatencyAlert = now;
            PublishEvent(new NetworkTimelineEvent(now, "HighLatency", "Warning", "HTTP 探测延迟偏高",
                $"当前探测耗时 {sample.Snapshot.HttpLatencyMs} ms；告警 10 分钟内不重复。", sample.Snapshot.ActiveAdapterId, sample.Snapshot.ActiveAdapterName));
        }
    }

    private async Task CheckDailyBudgetAsync(NetworkOverviewSnapshot snapshot)
    {
        if (_alertSettings.DailyBudgetGigabytes <= 0 || _lastBudgetAlertDate.Date == DateTime.Today || IsQuietHour(DateTime.Now.Hour)) return;
        try
        {
            var samples = await _historyStore.GetTrafficAsync(TimeSpan.FromDays(1));
            var total = samples.Where(item => item.BucketTime.Date == DateTime.Today).Sum(item => item.DownloadedBytes + item.UploadedBytes);
            if (total < _alertSettings.DailyBudgetGigabytes * 1024 * 1024 * 1024) return;
            _lastBudgetAlertDate = DateTime.Today;
            PublishEvent(new NetworkTimelineEvent(DateTime.Now, "DailyBudget", "Warning", "今日流量达到预算",
                $"今日累计 {total / 1024d / 1024d / 1024d:F2} GB，预算 {_alertSettings.DailyBudgetGigabytes:F2} GB。", snapshot.ActiveAdapterId, snapshot.ActiveAdapterName));
        }
        catch (Exception exception) { MonitorFailed?.Invoke(this, exception); }
    }

    private bool IsQuietHour(int hour)
    {
        var start = _alertSettings.QuietStartHour; var end = _alertSettings.QuietEndHour;
        if (start == end) return false;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    private void PublishEvent(NetworkTimelineEvent entry)
    {
        TrackPersistence(_historyStore.AddEventAsync(entry).AsTask());
        TimelineEventAvailable?.Invoke(this, entry);
    }

    private void TrackPersistence(Task task)
    {
        _pendingPersistence.TryAdd(task, 0);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            _pendingPersistence.TryRemove(completed, out _);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task AwaitPendingPersistenceAsync()
    {
        while (true)
        {
            var pending = _pendingPersistence.Keys.ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending); } catch { }
        }
    }

    private void RegisterNetworkEvents()
    {
        if (_networkEventsRegistered) return;
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        _networkEventsRegistered = true;
    }

    private void UnregisterNetworkEvents()
    {
        if (!_networkEventsRegistered) return;
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        _networkEventsRegistered = false;
    }

    private void NetworkChanged(object? sender, EventArgs e) => _ = DelayedNetworkRefreshAsync();
    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => _ = DelayedNetworkRefreshAsync();

    private async Task DelayedNetworkRefreshAsync()
    {
        try
        {
            await Task.Delay(450, _monitorCancellation?.Token ?? CancellationToken.None);
            await CollectAsync(true, _monitorCancellation?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _refreshGate.Dispose();
    }
}

internal sealed record NetworkMonitorSample(
    NetworkOverviewSnapshot Snapshot,
    NetworkOverviewSnapshot? PreviousSnapshot,
    bool SameAdapter,
    long ReceivedDelta,
    long SentDelta,
    double DownloadRate,
    double UploadRate);
