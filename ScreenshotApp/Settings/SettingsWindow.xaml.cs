using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotApp.Collaboration;
using ScreenshotApp.NetworkWorkbench;
using ScreenshotApp.Shortcuts;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.Settings;

/// <summary>按类别组织应用设置的半透明子窗口。</summary>
public partial class SettingsWindow : Window
{
    internal static IReadOnlyList<string> ValidationCategories { get; } =
        ["General", "Screenshot", "Shortcut", "DesktopPet", "ReadWrite", "NetworkTraffic", "Connection"];

    private readonly AppPreferences _preferences;
    private readonly Func<string, GlobalShortcut, string?> _applyShortcut;
    private readonly Func<bool, string> _applyVoiceInputEnabled;
    private readonly Action<int> _applyDesktopPetScale;
    private readonly Func<bool, bool> _applyDesktopPetVisibility;
    private readonly Func<Task> _refreshHistory;
    private readonly Func<Task<(bool Success, string Message)>> _startNetworkTraffic;
    private readonly Action _stopNetworkTraffic;
    private GlobalShortcut _screenshotShortcut;
    private GlobalShortcut _fullScreenShortcut;
    private GlobalShortcut _clipboardShortcut;
    private GlobalShortcut _voiceInputShortcut;
    private string? _editingShortcutTarget;
    private bool _initialized;

    internal SettingsWindow(
        AppPreferences preferences,
        GlobalShortcut screenshotShortcut,
        GlobalShortcut fullScreenShortcut,
        GlobalShortcut clipboardShortcut,
        GlobalShortcut voiceInputShortcut,
        Func<string, GlobalShortcut, string?> applyShortcut,
        Func<bool, string> applyVoiceInputEnabled,
        Action<int> applyDesktopPetScale,
        bool desktopPetVisible,
        Func<bool, bool> applyDesktopPetVisibility,
        Func<Task> refreshHistory,
        Func<Task<(bool Success, string Message)>> startNetworkTraffic,
        Action stopNetworkTraffic,
        string voiceInputModelStatus)
    {
        InitializeComponent();
        _preferences = preferences;
        _screenshotShortcut = screenshotShortcut;
        _fullScreenShortcut = fullScreenShortcut;
        _clipboardShortcut = clipboardShortcut;
        _voiceInputShortcut = voiceInputShortcut;
        _applyShortcut = applyShortcut;
        _applyVoiceInputEnabled = applyVoiceInputEnabled;
        _applyDesktopPetScale = applyDesktopPetScale;
        _applyDesktopPetVisibility = applyDesktopPetVisibility;
        _refreshHistory = refreshHistory;
        _startNetworkTraffic = startNetworkTraffic;
        _stopNetworkTraffic = stopNetworkTraffic;

        StartWithWindowsCheckBox.IsChecked = AutoStartService.IsEnabled();
        StickerTopmostCheckBox.IsChecked = preferences.StickerTopmost;
        VoiceInputEnabledCheckBox.IsChecked = preferences.VoiceInputEnabled;
        VoiceInputPasteAutomaticallyCheckBox.IsChecked = preferences.VoiceInputPasteAutomatically;
        VoiceInputModelStatusText.Text = voiceInputModelStatus;
        DesktopPetVisibleCheckBox.IsChecked = desktopPetVisible;
        CollaborationAutoReconnectCheckBox.IsChecked = preferences.CollaborationAutoReconnect;
        DesktopPetScaleSlider.Value = Math.Clamp(preferences.DesktopPetScalePercent, 60, 160);
        UpdateDesktopPetScaleText((int)Math.Round(DesktopPetScaleSlider.Value));
        UpdateShortcutButtons();
        UpdateStorageLocationText();
        _initialized = true;
        ShowCategory("General");
        Loaded += SettingsWindow_Loaded;
    }

    internal void SelectCategoryForValidation(string category)
    {
        var navigation = category switch
        {
            "Screenshot" => ScreenshotCategoryNav,
            "Shortcut" => ShortcutCategoryNav,
            "DesktopPet" => DesktopPetCategoryNav,
            "ReadWrite" => ReadWriteCategoryNav,
            "NetworkTraffic" => NetworkTrafficCategoryNav,
            "Connection" => ConnectionCategoryNav,
            _ => GeneralCategoryNav
        };
        navigation.IsChecked = true;
        ShowCategory(category);
        UpdateLayout();
    }

