using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenshotApp.Shortcuts;

/// <summary>
/// 快捷键设置弹框：负责捕获按键与展示状态，校验和热键注册由主窗口回调完成。
/// </summary>
public partial class ShortcutSettingsWindow : Window
{
    private GlobalShortcut _screenshotShortcut;
    private GlobalShortcut _clipboardShortcut;
    private GlobalShortcut _voiceInputShortcut;
    private string? _editingTarget;

    /// <summary>由主窗口注入；返回 null 表示应用成功，否则返回失败原因。</summary>
    internal Func<string, GlobalShortcut, string?>? ApplyShortcutRequested { get; set; }

    internal ShortcutSettingsWindow(GlobalShortcut screenshot, GlobalShortcut clipboard, GlobalShortcut voice)
    {
        InitializeComponent();
        _screenshotShortcut = screenshot;
        _clipboardShortcut = clipboard;
        _voiceInputShortcut = voice;
        UpdateButtons();
    }

    private void ShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string target)
        {
            return;
        }

        _editingTarget = target;
        button.Content = "请按下快捷键…";
        ShortcutCaptureStatusText.Text = "正在监听。按 Esc 取消；右 Alt 仅可用于本地语音输入。";
        button.Focus();
    }

    private void ShortcutButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editingTarget is null)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key == Key.Escape)
        {
            _editingTarget = null;
            UpdateButtons();
            ShortcutCaptureStatusText.Text = "已取消修改。";
            return;
        }

        var candidate = key == Key.RightAlt && _editingTarget == "Voice"
            ? GlobalShortcut.VoiceDefault
            : GlobalShortcut.FromKey(key, Keyboard.Modifiers);

        var error = ApplyShortcutRequested?.Invoke(_editingTarget, candidate);
        if (error is null)
        {
            SetShortcut(_editingTarget, candidate);
            _editingTarget = null;
            UpdateButtons();
            ShortcutCaptureStatusText.Text = $"已设为 {candidate.DisplayText}，未发现应用内或已注册的系统级冲突。";
        }
        else
        {
            UpdateButtons();
            ShortcutCaptureStatusText.Text = error;
        }
    }

    private void SetShortcut(string target, GlobalShortcut shortcut)
    {
        if (target == "Screenshot") _screenshotShortcut = shortcut;
        else if (target == "Clipboard") _clipboardShortcut = shortcut;
        else _voiceInputShortcut = shortcut;
    }

    private void UpdateButtons()
    {
        ScreenshotShortcutButton.Content = _screenshotShortcut.DisplayText;
        ClipboardShortcutButton.Content = _clipboardShortcut.DisplayText;
        VoiceShortcutButton.Content = _voiceInputShortcut.DisplayText;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
        catch
        {
            // 拖动过程中可能触发重入，忽略即可。
        }
    }
}
