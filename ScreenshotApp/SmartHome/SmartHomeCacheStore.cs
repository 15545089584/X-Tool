using System.IO;
using System.Text.Json;

namespace ScreenshotApp.SmartHome;

internal sealed record SmartHomeCacheEnvelope(string ServerUrl, SmartHomeSnapshot Snapshot);

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
            var envelope = JsonSerializer.Deserialize<SmartHomeCacheEnvelope>(File.ReadAllText(CachePath));
            return envelope is not null && string.Equals(
                envelope.ServerUrl.TrimEnd('/'), serverUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                ? envelope.Snapshot
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string serverUrl, SmartHomeSnapshot snapshot)
    {
        Directory.CreateDirectory(CacheDirectory);
        var temporaryPath = CachePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
            new SmartHomeCacheEnvelope(serverUrl, snapshot),
            new JsonSerializerOptions { WriteIndented = false }));
        File.Move(temporaryPath, CachePath, overwrite: true);
    }

    public static void Delete()
    {
        if (File.Exists(CachePath)) File.Delete(CachePath);
    }
}