    internal void SelectCategory(string category) => SelectCategoryForValidation(category);

    internal void CaptureForValidation(string outputPath)
    {
        UpdateLayout();
        var width = Math.Max(1, (int)Math.Round(ActualWidth));
        var height = Math.Max(1, (int)Math.Round(ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (Owner is { ActualWidth: > 0, ActualHeight: > 0 } owner)
        {
            Width = Math.Clamp(owner.ActualWidth * 0.82, MinWidth, MaxWidth);
            Height = Math.Clamp(owner.ActualHeight * 0.80, MinHeight, MaxHeight);
        }

        await RefreshNetworkAuthorizationStateAsync();
    }

    private void CategoryNav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not FrameworkElement { Tag: string category })
        {
            return;
        }

        ShowCategory(category);
    }

    private void ShowCategory(string category)
    {
        GeneralSettingsPanel.Visibility = Visibility.Collapsed;
        ScreenshotSettingsPanel.Visibility = Visibility.Collapsed;
        ShortcutSettingsPanel.Visibility = Visibility.Collapsed;
        DesktopPetSettingsPanel.Visibility = Visibility.Collapsed;
        ReadWriteSettingsPanel.Visibility = Visibility.Collapsed;
        NetworkTrafficSettingsPanel.Visibility = Visibility.Collapsed;
        ConnectionSettingsPanel.Visibility = Visibility.Collapsed;

        var (title, description, panel) = category switch
        {
            "Screenshot" => ("截图设置", "管理截图完成后的行为、贴图和内容存储位置", (FrameworkElement)ScreenshotSettingsPanel),
            "Shortcut" => ("快捷键设置", "直接查看并修改 X-Tool 的全部全局快捷键", ShortcutSettingsPanel),
            "DesktopPet" => ("桌面宠物设置", "调整桌面宠物的显示尺寸", DesktopPetSettingsPanel),
            "ReadWrite" => ("读写设置", "管理本地语音输入与识别结果写入行为", ReadWriteSettingsPanel),
            "NetworkTraffic" => ("网络流量自动获取", "管理 ETW 辅助任务的授权和自动连接", NetworkTrafficSettingsPanel),
            "Connection" => ("连接", "管理手机自动重连、局域网发现与文件收发位置", ConnectionSettingsPanel),
            _ => ("通用设置", "管理应用启动与基础行为", GeneralSettingsPanel)
        };
        CategoryTitleText.Text = title;
        CategoryDescriptionText.Text = description;
        panel.Visibility = Visibility.Visible;
    }

    private void StartWithWindowsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        var enabled = StartWithWindowsCheckBox.IsChecked == true;
        if (!AutoStartService.TrySetEnabled(enabled, out var error))
        {
            StartWithWindowsCheckBox.IsChecked = AutoStartService.IsEnabled();
            SetFooterStatus($"开机自启动设置失败：{error}", isError: true);
            return;
        }

