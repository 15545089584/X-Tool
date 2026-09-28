using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ScreenshotApp.Capture;
using ScreenshotApp.Collaboration;
using ScreenshotApp.Archives;

namespace ScreenshotApp.DesktopPet;

/// <summary>显示在桌面宠物头顶的非激活彩虹传输气泡；接收完成时可临时点击打开文件。</summary>
public partial class PetTransferBubbleWindow : Window
{
    internal const double PreferredWidth = 320;
    private double _messageWidth = PreferredWidth;

    private string? _openTransferId;
    private Action? _customClickAction;
    private string? _verificationCode;
    private Action? _mailReadAction;
    private bool _isMouseTransparent = true;
    private readonly AlarmTopmostPulse _topmostPulse;

    internal PetTransferBubbleWindow()
    {
        InitializeComponent();
        _topmostPulse = new AlarmTopmostPulse(RefreshTopmost, TimeSpan.FromMilliseconds(350));
        SourceInitialized += (_, _) => ConfigureNativeWindow();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _topmostPulse.Start();
            else _topmostPulse.Stop();
        };
        Closed += (_, _) => _topmostPulse.Dispose();
    }

    internal void ShowTransfer(CollaborationTransferProgress progress, double speedBytesPerSecond)
    {
        ConfigureOpenTarget(null);
        var isSending = progress.Direction.Equals("Send", StringComparison.OrdinalIgnoreCase);
        StatusIconText.Text = isSending ? "\uE724" : "\uE896";
        StatusIconText.Foreground = new SolidColorBrush(Color.FromRgb(77, 124, 254));
        TitleText.Text = progress.State switch
        {
            "Preparing" => "正在准备发送",
            "Queued" => "等待手机接收",
            _ => isSending ? "正在传到手机" : "正在接收手机文件"
        };
        FileNameText.Text = progress.FileName;
        var isQueued = progress.State == "Queued";
        var percentage = isQueued ? 0 : Math.Clamp(progress.Percentage, 0, 100);
        PercentageText.Text = isQueued ? "等待" : $"{percentage:0.0}%";
        PercentageText.Foreground = new SolidColorBrush(Color.FromRgb(77, 124, 254));
        TransferProgressBar.Value = percentage;
        TransferProgressBar.Visibility = Visibility.Visible;
        DetailRow.Visibility = Visibility.Visible;
        TransferredText.Text = isQueued
            ? $"{FormatSize(progress.TotalBytes)} · 等待手机开始接收"
            : $"{FormatSize(progress.TransferredBytes)} / {FormatSize(progress.TotalBytes)}";
        SpeedText.Text = isQueued
            ? "等待接收"
            : speedBytesPerSecond > 0
                ? $"{FormatSize((long)speedBytesPerSecond)}/s"
                : "准备中";
        ShowWithoutActivation();
    }

    internal void ShowCompletion(CollaborationTransferProgress progress)
    {
        var isSending = progress.Direction.Equals("Send", StringComparison.OrdinalIgnoreCase);
        var canOpenReceivedFile = !isSending &&
                                  CollaborationService.Instance.TryGetTransferOpenPath(progress.TransferId, out _);
        ConfigureOpenTarget(canOpenReceivedFile ? progress.TransferId : null);
        StatusIconText.Text = "\uE73E";
        StatusIconText.Foreground = new SolidColorBrush(Color.FromRgb(53, 172, 113));
        TitleText.Text = isSending ? "已传到手机" : "已收到手机文件";
        FileNameText.Text = progress.FileName;
        PercentageText.Text = "完成";
        PercentageText.Foreground = new SolidColorBrush(Color.FromRgb(53, 154, 105));
        TransferProgressBar.Value = 100;
        TransferProgressBar.Visibility = Visibility.Visible;
        DetailRow.Visibility = Visibility.Visible;
        TransferredText.Text = FormatSize(Math.Max(progress.TotalBytes, progress.TransferredBytes));
        SpeedText.Text = canOpenReceivedFile ? "单击打开" : "传输完成";
        ShowWithoutActivation();
    }

    internal void ShowFailure(CollaborationTransferProgress progress)
    {
        ConfigureOpenTarget(null);
        StatusIconText.Text = "\uE711";
        StatusIconText.Foreground = new SolidColorBrush(Color.FromRgb(220, 86, 100));
        TitleText.Text = "文件传输失败";
        FileNameText.Text = progress.FileName;
        PercentageText.Text = "失败";
        PercentageText.Foreground = new SolidColorBrush(Color.FromRgb(210, 77, 91));
        TransferProgressBar.Visibility = Visibility.Collapsed;
        DetailRow.Visibility = Visibility.Visible;
        TransferredText.Text = progress.Message;
        SpeedText.Text = string.Empty;
        ShowWithoutActivation();
    }

    internal void ShowArchiveProgress(
        PetArchiveDropAction action,
        string sourceName,
        ArchiveProgressInfo? progress = null)
    {
        ConfigureOpenTarget(null);
        var isExtracting = action == PetArchiveDropAction.Extract;
        StatusIconText.Text = isExtracting ? "\uE7C5" : "\uE7B8";
        StatusIconText.Foreground = new SolidColorBrush(Color.FromRgb(214, 132, 31));
        TitleText.Text = progress is null
            ? isExtracting ? "正在准备解压" : "正在准备压缩"
            : isExtracting ? "正在解压文件" : "正在压缩文件";
        FileNameText.Text = string.IsNullOrWhiteSpace(progress?.CurrentEntry)
            ? sourceName
            : progress.CurrentEntry;
        var percentage = Math.Clamp(progress?.Percent ?? 0, 0, 100);
        PercentageText.Text = progress is null ? "准备" : $"{percentage:0.0}%";
        PercentageText.Foreground = new SolidColorBrush(Color.FromRgb(214, 132, 31));
        TransferProgressBar.Value = percentage;
        TransferProgressBar.Visibility = Visibility.Visible;
        DetailRow.Visibility = Visibility.Visible;
        TransferredText.Text = progress is null || progress.TotalBytes <= 0
            ? "正在扫描文件与计算大小"
            : $"{FormatSize(progress.ProcessedBytes)} / {FormatSize(progress.TotalBytes)}";
        SpeedText.Text = "本地处理";
        ShowWithoutActivation();
    }

    internal void ShowArchiveCompletion(
        PetArchiveDropAction action,
        string resultName,
        string resultPath,
        long processedBytes)
    {
        ConfigureCustomAction(() => OpenLocalPath(resultPath), "单击打开结果位置");
        var isExtracting = action == PetArchiveDropAction.Extract;
        StatusIconText.Text = "\uE73E";
        StatusIconText.Foreground = new SolidColorBrush(Color.FromRgb(53, 172, 113));
        TitleText.Text = isExtracting ? "解压完成" : "压缩完成";
        FileNameText.Text = resultName;
        PercentageText.Text = "完成";
        PercentageText.Foreground = new SolidColorBrush(Color.FromRgb(53, 154, 105));
        TransferProgressBar.Value = 100;
        TransferProgressBar.Visibility = Visibility.Visible;
        DetailRow.Visibility = Visibility.Visible;
        TransferredText.Text = processedBytes > 0 ? FormatSize(processedBytes) : "本地任务已完成";
        SpeedText.Text = "单击打开位置";
        ShowWithoutActivation();
    }

    internal void ShowArchiveFailure(PetArchiveDropAction action, string sourceName, string message)
    {
        ConfigureOpenTarget(null);
        StatusIconText.Text = "\uE711";
        StatusIconText.Foreground = new SolidColorBrush(Color.FromRgb(220, 86, 100));
        TitleText.Text = action == PetArchiveDropAction.Extract ? "解压失败" : "压缩失败";
        FileNameText.Text = sourceName;
        PercentageText.Text = "失败";
        PercentageText.Foreground = new SolidColorBrush(Color.FromRgb(210, 77, 91));
        TransferProgressBar.Visibility = Visibility.Collapsed;
        DetailRow.Visibility = Visibility.Visible;
        TransferredText.Text = message;
        SpeedText.Text = string.Empty;
        ShowWithoutActivation();
    }

    internal void ShowShelfMessage(string title, string detail, string actionText, Action clickAction)
    {
        ConfigureCustomAction(clickAction, "单击打开文件暂存区");
        StatusIconText.Text = "\uE8B7";
        StatusIconText.Foreground = new SolidColorBrush(Color.FromRgb(104, 87, 216));
        TitleText.Text = title;
        FileNameText.Text = detail;
        PercentageText.Text = "暂存";
        PercentageText.Foreground = new SolidColorBrush(Color.FromRgb(104, 87, 216));
        TransferProgressBar.Visibility = Visibility.Collapsed;
        DetailRow.Visibility = Visibility.Visible;
        TransferredText.Text = "仅本次运行保留";
        SpeedText.Text = actionText;
        ShowWithoutActivation();
    }

    internal void ShowPhoneMessage(string title, string detail, Action clickAction, string? avatar = null, bool preview = false, PhoneVerificationCode? verificationCode = null)
    {
        _messageWidth = 440;
        TitleText.FontSize = 18;
        FileNameText.FontSize = 16; FileNameText.MaxWidth = 380;
        PercentageText.FontSize = 14; TransferredText.FontSize = 12; SpeedText.FontSize = 12;
        StatusIconColumn.Width = new GridLength(52);
        StatusIconBorder.BorderThickness = new Thickness(0);
        StatusIconBorder.Width = StatusIconBorder.Height = 44; StatusIconBorder.CornerRadius = new CornerRadius(22);
        StatusIconText.FontSize = 22;
        AvatarImage.Source = ScreenshotApp.Collaboration.PhoneNotificationImage.Decode(avatar);
        AvatarImage.Visibility = AvatarImage.Source is null ? Visibility.Collapsed : Visibility.Visible;
        StatusIconText.Visibility = AvatarImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        ConfigureCustomAction(clickAction, "打开手机通知");
        StatusIconText.Text = "\uE8F2";
        StatusIconText.Foreground = new SolidColorBrush(Color.FromRgb(77, 124, 254));
        TitleText.Text = title;
        FileNameText.Text = detail;
        FileNameText.TextWrapping = TextWrapping.Wrap;
        FileNameText.MaxHeight = 140;
        PercentageText.Text = "消息";
        TransferProgressBar.Visibility = Visibility.Collapsed;
        DetailRow.Visibility = Visibility.Visible;
        TransferredText.Text = "来自你的手机";
        SpeedText.Text = "单击查看";
        FileNameText.FontFamily = TitleText.FontFamily;
        if (verificationCode is not null)
        {
            _verificationCode = verificationCode.Code;
            TitleText.Text = verificationCode.Sender;
            FileNameText.Text = verificationCode.Code;
            FileNameText.FontFamily = new FontFamily("Consolas");
            FileNameText.FontSize = 34;
            PercentageText.Text = "验证码";
            DetailRow.Visibility = Visibility.Collapsed;
            CopyCodeButton.Visibility = Visibility.Visible;
            CopyCodeText.Text = "复制验证码";
        }
        if (!preview) ShowWithoutActivation();
        else { MinWidth = MaxWidth = Width = _messageWidth; }
    }

    internal void ShowMailMessage(string title, string detail, Action open, Action? markRead, string? logo = null)
    {
        ShowPhoneMessage(title, detail, open, preview: true);
        if (logo is not null)
        {
            AvatarImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(logo));
            AvatarImage.Visibility = Visibility.Visible;
            StatusIconText.Visibility = Visibility.Collapsed;
            StatusIconBorder.Background = Brushes.Transparent;
        }
        BubbleCard.ToolTip = "打开邮箱中心";
        PercentageText.Text = "邮箱";
        TransferredText.Text = "直接接收邮箱邮件";
        _mailReadAction = markRead;
        MailActions.Visibility = Visibility.Visible;
        MailReadButton.Visibility = markRead is null ? Visibility.Collapsed : Visibility.Visible;
        ShowWithoutActivation();
    }
    private void MailOpen_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; _customClickAction?.Invoke();
    }
    private void MailRead_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; _mailReadAction?.Invoke();
    }

    internal void ShowAlarmMessage(PetAlarmTrigger trigger, Action? dismissAction = null)
    {
        if (trigger.IsDue && dismissAction is not null)
        {
            ConfigureCustomAction(dismissAction, "单击结束本次闹钟提醒");
        }
        else
        {
            ConfigureOpenTarget(null);
        }
        StatusIconText.Text = "\uE823";
        StatusIconText.Foreground = new SolidColorBrush(Color.FromRgb(77, 124, 254));
        TitleText.Text = trigger.Title;
        FileNameText.Text = trigger.Detail;
        PercentageText.Text = trigger.IsDue ? "到点" : "提前";
        PercentageText.Foreground = new SolidColorBrush(Color.FromRgb(104, 87, 216));
        TransferProgressBar.Visibility = Visibility.Collapsed;
        DetailRow.Visibility = Visibility.Visible;
        TransferredText.Text = "静默消息提醒";
        SpeedText.Text = trigger.IsDue ? "单击结束提醒" : "约 5 秒后关闭";
        ShowWithoutActivation();
    }

    internal void RefreshTopmost()
    {
        if (!IsVisible)
        {
            return;
        }

        NativeMethods.KeepWindowTopmostWithoutActivating(new WindowInteropHelper(this).Handle);
    }

    /// <summary>让气泡尾部始终指向宠物实际锚点，而不是被夹紧后的气泡中心。</summary>
    internal void SetTailAnchor(double anchorXInWindow)
    {
        var contentWidth = Math.Max(1, (ActualWidth > 0 ? ActualWidth : Width) - 20);
        var anchorInContent = anchorXInWindow - 10;
        var left = Math.Clamp(anchorInContent - 9, 24, Math.Max(24, contentWidth - 42));
        TailArrow.Margin = new Thickness(left, -1, 0, 0);
    }

    private void ShowWithoutActivation()
    {
        // WPF 的透明无边框窗口在 Hide/Show 与高 DPI 混用时，偶尔会把原生 HWND 宽度保留为 1 像素。
        // 每次显示前重新声明固定宽度，避免事件正常到达但气泡在屏幕边缘不可见。
        MinWidth = _messageWidth;
        MaxWidth = _messageWidth;
        Width = _messageWidth;
        var isNewlyShown = !IsVisible;
        if (isNewlyShown)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Show();
        }

        UpdateLayout();
        Width = _messageWidth;
        ConfigureNativeWindow();
        if (isNewlyShown)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private void ConfigureNativeWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.MakeWindowNonActivating(handle);
        NativeMethods.SetWindowMouseTransparent(handle, _isMouseTransparent);
        NativeMethods.KeepWindowTopmostWithoutActivating(handle);
    }

    private void ConfigureOpenTarget(string? transferId)
    {
        ResetVerificationCode();
        _openTransferId = transferId;
        _customClickAction = null;
        _isMouseTransparent = string.IsNullOrWhiteSpace(transferId);
        BubbleCard.Cursor = _isMouseTransparent ? Cursors.Arrow : Cursors.Hand;
        BubbleCard.ToolTip = _isMouseTransparent ? null : "单击打开接收到的文件";
        ConfigureNativeWindow();
    }

    private void ConfigureCustomAction(Action clickAction, string toolTip)
    {
        ResetVerificationCode();
        _openTransferId = null;
        _customClickAction = clickAction;
        _isMouseTransparent = false;
        BubbleCard.Cursor = Cursors.Hand;
        BubbleCard.ToolTip = toolTip;
        ConfigureNativeWindow();
    }

    internal void ResetVerificationCode()
    {
        _verificationCode = null;
        _mailReadAction = null;
        MailActions.Visibility = Visibility.Collapsed;
        CopyCodeButton.Visibility = Visibility.Collapsed;
    }

    private void CopyCodeButton_Click(object sender, RoutedEventArgs e)
    {
        // 消耗按钮事件，复制动作不再触发整个气泡的“打开手机通知”。
        e.Handled = true;
        if (_verificationCode is null) return;
        var hub = PhoneNotificationHub.Instance;
        if (hub.Locked || hub.Paused || !hub.AlertsEnabled ||
            !ScreenshotApp.Settings.AppPreferences.Load().PhoneNotificationPreviewEnabled)
        {
            ResetVerificationCode();
            Hide();
            return;
        }
        try
        {
            ScreenshotApp.ClipboardUi.ClipboardService.SetSensitiveText(_verificationCode);
            CopyCodeText.Text = "已复制";
        }
        catch
        {
            CopyCodeText.Text = "复制失败，点击重试";
        }
    }

    private void BubbleCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 按钮自行处理鼠标和键盘复制，不能冒泡为卡片导航。
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != BubbleCard;)
        {
            if (source is System.Windows.Controls.Button) return;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        if (_customClickAction is { } customClickAction)
        {
            e.Handled = true;
            customClickAction();
            return;
        }

        var transferId = _openTransferId;
        if (string.IsNullOrWhiteSpace(transferId))
        {
            return;
        }

        e.Handled = true;
        if (!CollaborationService.Instance.TryGetTransferOpenPath(transferId, out var path))
        {
            SpeedText.Text = "文件已移动或删除";
            ConfigureOpenTarget(null);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                SpeedText.Text = "无法打开文件";
            }
        }
    }

    private static void OpenLocalPath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                startInfo.ArgumentList.Add($"/select,{path}");
                Process.Start(startInfo);
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch
        {
            // 结果可能已被移动或删除；完成气泡无需因此影响主任务状态。
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.00} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.0} MB",
        >= 1024L => $"{bytes / 1024d:0.0} KB",
        _ => $"{Math.Max(0, bytes)} B"
    };
}
