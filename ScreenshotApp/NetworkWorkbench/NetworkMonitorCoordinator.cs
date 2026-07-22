using System.Net.NetworkInformation;

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
            if (completedPoint is not null) _ = PersistPointAsync(completedPoint, CancellationToken.None);
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
        if (++_persistTicks % 10 == 0) _ = PersistBucketAsync(CancellationToken.None);
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
    }

    private void PublishEvent(NetworkTimelineEvent entry)
    {
        _ = _historyStore.AddEventAsync(entry);
        TimelineEventAvailable?.Invoke(this, entry);
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