        _preferences.StartWithWindows = enabled;
        _preferences.Save();
        SetFooterStatus(enabled ? "已开启开机自启动" : "已关闭开机自启动");
    }

    private void StickerTopmostCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        _preferences.StickerTopmost = StickerTopmostCheckBox.IsChecked == true;
        _preferences.Save();
        SetFooterStatus(_preferences.StickerTopmost ? "新建贴图将默认置顶" : "已关闭贴图默认置顶");
    }

    private void DesktopPetScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized)
        {
            return;
        }

        var scalePercent = Math.Clamp((int)Math.Round(e.NewValue / 5d) * 5, 60, 160);
        UpdateDesktopPetScaleText(scalePercent);
        if (_preferences.DesktopPetScalePercent != scalePercent)
        {
            _preferences.DesktopPetScalePercent = scalePercent;
            _preferences.Save();
        }
        _applyDesktopPetScale(scalePercent);
    }

    private void DesktopPetVisibleCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        var requested = DesktopPetVisibleCheckBox.IsChecked == true;
        var actual = _applyDesktopPetVisibility(requested);
        DesktopPetVisibleCheckBox.IsChecked = actual;
        _preferences.DesktopPetVisible = actual;
        _preferences.Save();
        SetFooterStatus(actual ? "桌面宠物已显示" : "桌面宠物已隐藏");
    }

    private void UpdateDesktopPetScaleText(int scalePercent)
    {
        var displaySize = (int)Math.Round(288 * scalePercent / 100d);
        DesktopPetScaleValueText.Text = $"{scalePercent}% · {displaySize} DIP";
    }

    private void VoiceInputEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        var message = _applyVoiceInputEnabled(VoiceInputEnabledCheckBox.IsChecked == true);
        VoiceInputEnabledCheckBox.IsChecked = _preferences.VoiceInputEnabled;
        SetFooterStatus(message, message.Contains("占用", StringComparison.Ordinal));
    }

    private void VoiceInputPasteAutomaticallyCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        _preferences.VoiceInputPasteAutomatically = VoiceInputPasteAutomaticallyCheckBox.IsChecked == true;
        _preferences.Save();
        SetFooterStatus(_preferences.VoiceInputPasteAutomatically ? "识别后将自动粘贴" : "识别后仅复制到剪贴板");
    }

    private void CollaborationAutoReconnectCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _preferences.CollaborationAutoReconnect = CollaborationAutoReconnectCheckBox.IsChecked == true;
        _preferences.Save();
        if (Application.Current?.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ApplyCollaborationAutoReconnect(_preferences.CollaborationAutoReconnect);
        }
        SetFooterStatus(_preferences.CollaborationAutoReconnect
            ? "已开启已信任手机自动连接"
            : "已关闭自动连接；仍可手动扫码连接");
    }

    private void ShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target } button)
        {
            return;
        }

        _editingShortcutTarget = target;
        button.Content = "请按下快捷键…";
        ShortcutStatusText.Text = "正在监听。按 Esc 取消；右 Ctrl 可作为全屏截图单键，右 Alt 可作为语音输入单键。";
        button.Focus();
    }

    private void ShortcutButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editingShortcutTarget is null)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key == Key.Escape)
        {
            _editingShortcutTarget = null;
            UpdateShortcutButtons();
            ShortcutStatusText.Text = "已取消快捷键修改。";
            return;
        }

        var candidate = key == Key.RightAlt && _editingShortcutTarget == "Voice"
            ? GlobalShortcut.VoiceDefault
            : key == Key.RightCtrl && _editingShortcutTarget == "FullScreen"
                ? GlobalShortcut.FullScreenDefault
                : GlobalShortcut.FromKey(key, Keyboard.Modifiers);
        var error = _applyShortcut(_editingShortcutTarget, candidate);
        if (error is null)
        {
            SetShortcut(_editingShortcutTarget, candidate);
            _editingShortcutTarget = null;
            UpdateShortcutButtons();
            ShortcutStatusText.Text = $"已设为 {candidate.DisplayText}，修改已立即生效。";
            SetFooterStatus("快捷键已更新");
            return;
        }

        UpdateShortcutButtons();
        ShortcutStatusText.Text = error;
        SetFooterStatus(error, isError: true);
    }

    private void SetShortcut(string target, GlobalShortcut shortcut)
    {
        if (target == "Screenshot") _screenshotShortcut = shortcut;
        else if (target == "FullScreen") _fullScreenShortcut = shortcut;
        else if (target == "Clipboard") _clipboardShortcut = shortcut;
        else _voiceInputShortcut = shortcut;
    }

    private void UpdateShortcutButtons()
    {
        ScreenshotShortcutButton.Content = _screenshotShortcut.DisplayText;
        FullScreenShortcutButton.Content = _fullScreenShortcut.DisplayText;
        ClipboardShortcutButton.Content = _clipboardShortcut.DisplayText;
        VoiceShortcutButton.Content = _voiceInputShortcut.DisplayText;
    }

    private async void StorageLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string category })
        {
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = $"选择{category}的保存位置",
            UseDescriptionForTitle = true,
            SelectedPath = GetStorageLocation(category)
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        SetStorageLocation(category, dialog.SelectedPath);
        _preferences.Save();
        UpdateStorageLocationText();
        await _refreshHistory();
        SetFooterStatus($"已更新{category}的存储位置");
    }

    private string GetStorageLocation(string category) => category switch
    {
        "截图" => _preferences.ScreenshotDirectory,
        "长截图" => _preferences.LongScreenshotDirectory,
        "文字提取" => _preferences.TextExtractionDirectory,
        "翻译" => _preferences.TranslationDirectory,
        "屏幕录制" => _preferences.RecordingDirectory,
        "外部复制" => _preferences.ClipboardDirectory,
        "协作接收" => _preferences.CollaborationIncomingDirectory,
        "协作发送" => _preferences.CollaborationOutgoingDirectory,
        _ => string.Empty
    };

    private void SetStorageLocation(string category, string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (category == "截图") _preferences.ScreenshotDirectory = fullPath;
        else if (category == "长截图") _preferences.LongScreenshotDirectory = fullPath;
        else if (category == "文字提取") _preferences.TextExtractionDirectory = fullPath;
        else if (category == "翻译") _preferences.TranslationDirectory = fullPath;
        else if (category == "屏幕录制") _preferences.RecordingDirectory = fullPath;
        else if (category == "外部复制") _preferences.ClipboardDirectory = fullPath;
        else if (category == "协作接收")
        {
            _preferences.CollaborationIncomingDirectory = fullPath;
            CollaborationService.Instance.IncomingDirectory = fullPath;
        }
        else if (category == "协作发送")
        {
            _preferences.CollaborationOutgoingDirectory = fullPath;
            CollaborationService.Instance.OutgoingDirectory = fullPath;
        }
    }

    private void UpdateStorageLocationText()
    {
        ScreenshotStoragePathText.Text = _preferences.ScreenshotDirectory;
        LongScreenshotStoragePathText.Text = _preferences.LongScreenshotDirectory;
        TextExtractionStoragePathText.Text = _preferences.TextExtractionDirectory;
        TranslationStoragePathText.Text = _preferences.TranslationDirectory;
        RecordingStoragePathText.Text = _preferences.RecordingDirectory;
        ClipboardStoragePathText.Text = _preferences.ClipboardDirectory;
        CollaborationIncomingPathText.Text = _preferences.CollaborationIncomingDirectory;
        CollaborationOutgoingPathText.Text = _preferences.CollaborationOutgoingDirectory;
    }

    private async Task RefreshNetworkAuthorizationStateAsync()
    {
        var registered = await Task.Run(NetworkEtwAutoStartService.IsRegistered);
        _preferences.NetworkEtwAutoStart = registered;
        if (!registered)
        {
            _preferences.Save();
        }
        NetworkEtwAuthorizationButton.Content = registered ? "取消授权" : "授权并自动获取";
        NetworkEtwAuthorizationStatusText.Text = registered
            ? "已授权：启动 X-Tool 时自动运行独立 ETW 辅助进程"
            : "尚未授权网络流量自动获取";
    }

    private async void NetworkEtwAuthorizationButton_Click(object sender, RoutedEventArgs e)
    {
        NetworkEtwAuthorizationButton.IsEnabled = false;
        try
        {
            var registered = await Task.Run(NetworkEtwAutoStartService.IsRegistered);
            if (registered)
            {
                _stopNetworkTraffic();
                var result = await NetworkEtwAutoStartService.UnregisterAsync();
                if (result.Success)
                {
                    _preferences.NetworkEtwAutoStart = false;
                    _preferences.Save();
                }
                SetFooterStatus(result.Message, !result.Success);
            }
            else
            {
                var result = await NetworkEtwAutoStartService.RegisterAsync();
                if (result.Success)
                {
                    _preferences.NetworkEtwAutoStart = true;
                    _preferences.Save();
                    var startResult = await _startNetworkTraffic();
                    if (!startResult.Success)
                    {
                        SetFooterStatus(startResult.Message, isError: true);
                    }
                }
                SetFooterStatus(result.Message, !result.Success);
            }
            await RefreshNetworkAuthorizationStateAsync();
        }
        catch (Exception exception)
        {
            SetFooterStatus($"网络流量授权操作失败：{exception.GetBaseException().Message}", isError: true);
            await RefreshNetworkAuthorizationStateAsync();
        }
        finally
        {
            NetworkEtwAuthorizationButton.IsEnabled = true;
        }
    }

    private void SetFooterStatus(string message, bool isError = false)
    {
        FooterStatusText.Text = message;
        FooterStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            isError
                ? System.Windows.Media.Color.FromRgb(215, 83, 93)
                : System.Windows.Media.Color.FromRgb(76, 126, 164));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _editingShortcutTarget is not null)
        {
            return;
        }
        e.Handled = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 连续点击标题区时可能发生拖动重入，忽略即可。
        }
    }
}
