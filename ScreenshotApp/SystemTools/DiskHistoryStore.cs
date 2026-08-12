using System.Globalization;
using System.IO;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace ScreenshotApp.SystemTools;

/// <summary>磁盘用量历史的单日快照点。</summary>
public sealed record DiskHistoryPoint(DateTime Date, long TotalBytes, long AvailableBytes)
{
    public long UsedBytes => TotalBytes - AvailableBytes;

    public double UsedPercent => TotalBytes <= 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
}

/// <summary>容量快照区间内捕获到的文件变更；只记录路径和变更类型，不读取文件内容。</summary>
public sealed record DiskFileChange(DateTime ChangedAt, string VolumeId, string ChangeKind, string Path);

/// <summary>固定卷容量按天记录到本地 SQLite；只读快照，不接入实时传感器，普通权限即可读取。</summary>
public static class DiskHistoryStore
{
    private const int RetentionDays = 365;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object WatcherGate = new();
    private static readonly List<FileSystemWatcher> FileWatchers = new();
    private static readonly ConcurrentDictionary<string, DateTime> RecentFileChanges = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Channel<DiskFileChange> FileChangeChannel = Channel.CreateBounded<DiskFileChange>(new BoundedChannelOptions(8192)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private static CancellationTokenSource? FileChangeTrackingCancellation;
    private static Task? FileChangeWriterTask;
    private static DateTime LastFileChangePruneAt = DateTime.MinValue;

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

    /// <summary>启动轻量文件变更追踪，供历史图表悬停时显示区间内的新增、删除和修改文件。</summary>
    public static void StartFileChangeTracking()
    {
        lock (WatcherGate)
        {
            if (FileChangeTrackingCancellation is not null)
            {
                return;
            }

            FileChangeTrackingCancellation = new CancellationTokenSource();
            FileChangeWriterTask = Task.Run(() => FileChangeWriterLoopAsync(FileChangeTrackingCancellation.Token));
            foreach (var drive in DriveInfo.GetDrives().Where(item => item.DriveType == DriveType.Fixed && item.IsReady))
            {
                try
                {
                    var watcher = new FileSystemWatcher(drive.RootDirectory.FullName)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        Filter = "*",
                        InternalBufferSize = 64 * 1024
                    };
                    watcher.Created += FileWatcher_Created;
                    watcher.Deleted += FileWatcher_Deleted;
                    watcher.Changed += FileWatcher_Changed;
                    watcher.Renamed += FileWatcher_Renamed;
                    watcher.Error += FileWatcher_Error;
                    watcher.EnableRaisingEvents = true;
                    FileWatchers.Add(watcher);
                }
                catch
                {
                    // 某个卷无法建立监听时跳过，不影响其他卷和容量快照。
                }
            }
        }
    }

