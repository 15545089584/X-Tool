using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ScreenshotApp.SystemTools;

/// <summary>启动应用页按需读取桌面与打包应用；服务、驱动、脚本和计划任务不进入应用列表。</summary>
public partial class StartupManagementView : UserControl
{
    private IReadOnlyList<StartupEntry> _allEntries = Array.Empty<StartupEntry>();
    private CancellationTokenSource? _scanCancellation;
    private bool _isScanning;

    public StartupManagementView()
    {
        InitializeComponent();
        StartupCategoryComboBox.SelectedIndex = 0;
    }

    public void CancelActiveScan() => _scanCancellation?.Cancel();

    public async Task RefreshAsync()
    {
        if (_isScanning) return;
        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();
        var cancellation = _scanCancellation;
        _isScanning = true;
        StartupRefreshButton.IsEnabled = false;
        StartupRefreshButtonText.Text = "扫描中…";
        StartupSummaryText.Text = "正在读取 Windows 启动应用…";
        StartupCoverageText.Text = "正在核对桌面应用、启动文件夹与打包应用 StartupTask。";

        var progress = new Progress<StartupScanProgress>(snapshot =>
        {
            StartupSummaryText.Text = $"正在读取：{snapshot.SourceText}";
            StartupCoverageText.Text = $"当前已发现 {snapshot.ItemsFound:N0} 项；正在继续检查其他来源。";
        });

        try
        {
            var result = await Task.Run(
                () => StartupManagementService.Discover(progress, cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(cancellation, _scanCancellation)) return;
            _allEntries = result.Entries;
            ApplyFilter();
            UpdateSummary(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(cancellation, _scanCancellation))
            {
                StartupSummaryText.Text = "启动项扫描已取消";
                StartupCoverageText.Text = "未使用不完整结果，可再次刷新。";
            }
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(cancellation, _scanCancellation))
            {
                StartupSummaryText.Text = "启动项扫描失败";
                StartupCoverageText.Text = exception.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(cancellation, _scanCancellation))
            {
                _scanCancellation = null;
                _isScanning = false;
                StartupRefreshButton.IsEnabled = true;
                StartupRefreshButtonText.Text = _allEntries.Count == 0 ? "开始扫描" : "刷新";
            }
            cancellation.Dispose();
        }
    }

    private void UpdateSummary(StartupDiscoveryResult result)
    {
        AllApplicationCountText.Text = result.Entries.Count.ToString("N0");
        DesktopApplicationCountText.Text = result.Entries.Count(item => item.Category == StartupCategory.DesktopApplication).ToString("N0");
        PackagedApplicationCountText.Text = result.Entries.Count(item => item.Category == StartupCategory.PackagedApplication).ToString("N0");
        DisabledApplicationCountText.Text = result.Entries.Count(item => !item.IsEnabled).ToString("N0");
        StartupSummaryText.Text = $"共发现 {result.Entries.Count:N0} 个启动应用 · 已开启 {result.Entries.Count(item => item.IsEnabled):N0} · 已关闭 {result.Entries.Count(item => !item.IsEnabled):N0}";
        StartupCoverageText.Text = result.Warnings.Count == 0
            ? $"已过滤脚本、失效命令、服务、驱动及计划任务 · {result.CompletedAt:HH:mm:ss}。未注册自启动的已安装应用不会出现在这里。"
            : $"已完成，但有 {result.Warnings.Count:N0} 类来源受权限或系统状态限制：{string.Join("；", result.Warnings.Take(2))}";
    }

    private void ApplyFilter()
    {
        var keyword = StartupFilterTextBox.Text.Trim();
        var category = (StartupCategoryComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        var filtered = _allEntries.Where(item =>
            (category == "All" || string.Equals(item.Category.ToString(), category, StringComparison.OrdinalIgnoreCase)) &&
            (ThirdPartyOnlyCheckBox.IsChecked != true || !item.IsMicrosoft) &&
            (DisabledOnlyCheckBox.IsChecked != true || !item.IsEnabled) &&
            (string.IsNullOrWhiteSpace(keyword) || item.SearchText.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        // 一次替换数据源，避免 500 余项逐条通知 UI 并反复触发布局。
        StartupListBox.ItemsSource = filtered;
        StartupEmptyState.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshStartup_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void StartupFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void StartupFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void StartupFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private async void ToggleStartup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StartupEntry entry || !entry.CanToggle) return;
        var action = entry.IsEnabled ? "关闭" : "开启";
        var warning = entry.IsEnabled
            ? $"确定关闭“{entry.Name}”的自动启动吗？\n\n来源：{entry.Source}\n触发：{entry.Trigger}\n\n只修改 Windows 的启用状态，不会删除程序文件或启动注册信息。"
            : $"确定重新开启“{entry.Name}”的自动启动吗？\n\n它将在对应触发条件满足时再次运行。";
        if (MessageBox.Show(warning, $"确认{action}启动项", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        StartupRefreshButton.IsEnabled = false;
        try
        {
            var result = await StartupManagementService.ToggleAsync(entry, enable: !entry.IsEnabled, CancellationToken.None);
            MessageBox.Show(result.Message, result.Success ? "启动项管理" : "操作未完成", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (result.Success) await RefreshAsync();
        }
        finally
        {
            if (!_isScanning) StartupRefreshButton.IsEnabled = true;
        }
    }

    private void OpenWindowsStartupSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "无法打开 Windows 设置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
