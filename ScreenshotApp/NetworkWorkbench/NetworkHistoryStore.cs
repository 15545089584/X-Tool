using Microsoft.Data.Sqlite;
using System.IO;
using System.Threading.Channels;
using System.Text.Json;

namespace ScreenshotApp.NetworkWorkbench;

internal sealed class NetworkHistoryStore : IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly Channel<WriteRequest> _writeQueue = Channel.CreateUnbounded<WriteRequest>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _writerTask;

    public NetworkHistoryStore()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "Network");
        Directory.CreateDirectory(directory);
        _databasePath = Path.Combine(directory, "network-history.db");
        _writerTask = Task.Run(WriterLoopAsync);
    }

    public async ValueTask UpsertTrafficAsync(NetworkTrafficHistoryPoint point)
    {
        await EnqueueAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO traffic_samples
                    (bucket_time, adapter_id, adapter_name, downloaded_bytes, uploaded_bytes, sample_count,
                     average_download_rate, average_upload_rate, peak_download_rate, peak_upload_rate,
                     internet_available, physical_connected)
                VALUES
                    ($bucket, $adapterId, $adapterName, $downloaded, $uploaded, $count,
                     $averageDownload, $averageUpload, $peakDownload, $peakUpload, $internet, $physical)
                ON CONFLICT(bucket_time, adapter_id) DO UPDATE SET
                    adapter_name = excluded.adapter_name,
                    downloaded_bytes = excluded.downloaded_bytes,
                    uploaded_bytes = excluded.uploaded_bytes,
                    sample_count = excluded.sample_count,
                    average_download_rate = excluded.average_download_rate,
                    average_upload_rate = excluded.average_upload_rate,
                    peak_download_rate = excluded.peak_download_rate,
                    peak_upload_rate = excluded.peak_upload_rate,
                    internet_available = excluded.internet_available,
                    physical_connected = excluded.physical_connected;";
            command.Parameters.AddWithValue("$bucket", point.BucketTime.ToString("O"));
            command.Parameters.AddWithValue("$adapterId", point.AdapterId);
            command.Parameters.AddWithValue("$adapterName", point.AdapterName);
            command.Parameters.AddWithValue("$downloaded", point.DownloadedBytes);
            command.Parameters.AddWithValue("$uploaded", point.UploadedBytes);
            command.Parameters.AddWithValue("$count", point.SampleCount);
            command.Parameters.AddWithValue("$averageDownload", point.AverageDownloadRate);
            command.Parameters.AddWithValue("$averageUpload", point.AverageUploadRate);
            command.Parameters.AddWithValue("$peakDownload", point.PeakDownloadRate);
            command.Parameters.AddWithValue("$peakUpload", point.PeakUploadRate);
            command.Parameters.AddWithValue("$internet", point.IsInternetAvailable ? 1 : 0);
            command.Parameters.AddWithValue("$physical", point.HasPhysicalConnection ? 1 : 0);
            command.ExecuteNonQuery();
        });
    }

    public async ValueTask AddEventAsync(NetworkTimelineEvent entry)
    {
        await EnqueueAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO network_events (event_time, event_type, severity, title, detail, adapter_id, adapter_name)
                VALUES ($time, $type, $severity, $title, $detail, $adapterId, $adapterName);";
            command.Parameters.AddWithValue("$time", entry.Time.ToString("O"));
            command.Parameters.AddWithValue("$type", entry.EventType);
            command.Parameters.AddWithValue("$severity", entry.Severity);
            command.Parameters.AddWithValue("$title", entry.Title);
            command.Parameters.AddWithValue("$detail", entry.Detail);
            command.Parameters.AddWithValue("$adapterId", entry.AdapterId);
            command.Parameters.AddWithValue("$adapterName", entry.AdapterName);
            command.ExecuteNonQuery();
        });
    }

    public async ValueTask AddProbeAsync(NetworkProbeHistoryPoint point)
    {
        await EnqueueAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO probe_samples
                (sample_time, adapter_id, gateway_ok, dns_ok, http_ok, gateway_ms, dns_ms, http_ms)
                VALUES ($time, $adapter, $gatewayOk, $dnsOk, $httpOk, $gatewayMs, $dnsMs, $httpMs);";
            command.Parameters.AddWithValue("$time", point.Time.ToString("O"));
            command.Parameters.AddWithValue("$adapter", point.AdapterId);
            command.Parameters.AddWithValue("$gatewayOk", point.GatewaySucceeded ? 1 : 0);
            command.Parameters.AddWithValue("$dnsOk", point.DnsSucceeded ? 1 : 0);
            command.Parameters.AddWithValue("$httpOk", point.HttpSucceeded ? 1 : 0);
            command.Parameters.AddWithValue("$gatewayMs", point.GatewayLatencyMs);
            command.Parameters.AddWithValue("$dnsMs", point.DnsLatencyMs);
            command.Parameters.AddWithValue("$httpMs", point.HttpLatencyMs);
            command.ExecuteNonQuery();
        });
    }

    public async ValueTask UpsertProcessTrafficAsync(
        string sessionId,
        IReadOnlyList<NetworkProcessTrafficHistoryPoint> points,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || points.Count == 0) return;
        await EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO process_traffic_sessions
                    (session_id, process_key, process_name, process_path, sent_bytes, received_bytes,
                     proxy_exit_sent_bytes, proxy_exit_received_bytes, proxy_exit_seen, proxy_ingress_seen, last_seen)
                VALUES
                    ($session, $key, $name, $path, $sent, $received, $proxySent, $proxyReceived,
                     $proxyExitSeen, $proxyIngressSeen, $lastSeen)
                ON CONFLICT(session_id, process_key) DO UPDATE SET
                    process_name = excluded.process_name,
                    process_path = excluded.process_path,
                    sent_bytes = excluded.sent_bytes,
                    received_bytes = excluded.received_bytes,
                    proxy_exit_sent_bytes = excluded.proxy_exit_sent_bytes,
                    proxy_exit_received_bytes = excluded.proxy_exit_received_bytes,
                    proxy_exit_seen = excluded.proxy_exit_seen,
                    proxy_ingress_seen = excluded.proxy_ingress_seen,
                    last_seen = excluded.last_seen;";
            foreach (var point in points)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$key", point.ProcessKey);
                command.Parameters.AddWithValue("$name", point.ProcessName);
                command.Parameters.AddWithValue("$path", point.ProcessPath);
                command.Parameters.AddWithValue("$sent", point.SentBytes);
                command.Parameters.AddWithValue("$received", point.ReceivedBytes);
                command.Parameters.AddWithValue("$proxySent", point.ProxyExitSentBytes);
                command.Parameters.AddWithValue("$proxyReceived", point.ProxyExitReceivedBytes);
                command.Parameters.AddWithValue("$proxyExitSeen", point.ProxyRole == "Exit" ? 1 : 0);
                command.Parameters.AddWithValue("$proxyIngressSeen", point.ProxyRole == "Ingress" ? 1 : 0);
                command.Parameters.AddWithValue("$lastSeen", point.LastSeen.ToString("O"));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<NetworkProcessTrafficHistoryTotal>> GetProcessTrafficTotalsAsync(
        TimeSpan range,
        string excludeSessionId = "",
        CancellationToken cancellationToken = default)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        return await Task.Run(() =>
        {
            var result = new List<NetworkProcessTrafficHistoryTotal>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT process_key, MAX(process_name), MAX(process_path),
                       SUM(sent_bytes), SUM(received_bytes),
                       SUM(CASE WHEN proxy_exit_seen = 1 THEN proxy_exit_sent_bytes ELSE 0 END),
                       SUM(CASE WHEN proxy_exit_seen = 1 THEN proxy_exit_received_bytes ELSE 0 END),
                       MAX(proxy_exit_seen), MAX(proxy_ingress_seen), MAX(last_seen)
                FROM process_traffic_sessions
                WHERE last_seen >= $start AND ($excludeSession = '' OR session_id <> $excludeSession)
                GROUP BY process_key
                ORDER BY SUM(sent_bytes + received_bytes) DESC;";
            command.Parameters.AddWithValue("$start", DateTime.Now.Subtract(range).ToString("O"));
            command.Parameters.AddWithValue("$excludeSession", excludeSessionId ?? string.Empty);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new NetworkProcessTrafficHistoryTotal(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                    reader.GetInt32(7) != 0, reader.GetInt32(8) != 0,
                    DateTime.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind)));
            }
            return (IReadOnlyList<NetworkProcessTrafficHistoryTotal>)result;
        }, cancellationToken);
    }

    public async ValueTask AddProcessTrafficUsageAsync(
        DateTime bucketTime,
        long nonProxyBytes,
        long proxyBytes,
        CancellationToken cancellationToken = default)
    {
        if (nonProxyBytes <= 0 && proxyBytes <= 0) return;
        var hour = new DateTime(bucketTime.Year, bucketTime.Month, bucketTime.Day, bucketTime.Hour, 0, 0, DateTimeKind.Local);
        await EnqueueAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO process_traffic_usage_buckets
                    (bucket_time, non_proxy_bytes, proxy_bytes)
                VALUES ($bucket, $nonProxy, $proxy)
                ON CONFLICT(bucket_time) DO UPDATE SET
                    non_proxy_bytes = non_proxy_bytes + excluded.non_proxy_bytes,
                    proxy_bytes = proxy_bytes + excluded.proxy_bytes;";
            command.Parameters.AddWithValue("$bucket", hour.ToString("O"));
            command.Parameters.AddWithValue("$nonProxy", Math.Max(0, nonProxyBytes));
            command.Parameters.AddWithValue("$proxy", Math.Max(0, proxyBytes));
            command.ExecuteNonQuery();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<NetworkProcessTrafficUsageBucket>> GetProcessTrafficUsageAsync(
        DateTime startTime,
        CancellationToken cancellationToken = default)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        return await Task.Run(() =>
        {
            var result = new List<NetworkProcessTrafficUsageBucket>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT bucket_time, non_proxy_bytes, proxy_bytes
                FROM process_traffic_usage_buckets
                WHERE bucket_time >= $start
                ORDER BY bucket_time;";
            command.Parameters.AddWithValue("$start", startTime.ToString("O"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new NetworkProcessTrafficUsageBucket(
                    DateTime.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    reader.GetInt64(1), reader.GetInt64(2)));
            }
            return (IReadOnlyList<NetworkProcessTrafficUsageBucket>)result;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<NetworkTrafficHistoryPoint>> GetTrafficAsync(TimeSpan range, CancellationToken cancellationToken = default)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        return await Task.Run(() =>
        {
            var result = new List<NetworkTrafficHistoryPoint>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT bucket_time, adapter_id, adapter_name, downloaded_bytes, uploaded_bytes, sample_count,
                       average_download_rate, average_upload_rate, peak_download_rate, peak_upload_rate,
                       internet_available, physical_connected
                FROM traffic_samples
                WHERE bucket_time >= $start
                ORDER BY bucket_time;";
            command.Parameters.AddWithValue("$start", DateTime.Now.Subtract(range).ToString("O"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new NetworkTrafficHistoryPoint(
                    DateTime.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5),
                    reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8), reader.GetDouble(9),
                    reader.GetInt32(10) != 0, reader.GetInt32(11) != 0));
            }
            return (IReadOnlyList<NetworkTrafficHistoryPoint>)result;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<NetworkTimelineEvent>> GetEventsAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        return await Task.Run(() =>
        {
            var result = new List<NetworkTimelineEvent>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT event_time, event_type, severity, title, detail, adapter_id, adapter_name
                FROM network_events ORDER BY event_time DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new NetworkTimelineEvent(
                    DateTime.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
            }
            return (IReadOnlyList<NetworkTimelineEvent>)result;
        }, cancellationToken);
    }

    public async Task<int> GetRetentionDaysAsync(CancellationToken cancellationToken = default)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        return await Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = 'retention_days';";
            return int.TryParse(command.ExecuteScalar()?.ToString(), out var days) ? Math.Clamp(days, 1, 90) : 7;
        }, cancellationToken);
    }

    public async Task SetRetentionDaysAsync(int days, CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 90);
        await EnqueueAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO settings (key, value) VALUES ('retention_days', $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$value", days.ToString());
            command.ExecuteNonQuery();
            Prune(connection, days);
        }, cancellationToken);
    }

    public async Task<NetworkAlertSettings> GetAlertSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        return await Task.Run(() =>
        {
            using var connection = OpenConnection(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = 'alert_settings';";
            try { return JsonSerializer.Deserialize<NetworkAlertSettings>(command.ExecuteScalar()?.ToString() ?? string.Empty) ?? NetworkAlertSettings.Default; }
            catch { return NetworkAlertSettings.Default; }
        }, cancellationToken);
    }

    public Task SetAlertSettingsAsync(NetworkAlertSettings settings, CancellationToken cancellationToken = default)
        => EnqueueAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO settings (key, value) VALUES ('alert_settings', $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(settings)); command.ExecuteNonQuery();
        }, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default) => EnqueueAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM traffic_samples; DELETE FROM probe_samples; DELETE FROM network_events; DELETE FROM process_traffic_sessions; DELETE FROM process_traffic_usage_buckets;";
        command.ExecuteNonQuery();
    }, cancellationToken);

    private async Task EnqueueAsync(Action<SqliteConnection> action, CancellationToken cancellationToken = default)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _writeQueue.Writer.WriteAsync(new WriteRequest(action, completion), cancellationToken);
        await completion.Task.WaitAsync(cancellationToken);
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            using var connection = OpenConnection();
            InitializeDatabase(connection);
            _initialized.TrySetResult();
            await foreach (var request in _writeQueue.Reader.ReadAllAsync())
            {
                try
                {
                    request.Action(connection);
                    request.Completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    request.Completion.TrySetException(exception);
                }
            }
        }
        catch (Exception exception)
        {
            _initialized.TrySetException(exception);
            while (_writeQueue.Reader.TryRead(out var request)) request.Completion.TrySetException(exception);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void InitializeDatabase(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 3000;
            CREATE TABLE IF NOT EXISTS traffic_samples (
                bucket_time TEXT NOT NULL,
                adapter_id TEXT NOT NULL,
                adapter_name TEXT NOT NULL,
                downloaded_bytes INTEGER NOT NULL,
                uploaded_bytes INTEGER NOT NULL,
                sample_count INTEGER NOT NULL,
                average_download_rate REAL NOT NULL,
                average_upload_rate REAL NOT NULL,
                peak_download_rate REAL NOT NULL,
                peak_upload_rate REAL NOT NULL,
                internet_available INTEGER NOT NULL,
                physical_connected INTEGER NOT NULL,
                PRIMARY KEY (bucket_time, adapter_id));
            CREATE INDEX IF NOT EXISTS ix_traffic_samples_time ON traffic_samples(bucket_time);
            CREATE TABLE IF NOT EXISTS probe_samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sample_time TEXT NOT NULL,
                adapter_id TEXT NOT NULL,
                gateway_ok INTEGER NOT NULL,
                dns_ok INTEGER NOT NULL,
                http_ok INTEGER NOT NULL,
                gateway_ms INTEGER NOT NULL,
                dns_ms INTEGER NOT NULL,
                http_ms INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_probe_samples_time ON probe_samples(sample_time DESC);
            CREATE TABLE IF NOT EXISTS network_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_time TEXT NOT NULL,
                event_type TEXT NOT NULL,
                severity TEXT NOT NULL,
                title TEXT NOT NULL,
                detail TEXT NOT NULL,
                adapter_id TEXT NOT NULL,
                adapter_name TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_network_events_time ON network_events(event_time DESC);
            CREATE TABLE IF NOT EXISTS process_traffic_sessions (
                session_id TEXT NOT NULL,
                process_key TEXT NOT NULL,
                process_name TEXT NOT NULL,
                process_path TEXT NOT NULL,
                sent_bytes INTEGER NOT NULL,
                received_bytes INTEGER NOT NULL,
                proxy_exit_sent_bytes INTEGER NOT NULL,
                proxy_exit_received_bytes INTEGER NOT NULL,
                proxy_exit_seen INTEGER NOT NULL,
                proxy_ingress_seen INTEGER NOT NULL,
                last_seen TEXT NOT NULL,
                PRIMARY KEY (session_id, process_key));
            CREATE INDEX IF NOT EXISTS ix_process_traffic_sessions_time ON process_traffic_sessions(last_seen);
            CREATE TABLE IF NOT EXISTS process_traffic_usage_buckets (
                bucket_time TEXT PRIMARY KEY,
                non_proxy_bytes INTEGER NOT NULL,
                proxy_bytes INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_process_traffic_usage_time ON process_traffic_usage_buckets(bucket_time);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT OR IGNORE INTO settings (key, value) VALUES ('retention_days', '7');";
        command.ExecuteNonQuery();

        using var retentionCommand = connection.CreateCommand();
        retentionCommand.CommandText = "SELECT value FROM settings WHERE key = 'retention_days';";
        var days = int.TryParse(retentionCommand.ExecuteScalar()?.ToString(), out var parsed) ? Math.Clamp(parsed, 1, 90) : 7;
        Prune(connection, days);
    }

    private static void Prune(SqliteConnection connection, int days)
    {
        var threshold = DateTime.Now.AddDays(-days).ToString("O");
        var usageThreshold = DateTime.Now.AddDays(-Math.Max(days, 30)).ToString("O");
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM traffic_samples WHERE bucket_time < $threshold; DELETE FROM probe_samples WHERE sample_time < $threshold; DELETE FROM network_events WHERE event_time < $threshold; DELETE FROM process_traffic_sessions WHERE last_seen < $threshold; DELETE FROM process_traffic_usage_buckets WHERE bucket_time < $usageThreshold;";
        command.Parameters.AddWithValue("$threshold", threshold);
        command.Parameters.AddWithValue("$usageThreshold", usageThreshold);
        command.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        _writeQueue.Writer.TryComplete();
        try { await _writerTask; } catch { }
    }

    private sealed record WriteRequest(Action<SqliteConnection> Action, TaskCompletionSource Completion);
}

internal sealed record NetworkTrafficHistoryPoint(
    DateTime BucketTime,
    string AdapterId,
    string AdapterName,
    long DownloadedBytes,
    long UploadedBytes,
    int SampleCount,
    double AverageDownloadRate,
    double AverageUploadRate,
    double PeakDownloadRate,
    double PeakUploadRate,
    bool IsInternetAvailable,
    bool HasPhysicalConnection);

internal sealed record NetworkTimelineEvent(
    DateTime Time,
    string EventType,
    string Severity,
    string Title,
    string Detail,
    string AdapterId,
    string AdapterName)
{
    public string TimeText => Time.ToString("MM-dd HH:mm:ss");
    public string Summary => string.IsNullOrWhiteSpace(Detail) ? Title : $"{Title} · {Detail}";
    public string AccentColor => Severity switch { "Error" => "#EF7E83", "Warning" => "#F0B15A", "Success" => "#61C995", _ => "#4D7CFE" };
}

internal sealed record NetworkProbeHistoryPoint(DateTime Time, string AdapterId, bool GatewaySucceeded, bool DnsSucceeded,
    bool HttpSucceeded, long GatewayLatencyMs, long DnsLatencyMs, long HttpLatencyMs);

internal sealed record NetworkProcessTrafficHistoryPoint(
    string ProcessKey,
    string ProcessName,
    string ProcessPath,
    long SentBytes,
    long ReceivedBytes,
    long ProxyExitSentBytes,
    long ProxyExitReceivedBytes,
    string ProxyRole,
    DateTime LastSeen);

internal sealed record NetworkProcessTrafficHistoryTotal(
    string ProcessKey,
    string ProcessName,
    string ProcessPath,
    long SentBytes,
    long ReceivedBytes,
    long ProxyExitSentBytes,
    long ProxyExitReceivedBytes,
    bool ProxyExitSeen,
    bool ProxyIngressSeen,
    DateTime LastSeen)
{
    public long TotalBytes => SentBytes + ReceivedBytes;
    public long ProxyExitTotalBytes => ProxyExitSentBytes + ProxyExitReceivedBytes;
}

internal sealed record NetworkProcessTrafficUsageBucket(DateTime BucketTime, long NonProxyBytes, long ProxyBytes)
{
    public long TotalBytes => NonProxyBytes + ProxyBytes;
}

public sealed record NetworkAlertSettings(double HighUploadMegabytesPerSecond, int HighLatencyMilliseconds,
    double DailyBudgetGigabytes, int QuietStartHour, int QuietEndHour)
{
    public static NetworkAlertSettings Default { get; } = new(10, 800, 0, 23, 7);
}