    /// <summary>读取指定快照区间内的文件变更，结果按时间倒序并限制数量。</summary>
    public static async Task<IReadOnlyList<DiskFileChange>> LoadFileChangesAsync(
        string volumeId,
        DateTime start,
        DateTime end,
        int limit = 12)
    {
        volumeId = NormalizeVolumeId(volumeId);
        limit = Math.Clamp(limit, 1, 100);
        await Gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = @"SELECT changed_at, volume_id, change_kind, path FROM disk_file_changes
                    WHERE (volume_id = $id OR volume_id = $id_with_separator)
                    AND changed_at > $start AND changed_at <= $end
                    ORDER BY changed_at DESC LIMIT $limit;";
                command.Parameters.AddWithValue("$id", volumeId);
                command.Parameters.AddWithValue("$id_with_separator", volumeId + "\\");
                command.Parameters.AddWithValue("$start", start.ToString("O"));
                command.Parameters.AddWithValue("$end", end.ToString("O"));
                command.Parameters.AddWithValue("$limit", limit);
                var changes = new List<DiskFileChange>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var changedAt = DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    changes.Add(new DiskFileChange(changedAt, reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                }
                return (IReadOnlyList<DiskFileChange>)changes;
            });
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>读取指定卷最近 N 天的快照，按本地日期升序；无记录时返回空列表。</summary>
    public static async Task<IReadOnlyList<DiskHistoryPoint>> LoadHistoryAsync(string volumeId, int days)
    {
        days = Math.Clamp(days, 1, RetentionDays);
        volumeId = NormalizeVolumeId(volumeId);
        await Gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = @"SELECT captured_at, total_bytes, available_bytes FROM disk_snapshots
                    WHERE (volume_id = $id OR volume_id = $id_with_separator)
                    AND snapshot_date >= $since_date AND snapshot_date <= $today
                    ORDER BY snapshot_date, captured_at;";
                command.Parameters.AddWithValue("$id", volumeId);
                command.Parameters.AddWithValue("$id_with_separator", volumeId + "\\");
                var today = DateTime.Today;
                command.Parameters.AddWithValue("$since_date", today.AddDays(-(days - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
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
                using var changes = connection.CreateCommand();
                changes.CommandText = "DELETE FROM disk_file_changes;";
                changes.ExecuteNonQuery();
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

        using var changes = connection.CreateCommand();
        changes.CommandText = @"CREATE TABLE IF NOT EXISTS disk_file_changes (
            changed_at TEXT NOT NULL,
            volume_id TEXT NOT NULL,
            change_kind TEXT NOT NULL,
            path TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_disk_file_changes_volume_time
                ON disk_file_changes(volume_id, changed_at DESC);
            CREATE INDEX IF NOT EXISTS ix_disk_file_changes_time
                ON disk_file_changes(changed_at);";
        changes.ExecuteNonQuery();
    }

    private static void Prune(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"DELETE FROM disk_snapshots WHERE captured_at < $cutoff;
            DELETE FROM disk_file_changes WHERE changed_at < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", DateTime.Today.AddDays(-RetentionDays).ToString("O"));
        command.ExecuteNonQuery();
    }

    private static string NormalizeVolumeId(string volumeId)
    {
        var normalized = volumeId.Trim();
        return normalized.EndsWith("\\", StringComparison.Ordinal) ? normalized[..^1] : normalized;
    }

    private static void FileWatcher_Created(object sender, FileSystemEventArgs e) => QueueFileChange("新增", e.FullPath);

    private static void FileWatcher_Deleted(object sender, FileSystemEventArgs e) => QueueFileChange("删除", e.FullPath);

    private static void FileWatcher_Changed(object sender, FileSystemEventArgs e) => QueueFileChange("修改", e.FullPath);

    private static void FileWatcher_Renamed(object sender, RenamedEventArgs e)
    {
        QueueFileChange("重命名", e.OldFullPath + " → " + e.FullPath);
    }

    private static void FileWatcher_Error(object sender, ErrorEventArgs e)
    {
        // 文件系统监听可能因高频变化丢事件；容量快照仍保持独立可靠，后续区间显示已捕获到的明细。
    }

    private static void QueueFileChange(string changeKind, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || FileChangeTrackingCancellation?.IsCancellationRequested == true)
        {
            return;
        }

        var historyDirectory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(historyDirectory)
            && path.StartsWith(historyDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var now = DateTime.Now;
        var key = changeKind + "|" + path;
        if (RecentFileChanges.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromSeconds(2))
        {
            return;
        }

        RecentFileChanges[key] = now;
        if (RecentFileChanges.Count > 12_000)
        {
            foreach (var item in RecentFileChanges.Where(item => now - item.Value > TimeSpan.FromMinutes(5)).Take(2_000))
            {
                RecentFileChanges.TryRemove(item.Key, out _);
            }
            if (RecentFileChanges.Count > 12_000)
            {
                foreach (var item in RecentFileChanges.OrderBy(item => item.Value).Take(2_000))
                    RecentFileChanges.TryRemove(item.Key, out _);
            }
        }

        FileChangeChannel.Writer.TryWrite(new DiskFileChange(now, root, changeKind, path));
    }

    private static async Task FileChangeWriterLoopAsync(CancellationToken cancellationToken)
    {
        var batch = new List<DiskFileChange>(128);
        try
        {
            while (await FileChangeChannel.Reader.WaitToReadAsync(cancellationToken))
            {
                batch.Clear();
                while (batch.Count < 128 && FileChangeChannel.Reader.TryRead(out var change))
                {
                    batch.Add(change);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                await Gate.WaitAsync(cancellationToken);
                try
                {
                    await Task.Run(() => PersistFileChanges(batch), cancellationToken);
                }
                finally
                {
                    Gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 进程退出或监听停止时结束后台写入。
        }
    }

    private static void PersistFileChanges(IReadOnlyList<DiskFileChange> changes)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var change in changes)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO disk_file_changes (changed_at, volume_id, change_kind, path)
                VALUES ($at, $volume, $kind, $path);";
            command.Parameters.AddWithValue("$at", change.ChangedAt.ToString("O"));
            command.Parameters.AddWithValue("$volume", change.VolumeId);
            command.Parameters.AddWithValue("$kind", change.ChangeKind);
            command.Parameters.AddWithValue("$path", change.Path);
            command.ExecuteNonQuery();
        }

        var now = DateTime.Now;
        if (now - LastFileChangePruneAt >= TimeSpan.FromHours(1))
        {
            using var prune = connection.CreateCommand();
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM disk_file_changes WHERE changed_at < $cutoff;";
            prune.Parameters.AddWithValue("$cutoff", now.AddDays(-RetentionDays).ToString("O"));
            prune.ExecuteNonQuery();
            LastFileChangePruneAt = now;
        }
        transaction.Commit();
    }
}
