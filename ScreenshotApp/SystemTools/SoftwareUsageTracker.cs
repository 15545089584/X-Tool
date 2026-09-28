using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.IO;
using System.Management;

namespace ScreenshotApp.SystemTools;

internal sealed record SoftwareUsageRecord(string Path, string ProcessName, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, long LaunchCount);

/// <summary>在用户明确启用后，只把本机进程路径与时间写入本地数据库，不上传也不读取窗口内容。</summary>
internal static class SoftwareUsageTracker
{
    private static readonly object Sync = new();
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "System");
    private static readonly string DatabasePath = Path.Combine(StateDirectory, "software-usage.db");
    private static ManagementEventWatcher? _watcher;
    private static bool _initialized;
    private static bool _enabled;
    private static DateTimeOffset? _trackingStartedAt;

    internal static bool IsEnabled
    {
        get { EnsureInitialized(); lock (Sync) return _enabled; }
    }

    internal static DateTimeOffset? TrackingStartedAt
    {
        get { EnsureInitialized(); lock (Sync) return _trackingStartedAt; }
    }

    internal static void EnsureStartedWhenEnabled()
    {
        EnsureInitialized();
        lock (Sync)
        {
            if (_enabled) StartWatcherLocked();
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        EnsureInitialized();
        lock (Sync)
        {
            if (_enabled == enabled)
            {
                if (enabled) StartWatcherLocked();
                return;
            }
            _enabled = enabled;
            if (enabled && !_trackingStartedAt.HasValue) _trackingStartedAt = DateTimeOffset.Now;
            WriteSettingLocked("tracking_enabled", enabled ? "1" : "0");
            if (_trackingStartedAt.HasValue)
                WriteSettingLocked("tracking_started_utc", _trackingStartedAt.Value.UtcDateTime.ToString("O"));
            if (enabled) StartWatcherLocked();
            else StopWatcherLocked();
        }
    }

    internal static IReadOnlyList<SoftwareUsageRecord> GetSnapshot()
    {
        EnsureInitialized();
        lock (Sync)
        {
            var result = new List<SoftwareUsageRecord>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT executable_path, process_name, first_seen_utc, last_seen_utc, launch_count FROM process_usage";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!DateTimeOffset.TryParse(reader.GetString(2), out var firstSeen) ||
                    !DateTimeOffset.TryParse(reader.GetString(3), out var lastSeen)) continue;
                result.Add(new SoftwareUsageRecord(reader.GetString(0), reader.GetString(1), firstSeen, lastSeen, reader.GetInt64(4)));
            }
            return result;
        }
    }

    private static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (_initialized) return;
            Directory.CreateDirectory(StateDirectory);
            using var connection = OpenConnection();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS settings (
                        key TEXT PRIMARY KEY,
                        value TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS process_usage (
                        executable_path TEXT PRIMARY KEY COLLATE NOCASE,
                        process_name TEXT NOT NULL,
                        first_seen_utc TEXT NOT NULL,
                        last_seen_utc TEXT NOT NULL,
                        launch_count INTEGER NOT NULL
                    );
                    """;
                command.ExecuteNonQuery();
            }
            _enabled = ReadSettingLocked(connection, "tracking_enabled") == "1";
            if (DateTimeOffset.TryParse(ReadSettingLocked(connection, "tracking_started_utc"), out var startedAt))
                _trackingStartedAt = startedAt;
            _initialized = true;
            if (_enabled) StartWatcherLocked();
        }
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        return connection;
    }

    private static string? ReadSettingLocked(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar()?.ToString();
    }

    private static void WriteSettingLocked(string key, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void StartWatcherLocked()
    {
        if (_watcher is not null) return;
        try
        {
            _watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT ProcessID, ProcessName FROM Win32_ProcessStartTrace"));
            _watcher.EventArrived += ProcessStarted;
            _watcher.Start();
        }
        catch
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private static void StopWatcherLocked()
    {
        if (_watcher is null) return;
        try { _watcher.Stop(); }
        catch { }
        _watcher.EventArrived -= ProcessStarted;
        _watcher.Dispose();
        _watcher = null;
    }

    private static void ProcessStarted(object sender, EventArrivedEventArgs e)
    {
        if (!uint.TryParse(e.NewEvent.Properties["ProcessID"]?.Value?.ToString(), out var processId)) return;
        var processName = e.NewEvent.Properties["ProcessName"]?.Value?.ToString() ?? string.Empty;
        _ = Task.Run(async () =>
        {
            var path = await ResolveExecutablePathAsync((int)processId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(path)) return;
            Record(path, processName);
        });
    }

    private static async Task<string> ResolveExecutablePathAsync(int processId)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await Task.Delay(60).ConfigureAwait(false);
        }
        return string.Empty;
    }

    private static void Record(string executablePath, string processName)
    {
        lock (Sync)
        {
            if (!_enabled) return;
            try
            {
                var now = DateTimeOffset.UtcNow.ToString("O");
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO process_usage(executable_path, process_name, first_seen_utc, last_seen_utc, launch_count)
                    VALUES($path, $name, $now, $now, 1)
                    ON CONFLICT(executable_path) DO UPDATE SET
                        process_name = excluded.process_name,
                        last_seen_utc = excluded.last_seen_utc,
                        launch_count = process_usage.launch_count + 1
                    """;
                command.Parameters.AddWithValue("$path", executablePath);
                command.Parameters.AddWithValue("$name", processName);
                command.Parameters.AddWithValue("$now", now);
                command.ExecuteNonQuery();
            }
            catch
            {
                // 使用记录失败不影响程序启动或系统工具页面。
            }
        }
    }
}
