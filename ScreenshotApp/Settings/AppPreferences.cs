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

    public bool StickerTopmost { get; set; } = true;

    /// <summary>桌面宠物相对于 288 DIP 基准尺寸的缩放百分比。</summary>
    public int DesktopPetScalePercent { get; set; } = 100;

    /// <summary>是否显示桌面宠物；关闭后下次启动仍保持隐藏。</summary>
    public bool DesktopPetVisible { get; set; } = true;

    public bool VoiceInputEnabled { get; set; } = true;

    public bool VoiceInputPasteAutomatically { get; set; } = true;

    /// <summary>是否在当前 Windows 用户登录后自动启动 X-Tool。</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>是否使用一次授权注册的独立 ETW 辅助计划任务。</summary>
    public bool NetworkEtwAutoStart { get; set; }

    public string ScreenshotShortcut { get; set; } = "Ctrl+Shift+A";

    public string FullScreenShortcut { get; set; } = "RightCtrl";

    public string ClipboardShortcut { get; set; } = "Ctrl+Shift+V";

    public string VoiceInputShortcut { get; set; } = "RightAlt";

    public static AppPreferences Load()
    {
        try
        {
            if (!File.Exists(PreferencesFilePath))
            {
                return new AppPreferences();
            }

            var preferences = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(PreferencesFilePath))
                              ?? new AppPreferences();
            preferences.DesktopPetScalePercent = Math.Clamp(preferences.DesktopPetScalePercent, 60, 160);
            return preferences;
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
