using System.IO;
using System.Text.Json;

namespace ScreenshotApp.Settings;

/// <summary>保存用户可调整的本机偏好，不影响已存在的历史文件。</summary>
public sealed class AppPreferences
{
    private const string PreferencesFileName = "preferences.json";
    private static readonly string PreferencesDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JieYing");
    private static readonly string PreferencesFilePath = Path.Combine(PreferencesDirectory, PreferencesFileName);

    public string ScreenshotDirectory { get; set; } = @"E:\截影\Screenshots";

    public string LongScreenshotDirectory { get; set; } = @"E:\截影\LongScreenshots";

    public string TextExtractionDirectory { get; set; } = @"E:\截影\History\文字提取";

    public string TranslationDirectory { get; set; } = @"E:\截影\History\翻译";

    public string RecordingDirectory { get; set; } = @"E:\截影\Recordings";

    public string ClipboardDirectory { get; set; } = @"E:\截影\Clipboard";

    public string ConverterDirectory { get; set; } = @"E:\截影\Converted";

    public bool StickerTopmost { get; set; } = true;

    public static AppPreferences Load()
    {
        try
        {
            if (!File.Exists(PreferencesFilePath))
            {
                return new AppPreferences();
            }

            return JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(PreferencesFilePath))
                ?? new AppPreferences();
        }
        catch
        {
            // 配置损坏时回退默认值，不能影响截图主流程。
            return new AppPreferences();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(PreferencesDirectory);
        var temporaryPath = $"{PreferencesFilePath}.tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, PreferencesFilePath, overwrite: true);
    }
}
