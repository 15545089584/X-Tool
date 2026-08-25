using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ScreenshotApp.Capture;
using ScreenshotApp.Collaboration;

namespace ScreenshotApp.DesktopPet;

/// <summary>显示在桌面宠物头顶的非激活、鼠标穿透彩虹传输气泡。</summary>
public partial class PetTransferBubbleWindow : Window
{
    private bool _hasOpened;

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
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        UpdateLayout();
        ConfigureNativeWindow();
        if (!_hasOpened || Opacity < 1)
        {
            _hasOpened = true;
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
        NativeMethods.MakeWindowMouseTransparent(handle);
        NativeMethods.KeepWindowTopmostWithoutActivating(handle);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.00} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.0} MB",
        >= 1024L => $"{bytes / 1024d:0.0} KB",
        _ => $"{Math.Max(0, bytes)} B"
    };
}
