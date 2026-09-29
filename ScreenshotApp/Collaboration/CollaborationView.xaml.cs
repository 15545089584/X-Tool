using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace ScreenshotApp.Collaboration;

/// <summary>重新设计的协作中心：配对状态、文件投递与双向传输进度。</summary>
public partial class CollaborationView : UserControl
{
    private readonly CollaborationService _service = CollaborationService.Instance;
    private readonly Dictionary<string, TransferRow> _transferById = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _statusTimer;
    private bool _autoReconnectEnabled = true;

    public CollaborationView()
    {
        InitializeComponent();
        DataContext = this;
        _service.TransferProgressChanged += Service_TransferProgressChanged;
        _service.DeviceStateChanged += Service_DeviceStateChanged;
        _service.RemoteFiles.Changed += Service_RemoteFilesChanged;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        Loaded += CollaborationView_Loaded;
        Unloaded += (_, _) => { _statusTimer.Stop(); PhoneNotificationHub.Instance.Changed -= NotificationPairingChanged; };
    }

    public ObservableCollection<TransferRow> Transfers { get; } = [];

    public event Action? ConnectionSettingsRequested;

    private void OpenPhoneNotifications_Click(object sender, RoutedEventArgs e) => PhoneNotificationsWindow.Open(Window.GetWindow(this));
    private void OpenPhoneCalendar_Click(object sender, RoutedEventArgs e) => ScreenshotApp.PhoneCalendar.PhoneCalendarWindow.Open();

    public void Configure(bool autoReconnectEnabled)
    {
        _autoReconnectEnabled = autoReconnectEnabled;
        RefreshStatus();
    }

