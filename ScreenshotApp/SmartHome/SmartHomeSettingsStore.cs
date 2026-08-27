using System.IO;
using System.Text.Json;

namespace ScreenshotApp.SmartHome;

public sealed record SmartHomeConnectionSettings
{
    public string ServerUrl { get; init; } = string.Empty;

    public bool AutoConnect { get; init; } = true;
}

/// <summary>仅保存非敏感连接偏好；Access Token 由凭据管理器单独保护。</summary>
public static class SmartHomeSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "X-Tool",
        "SmartHome");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static SmartHomeConnectionSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new SmartHomeConnectionSettings();
            return JsonSerializer.Deserialize<SmartHomeConnectionSettings>(
                       File.ReadAllText(SettingsPath)) ?? new SmartHomeConnectionSettings();
        }
        catch
        {
            return new SmartHomeConnectionSettings();
        }
    }

    public static void Save(SmartHomeConnectionSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    public static void Delete()
    {
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
    }

    public static Uri NormalizeServerUri(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("请输入有效的 Home Assistant HTTP 或 HTTPS 地址。", nameof(value));
        }

        return uri;
    }
}
