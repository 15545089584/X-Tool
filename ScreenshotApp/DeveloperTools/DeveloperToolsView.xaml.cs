using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using ScreenshotApp.SystemTools;

namespace ScreenshotApp.DeveloperTools;

public partial class DeveloperToolsView : UserControl
{
    private readonly DeveloperEnvironmentScanner _scanner = new();
    private CancellationTokenSource? _scanCancellation;
    private DeveloperEnvironmentSnapshot _snapshot = new();
    private bool _hasScanned;
    private ToolchainSummary? _configurationToolchain;
    private ToolchainEnvironmentPlan? _configurationPlan;
    private bool _isApplyingEnvironment;

    public DeveloperToolsView()
    {
        InitializeComponent();
        DataContext = _snapshot;
    }

    private async void DeveloperToolsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_hasScanned && _scanCancellation is null && Visibility == Visibility.Visible)
        {
            await StartScanAsync();
        }
    }

    private async void DeveloperToolsView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            _scanCancellation?.Cancel();
            return;
        }

        if (IsLoaded && !_hasScanned && _scanCancellation is null)
        {
            await StartScanAsync();
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null)
        {
            _scanCancellation.Cancel();
            return;
        }

        await StartScanAsync();
    }

    private async Task StartScanAsync()
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        ScanButton.Content = "取消扫描";
        ScanStateText.Text = "正在扫描";
        ScanDetailText.Text = "读取 PATH 与常用安装来源";
        ProgressTrack.Visibility = Visibility.Visible;
        EmptyState.Visibility = _hasScanned ? Visibility.Collapsed : Visibility.Visible;

        var progress = new Progress<DeveloperScanProgress>(value =>
        {
            ScanStateText.Text = value.Message;
            ScanDetailText.Text = $"{value.Completed} / {value.Total}";
            var availableWidth = ProgressTrack.ActualWidth > 0 ? ProgressTrack.ActualWidth : 210;
            ProgressFill.Width = availableWidth * Math.Clamp(value.Percentage / 100d, 0, 1);
        });

        try
        {
            var snapshot = await _scanner.ScanAsync(progress, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _snapshot = snapshot;
            DataContext = snapshot;
            _hasScanned = true;
            ScanStateText.Text = "扫描完成";
            ScanDetailText.Text = snapshot.ScannedAtText;
            EmptyState.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            ScanStateText.Text = "扫描已取消";
            ScanDetailText.Text = _hasScanned ? _snapshot.ScannedAtText : "可重新开始扫描";
            EmptyState.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ScanStateText.Text = "扫描失败";
            ScanDetailText.Text = ex.Message;
            EmptyState.Visibility = Visibility.Collapsed;
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation.Dispose();
                _scanCancellation = null;
                ScanButton.Content = "重新扫描";
                ProgressTrack.Visibility = Visibility.Collapsed;
                ProgressFill.Width = 0;
            }
        }
    }

    private void DeveloperTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton radioButton)
        {
            return;
        }

        var tab = radioButton.Tag?.ToString() ?? "Overview";
        OverviewView.Visibility = tab == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        ToolchainsView.Visibility = tab == "Toolchains" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsView.Visibility = tab == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenInstallationLocation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not ToolchainInstallation installation)
        {
            return;
        }

        var path = installation.InstallationPath;
        if (!Directory.Exists(path))
        {
            ScanStateText.Text = "安装目录已不存在";
            ScanDetailText.Text = path;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                ArgumentList = { path }
            });
        }
        catch (Exception ex)
        {
            ScanStateText.Text = "无法打开安装目录";
            ScanDetailText.Text = ex.Message;
        }
    }

    private void ConfigureToolchain_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not ToolchainSummary toolchain) return;
        var installation = toolchain.Installations
            .OrderByDescending(item => item.IsActive)
            .ThenByDescending(item => item.IsVerified)
            .FirstOrDefault(item => item.IsVerified);
        if (installation is null)
        {
            ScanStateText.Text = "没有可自动配置的安装";
            ScanDetailText.Text = "请先在 SDK 与工具链中核实安装路径";
            return;
        }

        OpenEnvironmentConfiguration(toolchain, installation);
    }

    private void OpenEnvironmentConfiguration(ToolchainSummary toolchain, ToolchainInstallation installation)
    {
        _configurationToolchain = toolchain;
        ConfigurationTitleText.Text = $"配置 {toolchain.DisplayName} 环境";
        ConfigurationSubtitleText.Text = "X-Tool 只会应用下方列出的 PATH 条目和配套变量，不会删除已有配置。";
        ConfigurationResultText.Text = string.Empty;
        ConfigurationInstallationComboBox.ItemsSource = toolchain.Installations.Where(item => item.IsVerified).ToList();
        ConfigurationScopeComboBox.SelectedIndex = 0;
        ConfigurationInstallationComboBox.SelectedItem = installation;
        EnvironmentConfigurationOverlay.Visibility = Visibility.Visible;
        RefreshEnvironmentConfigurationPlan();
    }

    private void ConfigurationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EnvironmentConfigurationOverlay.Visibility == Visibility.Visible) RefreshEnvironmentConfigurationPlan();
    }

    private void ConfigurationScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EnvironmentConfigurationOverlay.Visibility == Visibility.Visible) RefreshEnvironmentConfigurationPlan();
    }

    private void RefreshEnvironmentConfigurationPlan()
    {
        if (_configurationToolchain is null || ConfigurationInstallationComboBox.SelectedItem is not ToolchainInstallation installation)
        {
            _configurationPlan = null;
            ApplyEnvironmentConfigurationButton.IsEnabled = false;
            return;
        }

        var plan = DeveloperEnvironmentConfigurationPlanner.Build(_configurationToolchain, installation);
        _configurationPlan = plan;
        ConfigurationPathText.Text = $"{installation.Version}  ·  {installation.ExecutablePath}";
        var target = SelectedConfigurationScope;
        var preview = new StringBuilder();
        if (plan.PathEntries.Count > 0)
        {
            preview.AppendLine("PATH：");
            foreach (var entry in plan.PathEntries) preview.AppendLine($"  + {entry}");
        }
        if (plan.Variables.Count > 0)
        {
            if (preview.Length > 0) preview.AppendLine();
            preview.AppendLine("配套变量：");
            foreach (var pair in plan.Variables)
            {
                var current = Environment.GetEnvironmentVariable(pair.Key, target);
                var otherTarget = target == EnvironmentVariableTarget.Machine ? EnvironmentVariableTarget.User : EnvironmentVariableTarget.Machine;
                var otherValue = Environment.GetEnvironmentVariable(pair.Key, otherTarget);
                preview.AppendLine(string.IsNullOrWhiteSpace(current)
                    ? $"  + {pair.Key} = {pair.Value}"
                    : $"  {pair.Key}：{current}  →  {pair.Value}");
                if (target == EnvironmentVariableTarget.Machine && !string.IsNullOrWhiteSpace(otherValue))
                {
                    preview.AppendLine($"    注意：当前用户还存在 {pair.Key} = {otherValue}，它可能覆盖系统值。");
                }
            }
        }
        ConfigurationPlanText.Text = preview.Length == 0 ? "没有可应用的配置。" : preview.ToString().TrimEnd();

        var messages = new List<string>();
        if (!string.IsNullOrWhiteSpace(plan.BlockingReason)) messages.Add(plan.BlockingReason);
        messages.AddRange(plan.Warnings);
        if (target == EnvironmentVariableTarget.Machine) messages.Add("系统范围会弹出 Windows UAC；授权子进程只执行本次环境配置。 ");
        ConfigurationWarningText.Text = string.Join(Environment.NewLine, messages);
        ConfigurationWarningBorder.Visibility = messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyEnvironmentConfigurationButton.IsEnabled = plan.CanApply && !_isApplyingEnvironment;
    }

    private async void ApplyEnvironmentConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (_configurationPlan is null || !_configurationPlan.CanApply || _isApplyingEnvironment) return;
        var target = SelectedConfigurationScope;
        if (target == EnvironmentVariableTarget.Machine && !SystemToolsService.IsRunningAsAdministrator())
        {
            var confirmation = MessageBox.Show(
                "将把所选开发工具配置写入系统环境变量，整台电脑上的新进程都会读取该配置。是否继续申请管理员权限？",
                "确认配置系统环境",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes) return;
        }

        _isApplyingEnvironment = true;
        ApplyEnvironmentConfigurationButton.IsEnabled = false;
        ApplyEnvironmentConfigurationButton.Content = "正在配置…";
        ConfigurationInstallationComboBox.IsEnabled = false;
        ConfigurationScopeComboBox.IsEnabled = false;
        ConfigurationResultText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(57, 118, 90));
        ConfigurationResultText.Text = "正在保存环境变量，窗口仍可继续响应。";
        try
        {
            var result = await SystemToolsService.ApplyEnvironmentConfigurationAsync(
                target,
                _configurationPlan.PathEntries,
                _configurationPlan.Variables);
            if (!result.Succeeded)
            {
                ConfigurationResultText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(181, 76, 76));
                ConfigurationResultText.Text = result.Error ?? "配置失败，请检查环境变量页面。";
                return;
            }

            ConfigurationResultText.Text = result.Changed
                ? "配置已保存，正在重新扫描验证；已经打开的终端需要重新启动。"
                : "目标配置已经存在，无需重复写入。";
            await StartScanAsync();
            EnvironmentConfigurationOverlay.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _isApplyingEnvironment = false;
            ApplyEnvironmentConfigurationButton.Content = "加入 PATH";
            ConfigurationInstallationComboBox.IsEnabled = true;
            ConfigurationScopeComboBox.IsEnabled = true;
            RefreshEnvironmentConfigurationPlan();
        }
    }

    private void CancelEnvironmentConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingEnvironment) return;
        EnvironmentConfigurationOverlay.Visibility = Visibility.Collapsed;
        _configurationToolchain = null;
        _configurationPlan = null;
        ConfigurationResultText.Text = string.Empty;
    }

    private EnvironmentVariableTarget SelectedConfigurationScope
        => (ConfigurationScopeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Machine"
            ? EnvironmentVariableTarget.Machine
            : EnvironmentVariableTarget.User;

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        var report = new StringBuilder()
            .AppendLine("X-Tool 开发环境诊断报告")
            .AppendLine($"扫描时间：{_snapshot.ScannedAt:yyyy-MM-dd HH:mm:ss}")
            .AppendLine();

        foreach (var toolchain in _snapshot.Toolchains)
        {
            report.AppendLine($"[{toolchain.DisplayName}] {toolchain.StatusText}");
            foreach (var installation in toolchain.Installations)
            {
                report.AppendLine($"- {installation.Version} | {installation.StateText} | {installation.ExecutablePath}");
                if (!string.Equals(Path.GetDirectoryName(installation.ExecutablePath), installation.InstallationPath, StringComparison.OrdinalIgnoreCase))
                {
                    report.AppendLine($"  安装位置：{installation.InstallationPath}");
                }
                report.AppendLine($"  依据：{installation.Evidence}");
            }
            report.AppendLine();
        }

        report.AppendLine("诊断：");
        foreach (var issue in _snapshot.Issues)
        {
            report.AppendLine($"- [{issue.SeverityText}] {issue.Title}")
                .AppendLine($"  {issue.Description}")
                .AppendLine($"  {issue.Evidence.Replace(Environment.NewLine, Environment.NewLine + "  ")}");
        }

        try
        {
            Clipboard.SetText(report.ToString());
            ScanStateText.Text = "诊断报告已复制";
            ScanDetailText.Text = "报告不包含密码、Token 或项目文件内容";
        }
        catch (Exception ex)
        {
            ScanStateText.Text = "复制失败";
            ScanDetailText.Text = ex.Message;
        }
    }
}