    private void CollaborationView_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureServiceStarted();
        PhoneNotificationHub.Instance.Changed -= NotificationPairingChanged;
        PhoneNotificationHub.Instance.Changed += NotificationPairingChanged;
        RefreshPairingVisual();
        RefreshStatus();
        _statusTimer.Start();
    }

    private void EnsureServiceStarted()
    {
        if (_service.IsRunning) return;
        try
        {
            _service.Start();
        }
        catch (Exception exception)
        {
            HeaderStatusText.Text = $"服务启动失败：{exception.GetBaseException().Message}";
            HeaderStatusDot.Fill = new SolidColorBrush(Color.FromRgb(224, 91, 103));
        }
    }

    private void RefreshStatus()
    {
        if (!_service.IsRunning)
        {
            HeaderStatusText.Text = "连接服务未启动";
            HeaderStatusDot.Fill = new SolidColorBrush(Color.FromRgb(224, 91, 103));
            ConnectionLineText.Text = "当前不可连接";
            LocalIpText.Text = "地址不可用";
            TailscaleIpText.Text = "Tailscale 未连接";
            DeviceIpText.Text = "未连接";
            return;
        }

        var count = _service.SessionCount;
        var connectedDevice = _service.ConnectedDevices.FirstOrDefault();
        var remoteFileStatus = _service.RemoteFiles.Enabled
            ? $"远程文件 {_service.RemoteFiles.Roots.Count} 个目录"
            : "远程文件已关闭";
        EndpointText.Text = $"{Environment.MachineName} · 端口 {_service.Port} · {remoteFileStatus}";
        LocalIpText.Text = _service.LocalIpAddress ?? "地址待确认";
        TailscaleIpText.Text = string.IsNullOrWhiteSpace(_service.TailscaleIpAddress)
            ? "Tailscale 未连接"
            : $"TS {_service.TailscaleIpAddress}";
        HeaderStatusDot.Fill = new SolidColorBrush(count > 0 ? Color.FromRgb(31, 184, 143) : Color.FromRgb(77, 124, 254));
        HeaderStatusText.Text = count > 0 ? $"已连接 {count} 台设备" : "等待局域网或 Tailscale 设备";
        ConnectionLineText.Text = count > 0 ? "可信会话已连接" : _autoReconnectEnabled ? "已开启地址回退与自动重连" : "等待扫码连接";
        DeviceNameText.Text = connectedDevice?.DeviceName ?? "Vivo X300 PRO";
        DeviceIpText.Text = connectedDevice?.IpAddress ?? "等待连接";
        DeviceHintText.Text = count > 0
            ? "可开始双向传输"
            : _autoReconnectEnabled ? "已信任设备会自动连接" : "请扫描右侧配对码";

        var queued = _service.PendingOutgoingCount;
        QueueStatusText.Text = queued == 0 ? "发送队列为空" : $"{queued} 个文件等待手机接收";
    }

    private string? _notificationPairingPayload;
    private void NotificationPairingChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (IsLoaded) RefreshPairingVisual();
    });
    private void RefreshPairingVisual()
    {
        if (!_service.IsRunning) return;
        PairingPinText.Text = _service.Pin;
        var host = _service.LocalIpAddress;
        if (string.IsNullOrWhiteSpace(host)) { PairingQrImage.Source = null; return; }
        try
        {
            var payload = PhoneNotificationHub.Instance.CollaborationPairingPayload(
                host, _service.Port, _service.Pin, _service.ServerId);
            if (_notificationPairingPayload == payload && PairingQrImage.Source is not null) return;
            _notificationPairingPayload = payload;
            var writer = new BarcodeWriterPixelData
            {
                Format = BarcodeFormat.QR_CODE,
                Options = new QrCodeEncodingOptions
                {
                    Height = 256,
                    Width = 256,
                    Margin = 1,
                    CharacterSet = "UTF-8",
                    ErrorCorrection = ZXing.QrCode.Internal.ErrorCorrectionLevel.M
                }
            };
            var pixelData = writer.Write(payload);
            var bitmap = BitmapSource.Create(pixelData.Width, pixelData.Height, 96, 96,
                PixelFormats.Bgra32, null, pixelData.Pixels, pixelData.Width * 4);
            bitmap.Freeze();
            PairingQrImage.Source = bitmap;
        }
        catch
        {
            PairingQrImage.Source = null;
        }
    }

    private void RefreshPairing_Click(object sender, RoutedEventArgs e)
    {
        EnsureServiceStarted();
        _service.RegeneratePin();
        RefreshPairingVisual();
    }

    private async void SelectFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要发送到手机的文件",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true) await QueueFilesAsync(dialog.FileNames);
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            DropTitleText.Text = "暂不支持这种拖拽内容";
            DropHintText.Text = "当前支持本地文件与图片";
            return;
        }
        await QueueFilesAsync(paths.Where(File.Exists));
    }

    private void Root_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(e.Effects == DragDropEffects.Copy);
        e.Handled = true;
    }

    private void Root_DragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    private void SetDropHighlight(bool active)
    {
        DropZoneOutline.Stroke = new SolidColorBrush(active ? Color.FromRgb(77, 124, 254) : Color.FromRgb(103, 155, 213));
        DropZoneOutline.StrokeThickness = active ? 3.2 : 2.4;
        DropZoneOutline.Fill = new SolidColorBrush(active
            ? Color.FromArgb(50, 77, 124, 254)
            : Color.FromArgb(42, 255, 255, 255));
        DropZoneOutline.Effect = active
            ? new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(77, 124, 254),
                BlurRadius = 34,
                ShadowDepth = 0,
                Opacity = 0.72
            }
            : null;
        DropGlyph.Effect = active
            ? new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(92, 145, 255),
                BlurRadius = 24,
                ShadowDepth = 0,
                Opacity = 0.6
            }
            : null;
        DropGlyph.RenderTransformOrigin = new Point(0.5, 0.5);
        DropGlyph.RenderTransform = new ScaleTransform(active ? 1.08 : 1, active ? 1.08 : 1);
        DropTitleText.Text = active ? "松手加入发送队列" : "把内容交给 X-Tool";
        DropHintText.Text = active ? "原文件会保留在当前位置" : "支持多选，单文件最大 5 GB";
    }

    private async Task QueueFilesAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;
        DropTitleText.Text = $"正在准备 {files.Length} 个文件";
        try
        {
            foreach (var file in files)
            {
                await _service.QueueOutgoingFileAsync(file);
            }
            DropTitleText.Text = "已加入发送队列";
            DropHintText.Text = _service.SessionCount > 0 ? "手机端将自动开始接收" : "手机连接后将自动开始接收";
        }
        catch (Exception exception)
        {
            DropTitleText.Text = "文件准备失败";
            DropHintText.Text = exception.GetBaseException().Message;
        }
        RefreshStatus();
    }

    private void Service_TransferProgressChanged(CollaborationTransferProgress progress)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_transferById.TryGetValue(progress.TransferId, out var row))
            {
                row = new TransferRow(progress.TransferId);
                _transferById[progress.TransferId] = row;
                Transfers.Insert(0, row);
            }
            row.Update(progress);
            while (Transfers.Count > 60)
            {
                var removed = Transfers[^1];
                Transfers.RemoveAt(Transfers.Count - 1);
                _transferById.Remove(removed.TransferId);
            }
            TransferEmptyState.Visibility = Transfers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshStatus();
        });
    }

    private void Service_DeviceStateChanged() => _ = Dispatcher.InvokeAsync(RefreshStatus);

    private void Service_RemoteFilesChanged() => _ = Dispatcher.InvokeAsync(RefreshStatus);

    private void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Transfers.Where(row => row.IsFinished).ToArray())
        {
            Transfers.Remove(row);
            _transferById.Remove(row.TransferId);
        }
        TransferEmptyState.Visibility = Transfers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TransferItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TransferRow row } || !row.IsCompleted)
        {
            return;
        }
        if (!_service.TryGetTransferOpenPath(row.TransferId, out var path))
        {
            DropTitleText.Text = "文件已不在原位置";
            DropHintText.Text = "它可能已被移动或删除";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            DropTitleText.Text = "无法打开该文件";
            DropHintText.Text = exception.GetBaseException().Message;
        }
    }

    private void OpenIncomingFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_service.IncomingDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_service.IncomingDirectory}\"") { UseShellExecute = true });
        }
        catch
        {
            DropTitleText.Text = "无法打开接收目录";
        }
    }

    private void OpenConnectionSettings_Click(object sender, RoutedEventArgs e) => ConnectionSettingsRequested?.Invoke();

    private void OpenRemoteFiles_Click(object sender, RoutedEventArgs e)
    {
        var window = new RemoteFileAccessWindow(_service.RemoteFiles)
        {
            Owner = Window.GetWindow(this)
        };
        window.ShowDialog();
        RefreshStatus();
    }
}

