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
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) =>
        {
            RefreshStatus();
            RefreshFiles();
        };
        _statusTimer.Start();
        RefreshStatus();
        RefreshFiles();
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

    /// <summary>跳转到设置页的协作中心区块，收发目录在那里统一调整。</summary>
    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.OpenCollaborationSettings();
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

    private void FileDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FileDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(CollaborationService.Instance.OutgoingDirectory);
            var copied = 0;
            foreach (var file in files)
            {
                if (!File.Exists(file))
                {
                    continue;
                }
                var target = UniquePath(Path.Combine(CollaborationService.Instance.OutgoingDirectory, Path.GetFileName(file)));
                File.Copy(file, target);
                copied++;
            }
            TransferStateText.Text = $"已拖入发送目录 {copied} 个文件，手机端自动下载";
            RefreshFiles();
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
                : "电脑→手机同步已关闭";
            return;
        }

        var summary = entry.Kind == "image" ? "图片" : (entry.Text.Length > 40 ? entry.Text.Substring(0, 40) + "…" : entry.Text);
        ClipboardStateText.Text = $"最近：{summary} · 共 {service.ClipboardSeq} 条";
    }

    /// <summary>展示两个收发目录最近的文件列表。</summary>
    private void RefreshFiles()
    {
        FillFileList(IncomingFilesList, CollaborationService.Instance.IncomingDirectory);
        FillFileList(OutgoingFilesList, CollaborationService.Instance.OutgoingDirectory);
    }

    private static void FillFileList(StackPanel panel, string directory)
    {
        panel.Children.Clear();
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }
            var files = Directory.EnumerateFiles(directory)
                .Select(file => new FileInfo(file))
                .OrderByDescending(info => info.LastWriteTime)
                .Take(10);
            foreach (var info in files)
            {
                var name = new TextBlock { Text = info.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(64, 95, 124)), TextTrimming = TextTrimming.CharacterEllipsis };
                var detail = new TextBlock
                {
                    Text = $"{FormatSize(info.Length)} · {info.LastWriteTime:MM-dd HH:mm}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(123, 147, 168))
                };
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition());
                grid.RowDefinitions.Add(new RowDefinition());
                Grid.SetRow(detail, 1);
                grid.Children.Add(name);
                grid.Children.Add(detail);
                panel.Children.Add(new Border
                {
                    Margin = new Thickness(0, 0, 0, 6),
                    Padding = new Thickness(10, 7, 10, 7),
                    Background = new SolidColorBrush(Color.FromArgb(239, 239, 245, 251)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(211, 228, 241)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Child = grid
                });
            }
        }
        catch
        {
            // 目录读取失败时保持列表为空。
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024 / 1024:0.0} GB";
        }
        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / 1024.0 / 1024:0.0} MB";
        }
        return $"{bytes / 1024.0:0.0} KB";
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
