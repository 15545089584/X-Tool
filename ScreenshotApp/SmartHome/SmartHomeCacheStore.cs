using System.IO;
using System.Text.Json;

namespace ScreenshotApp.SmartHome;

internal sealed record SmartHomeCacheEnvelope(string ServerUrl, SmartHomeSnapshot Snapshot, int SchemaVersion = 0);

/// <summary>缓存设备元数据和上次状态；离线展示时必须明确标记为历史状态。</summary>
public static class SmartHomeCacheStore
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "X-Tool",
        "SmartHome");

    private static readonly string CachePath = Path.Combine(CacheDirectory, "device-cache.json");

    public static SmartHomeSnapshot? Load(string serverUrl)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            return ReadVerifiedSnapshot(File.ReadAllText(CachePath), serverUrl);
        }
        catch
        {
            return null;
        }
    }

    internal static SmartHomeSnapshot? ReadVerifiedSnapshot(string json, string serverUrl)
    {
        var envelope = JsonSerializer.Deserialize<SmartHomeCacheEnvelope>(json);
        // 旧缓存可能来自归属加载失败后的错误聚合，保留原文件但不再展示。
        return envelope is { SchemaVersion: 1 } && string.Equals(
            envelope.ServerUrl.TrimEnd('/'), serverUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            ? envelope.Snapshot : null;
    }

    public static void Save(string serverUrl, SmartHomeSnapshot snapshot)
    {
        Directory.CreateDirectory(CacheDirectory);
        var temporaryPath = CachePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
            new SmartHomeCacheEnvelope(serverUrl, snapshot, SchemaVersion: 1),
            new JsonSerializerOptions { WriteIndented = false }));
        File.Move(temporaryPath, CachePath, overwrite: true);
    }

    public static void Delete()
    {
        if (File.Exists(CachePath)) File.Delete(CachePath);
    }
}
