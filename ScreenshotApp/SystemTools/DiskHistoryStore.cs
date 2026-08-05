using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ScreenshotApp.SystemTools;

/// <summary>磁盘用量历史的单日快照点。</summary>
public sealed record DiskHistoryPoint(DateTime Date, long TotalBytes, long AvailableBytes)
{
    public long UsedBytes => TotalBytes - AvailableBytes;

    public double UsedPercent => TotalBytes <= 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
}

/// <summary>固定卷容量按天记录到本地 SQLite；只读快照，不接入实时传感器，普通权限即可读取。</summary>
public static class DiskHistoryStore
{
    private const int RetentionDays = 365;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "X-Tool",
        "System",
        "disk-history.db");

    /// <summary>为所有固定卷写入当天快照（同一天已存在则跳过），并清理超期记录。</summary>
    public static async Task EnsureTodaySnapshotAsync()
    {
        await Gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                using var connection = OpenConnection();
                foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = @"INSERT OR IGNORE INTO disk_snapshots (volume_id, volume_name, total_bytes, available_bytes, captured_at, snapshot_date)
                        VALUES ($id, $name, $total, $available, $at, $date);";
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? string.Empty : " " + drive.VolumeLabel;
                    command.Parameters.AddWithValue("$id", drive.Name);
                    command.Parameters.AddWithValue("$name", drive.Name.TrimEnd('\\') + label);
                    command.Parameters.AddWithValue("$total", drive.TotalSize);
                    command.Parameters.AddWithValue("$available", drive.AvailableFreeSpace);
                    command.Parameters.AddWithValue("$at", DateTime.Now.ToString("O"));
                    command.Parameters.AddWithValue("$date", DateTime.Now.ToString("yyyy-MM-dd"));
                    command.ExecuteNonQuery();
                }
                Prune(connection);
            });
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>读取指定卷最近 N 天的快照，按时间升序；无记录时返回空列表。</summary>
    public static async Task<IReadOnlyList<DiskHistoryPoint>> LoadHistoryAsync(string volumeId, int days)
    {
        await Gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = @"SELECT captured_at, total_bytes, available_bytes FROM disk_snapshots
                    WHERE volume_id = $id AND captured_at >= $since ORDER BY captured_at;";
                command.Parameters.AddWithValue("$id", volumeId);
                command.Parameters.AddWithValue("$since", DateTime.Today.AddDays(-(days - 1)).ToString("O"));
                var points = new List<DiskHistoryPoint>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var at = DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    points.Add(new DiskHistoryPoint(at, reader.GetInt64(1), reader.GetInt64(2)));
                }
                return (IReadOnlyList<DiskHistoryPoint>)points;
            });
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task ClearAsync()
    {
        await Gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM disk_snapshots;";
                command.ExecuteNonQuery();
            });
        }
        finally
        {
            Gate.Release();
        }
    }

    private static SqliteConnection OpenConnection()
    {
        var directory = Path.GetDirectoryName(DatabasePath)!;
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }
        InitializeDatabase(connection);
        return connection;
    }

    private static void InitializeDatabase(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"CREATE TABLE IF NOT EXISTS disk_snapshots (
            volume_id TEXT NOT NULL,
            volume_name TEXT NOT NULL,
            total_bytes INTEGER NOT NULL,
            available_bytes INTEGER NOT NULL,
            captured_at TEXT NOT NULL,
            snapshot_date TEXT NOT NULL,
            PRIMARY KEY (volume_id, snapshot_date));";
        command.ExecuteNonQuery();
    }

    private static void Prune(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM disk_snapshots WHERE captured_at < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", DateTime.Today.AddDays(-RetentionDays).ToString("O"));
        command.ExecuteNonQuery();
    }
}
