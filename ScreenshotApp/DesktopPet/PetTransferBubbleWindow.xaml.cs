using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ScreenshotApp.Capture;
using ScreenshotApp.Collaboration;

namespace ScreenshotApp.DesktopPet;

/// <summary>显示在桌面宠物头顶的非激活、鼠标穿透彩虹传输气泡。</summary>
public partial class PetTransferBubbleWindow : Window
{
    internal const double PreferredWidth = 320;
    private double _messageWidth = PreferredWidth;
    private Action? _customClickAction;
    private string? _verificationCode;
    private bool _isMouseTransparent = true;

    internal PetTransferBubbleWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ConfigureNativeWindow();
    }

    internal void ShowTransfer(CollaborationTransferProgress progress, double speedBytesPerSecond)
    {
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
        SpeedText.Text = "传输完成";
        ShowWithoutActivation();
    }

    internal void ShowFailure(CollaborationTransferProgress progress)
    {
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

    private void ConfigureCustomAction(Action clickAction, string toolTip)
    {
        ResetVerificationCode();
        _customClickAction = clickAction;
        _isMouseTransparent = false;
        BubbleCard.Cursor = Cursors.Hand;
        BubbleCard.ToolTip = toolTip;
        ConfigureNativeWindow();
    }

    internal void ResetVerificationCode()
    {
        _verificationCode = null;
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

    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.00} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.0} MB",
        >= 1024L => $"{bytes / 1024d:0.0} KB",
        _ => $"{Math.Max(0, bytes)} B"
    };
}