public sealed class TransferRow : INotifyPropertyChanged
{
    private string _fileName = string.Empty;
    private string _detailText = string.Empty;
    private string _stateText = string.Empty;
    private string _sizeText = string.Empty;
    private string _percentageText = string.Empty;
    private double _percentage;
    private Brush _badgeBackground = Brushes.Transparent;
    private Brush _badgeForeground = Brushes.Transparent;

    public TransferRow(string transferId) => TransferId = transferId;

    public string TransferId { get; }
    public string FileName { get => _fileName; private set => SetField(ref _fileName, value); }
    public string DetailText { get => _detailText; private set => SetField(ref _detailText, value); }
    public string StateText { get => _stateText; private set => SetField(ref _stateText, value); }
    public string SizeText { get => _sizeText; private set => SetField(ref _sizeText, value); }
    public string PercentageText { get => _percentageText; private set => SetField(ref _percentageText, value); }
    public double Percentage { get => _percentage; private set => SetField(ref _percentage, value); }
    public Brush BadgeBackground { get => _badgeBackground; private set => SetField(ref _badgeBackground, value); }
    public Brush BadgeForeground { get => _badgeForeground; private set => SetField(ref _badgeForeground, value); }
    public bool IsFinished { get; private set; }
    public bool IsCompleted { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(CollaborationTransferProgress progress)
    {
        FileName = progress.FileName;
        DetailText = $"{(progress.Direction == "Send" ? "电脑 → 手机" : "手机 → 电脑")} · {progress.Message}";
        Percentage = progress.Percentage;
        PercentageText = progress.State == "Queued" ? "等待接收" : $"{progress.Percentage:0}%";
        SizeText = $"{FormatBytes(progress.TransferredBytes)} / {FormatBytes(progress.TotalBytes)}";
        StateText = progress.State switch
        {
            "Completed" => "已完成",
            "Failed" => "失败",
            "Queued" => "等待中",
            "Preparing" => "准备中",
            _ => "传输中"
        };
        IsFinished = progress.State is "Completed" or "Failed";
        IsCompleted = progress.State == "Completed";
        BadgeBackground = new SolidColorBrush(progress.State switch
        {
            "Completed" => Color.FromArgb(170, 218, 250, 239),
            "Failed" => Color.FromArgb(170, 255, 226, 229),
            _ => Color.FromArgb(170, 226, 237, 255)
        });
        BadgeForeground = new SolidColorBrush(progress.State switch
        {
            "Completed" => Color.FromRgb(26, 151, 112),
            "Failed" => Color.FromRgb(206, 74, 88),
            _ => Color.FromRgb(77, 124, 254)
        });
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsCompleted));
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.00} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.0} MB",
        >= 1024L => $"{bytes / 1024d:0.0} KB",
        _ => $"{Math.Max(0, bytes)} B"
    };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
