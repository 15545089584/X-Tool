using ScreenshotApp.SystemTools;
using System.Windows;

namespace ScreenshotApp.NetworkWorkbench;

public partial class NetworkDeepToolsWindow : Window
{
    private CancellationTokenSource? _cancellation;
    public NetworkDeepToolsWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _cancellation?.Cancel();
        LanAddressTextBox.Text = NetworkWorkbenchService.GetAdapters().FirstOrDefault(item => item.IsPrimary)?.IPv4Address ?? string.Empty;
    }

    private async Task RefreshAsync()
    {
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = new CancellationTokenSource();
        try
        {
            var snapshot = await NetworkDeepToolsService.ReadAsync(_cancellation.Token);
            RoutesTextBox.Text = snapshot.Routes; FirewallTextBox.Text = snapshot.FirewallProfiles;
            WifiTextBox.Text = snapshot.WifiInterface; NearbyWifiTextBox.Text = snapshot.NearbyWifi;
            ExplanationTextBox.Text = snapshot.Explanation;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "读取网络信息失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private async void ScanLan_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = new CancellationTokenSource();
        LanStatusText.Text = "正在扫描（最多 254 个私有地址）…";
        try
        {
            var devices = await NetworkDeepToolsService.ScanPrivateLanAsync(LanAddressTextBox.Text.Trim(), _cancellation.Token);
            LanDevicesListBox.ItemsSource = devices; LanStatusText.Text = $"发现 {devices.Count} 台响应设备";
        }
        catch (OperationCanceledException) { LanStatusText.Text = "扫描已取消"; }
        catch (Exception exception) { LanStatusText.Text = exception.Message; }
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string action }) return;
        var command = action switch { "FlushDns" => "ipconfig /flushdns", "Winsock" => "netsh winsock reset（需要重启）", "TcpIp" => "netsh int ip reset（需要重启）", "BlockPort" => $"创建 X-Tool 临时出站阻止规则，TCP {FirewallPortTextBox.Text}", _ => $"删除 X-Tool 临时出站阻止规则，TCP {FirewallPortTextBox.Text}" };
        if (MessageBox.Show(this, $"即将执行：\n{command}\n\n该操作可能短暂影响网络；重置操作需要重启 Windows 才完全生效。是否继续？", "确认网络修复", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var result = await Task.Run(() => ExecuteRepair(action));
        MessageBox.Show(this, result ?? "操作已完成。", result is null ? "网络修复" : "操作失败", MessageBoxButton.OK, result is null ? MessageBoxImage.Information : MessageBoxImage.Warning);
        await RefreshAsync();
    }

    private string? ExecuteRepair(string action)
    {
        bool ok; string? error;
        if (action == "FlushDns") ok = SystemToolsService.TryFlushDnsWithElevation(out error);
        else if (action == "Winsock") ok = SystemToolsService.TryResetWinsockWithElevation(out error);
        else if (action == "TcpIp") ok = SystemToolsService.TryResetTcpIpWithElevation(out error);
        else if (!int.TryParse(FirewallPortTextBox.Dispatcher.Invoke(() => FirewallPortTextBox.Text), out var port)) return "请输入有效端口。";
        else if (action == "BlockPort") ok = SystemToolsService.TryCreateTemporaryFirewallBlockRuleWithElevation(port, out error);
        else ok = SystemToolsService.TryRemoveXToolFirewallRuleWithElevation(port, out error);
        return ok ? null : error ?? "操作未完成。";
    }
}
