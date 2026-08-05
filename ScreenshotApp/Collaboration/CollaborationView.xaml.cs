using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenshotApp.ClipboardUi;
using ScreenshotApp.Converters;
using ZXing.QrCode.Internal;

namespace ScreenshotApp.Collaboration;

/// <summary>协作中心页面：局域网配对、剪贴板桥与文件传输状态。</summary>
public partial class CollaborationView : UserControl
{
    private readonly DispatcherTimer _statusTimer;

    public CollaborationView()
    {
        InitializeComponent();
        IncomingPathText.Text = CollaborationService.Instance.IncomingDirectory;
        OutgoingPathText.Text = CollaborationService.Instance.OutgoingDirectory;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        RefreshStatus();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshStatus();
            }
        };
    }

    private void StartService_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var baseUrl = CollaborationService.Instance.Start();
            UpdateServiceUi();
            ClipboardStateText.Text = "服务已启动，电脑复制内容将自动同步到手机页面";
        }
        catch (Exception exception)
        {
            ClipboardStateText.Text = "启动失败：" + exception.Message;
        }
    }

    private void StopService_Click(object sender, RoutedEventArgs e)
    {
        CollaborationService.Instance.Stop();
        UpdateServiceUi();
        ClipboardStateText.Text = "服务已停止";
    }

    private void RegeneratePin_Click(object sender, RoutedEventArgs e)
    {
        CollaborationService.Instance.RegeneratePin();
        RefreshStatus();
        ClipboardStateText.Text = "已更换配对 PIN，旧 PIN 立即失效";
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var service = CollaborationService.Instance;
        if (!service.IsRunning)
        {
            return;
        }
        try
        {
            ClipboardService.SetText($"http://{service.LocalIpAddress}:{service.Port}/pair?pin={service.Pin}");
            ClipboardStateText.Text = "配对地址已复制到剪贴板";
        }
        catch
        {
            ClipboardStateText.Text = "复制失败，请重试";
        }
    }

    private void ClipboardBridge_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        CollaborationService.Instance.ClipboardBridgeEnabled = ClipboardBridgeCheckBox.IsChecked == true;
        RefreshStatus();
    }

    private void AllowFirewall_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        var message = "将在防火墙中放行 TCP 入站端口 " + CollaborationService.DefaultPort +
            "（需要管理员权限，仅用于局域网手机连接）。" + Environment.NewLine +
            "如不确定或不在可信局域网，可取消并保持不放行。";
        if (MessageBox.Show(window, message, "放行防火墙", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(
                "netsh.exe",
                $"advfirewall firewall add rule name=\"X-Tool 协作中心 {CollaborationService.DefaultPort}\" dir=in action=allow protocol=TCP localport={CollaborationService.DefaultPort}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            ClipboardStateText.Text = "已在系统弹窗确认后放行端口；手机即可连接";
        }
        catch
        {
            ClipboardStateText.Text = "放行被取消或未获得管理员权限";
        }
    }

    private void OpenIncoming_Click(object sender, RoutedEventArgs e) => OpenDirectory(CollaborationService.Instance.IncomingDirectory);

    private void OpenOutgoing_Click(object sender, RoutedEventArgs e) => OpenDirectory(CollaborationService.Instance.OutgoingDirectory);

    private static void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // 打开失败时保持现状。
        }
    }

    private void SendFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择发送到手机的文件",
            Multiselect = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(CollaborationService.Instance.OutgoingDirectory);
            var copied = 0;
            foreach (var file in dialog.FileNames)
            {
                var target = UniquePath(Path.Combine(CollaborationService.Instance.OutgoingDirectory, Path.GetFileName(file)));
                File.Copy(file, target);
                copied++;
            }
            TransferStateText.Text = $"已放入发送目录 {copied} 个文件，手机端可在页面内下载";
        }
        catch (Exception exception)
        {
            TransferStateText.Text = "发送失败：" + exception.Message;
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name}_{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private void RefreshStatus()
    {
        var service = CollaborationService.Instance;
        var running = service.IsRunning;
        ServiceStateText.Text = running ? "服务运行中" : "服务未启动";
        ServiceStateText.Foreground = new SolidColorBrush(running ? Color.FromRgb(22, 185, 155) : Color.FromRgb(74, 107, 140));
        StatusDot.Background = new SolidColorBrush(running ? Color.FromRgb(22, 185, 155) : Color.FromRgb(154, 169, 184));
        StartServiceButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopServiceButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RegeneratePinButton.IsEnabled = running;
        AllowFirewallButton.IsEnabled = running;
        CopyUrlButton.IsEnabled = running;
        if (!running)
        {
            PinText.Text = "—";
            PairUrlText.Text = "启动后生成配对地址";
            ManualUrlText.Text = "—";
            QrEmptyText.Visibility = Visibility.Visible;
            PairQrImage.Source = null;
            SessionText.Text = "等待配对";
            SessionText.Foreground = new SolidColorBrush(Color.FromRgb(123, 147, 168));
            ClipboardStateText.Text = "等待服务启动";
            RecentSyncImage.Visibility = Visibility.Collapsed;
            RecentSyncTimeText.Text = string.Empty;
            SyncCountText.Text = string.Empty;
            return;
        }

        PinText.Text = service.Pin;
        var pairUrl = $"http://{service.LocalIpAddress}:{service.Port}/pair?pin={service.Pin}";
        PairUrlText.Text = pairUrl.Replace("http://", string.Empty);
        ManualUrlText.Text = pairUrl;
        SessionText.Foreground = new SolidColorBrush(service.SessionCount > 0 ? Color.FromRgb(22, 185, 155) : Color.FromRgb(123, 147, 168));
        SessionText.Text = service.SessionCount > 0 ? $"● {service.SessionCount} 台已连接" : "等待手机扫码";
        RefreshQrCode(pairUrl);
        UpdateRecentSync(service);
    }

    private void UpdateRecentSync(CollaborationService service)
    {
        var entry = service.LastClipboardEntry;
        if (entry == null)
        {
            ClipboardStateText.Text = ClipboardBridgeCheckBox.IsChecked == true
                ? $"桥接运行中 · 已同步 {service.ClipboardSeq} 条"
                : "电脑→手机同步已关闭，手机→电脑仍可用";
            SyncCountText.Text = $"累计同步 {service.ClipboardSeq} 条";
            RecentSyncImage.Visibility = Visibility.Collapsed;
            RecentSyncTimeText.Text = string.Empty;
            return;
        }

        RecentSyncTimeText.Text = $"第 {entry.Seq} 条 · {entry.CreatedAt:HH:mm:ss}";
        SyncCountText.Text = $"累计同步 {service.ClipboardSeq} 条";
        if (entry.Kind == "image" && entry.ImagePngBase64.Length > 0)
        {
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(entry.ImagePngBase64));
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                RecentSyncImage.Source = image;
                RecentSyncImage.Visibility = Visibility.Visible;
                ClipboardStateText.Text = "最近同步：图片";
                return;
            }
            catch
            {
                // 图片解码失败时按文本方式处理。
            }
        }
        RecentSyncImage.Visibility = Visibility.Collapsed;
        var text = entry.Text;
        ClipboardStateText.Text = text.Length > 120 ? text.Substring(0, 120) + "…" : text;
    }

    private void RefreshQrCode(string content)
    {
        try
        {
            var bitmap = QrCodeService.Generate(content, 256, 2, ErrorCorrectionLevel.M, Colors.Black, Colors.White);
            PairQrImage.Source = bitmap;
            QrEmptyText.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // 二维码生成失败时保留占位提示。
        }
    }

    private void UpdateServiceUi() => RefreshStatus();
}
