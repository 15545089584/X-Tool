using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using ScreenshotApp.SystemTools;

namespace ScreenshotApp.DeveloperTools;

public partial class DeveloperToolsView : UserControl
{
    private readonly DeveloperEnvironmentScanner _scanner = new();
    private readonly ManagedToolchainService _managedToolchainService = new();
    private readonly ObservableCollection<ManagedToolchainRelease> _managedReleases = new();
    private readonly ObservableCollection<ManagedToolchainRelease> _managedHistoricalReleases = new();
    private readonly ObservableCollection<ManagedToolchainRelease> _managedMysqlReleases = new();
    private readonly ObservableCollection<ManagedToolchainRelease> _managedHistoricalMysqlReleases = new();
    private readonly ObservableCollection<ManagedToolchainRelease> _managedDesktopReleases = new();
    private readonly ObservableCollection<ManagedToolchainRelease> _managedDockerCliReleases = new();
    private readonly ObservableCollection<ManagedToolchainRelease> _managedPythonReleases = new();
    private readonly ObservableCollection<ManagedToolchainRelease> _managedHistoricalPythonReleases = new();
    private readonly ObservableCollection<ManagedToolchainRelease> _managedNodeReleases = new();
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _managedCatalogCancellation;
    private CancellationTokenSource? _managedOperationCancellation;
    private DeveloperEnvironmentSnapshot _snapshot = new();
    private bool _hasScanned;
    private ToolchainSummary? _configurationToolchain;
    private ToolchainEnvironmentPlan? _configurationPlan;
    private bool _isApplyingEnvironment;

    public DeveloperToolsView()
    {
        InitializeComponent();
        DataContext = _snapshot;
        ManagedReleasesItemsControl.ItemsSource = _managedReleases;
        ManagedHistoricalReleasesItemsControl.ItemsSource = _managedHistoricalReleases;
        ManagedMysqlReleasesItemsControl.ItemsSource = _managedMysqlReleases;
        ManagedHistoricalMysqlReleasesItemsControl.ItemsSource = _managedHistoricalMysqlReleases;
        ManagedDesktopReleasesItemsControl.ItemsSource = _managedDesktopReleases;
        ManagedDockerCliReleasesItemsControl.ItemsSource = _managedDockerCliReleases;
        ManagedPythonReleasesItemsControl.ItemsSource = _managedPythonReleases;
        ManagedHistoricalPythonReleasesItemsControl.ItemsSource = _managedHistoricalPythonReleases;
        ManagedNodeReleasesItemsControl.ItemsSource = _managedNodeReleases;
        ManagedInstallRootText.Text = $"托管目录：{_managedToolchainService.ManagedRoot}";
    }

    private void DeveloperToolsView_Unloaded(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
        _managedCatalogCancellation?.Cancel();
        _managedOperationCancellation?.Cancel();
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
        ManagedInstallView.Visibility = tab == "Managed" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsView.Visibility = tab == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        if (tab == "Managed" && _managedReleases.Count == 0 && _managedHistoricalReleases.Count == 0 && _managedPythonReleases.Count == 0 &&
            _managedHistoricalPythonReleases.Count == 0 &&
            _managedNodeReleases.Count == 0 && _managedCatalogCancellation is null)
        {
            _ = RefreshManagedCatalogAsync();
        }
    }

    private async void RefreshManagedCatalog_Click(object sender, RoutedEventArgs e)
        => await RefreshManagedCatalogAsync();

    private async Task RefreshManagedCatalogAsync()
    {
        _managedCatalogCancellation?.Cancel();
        _managedCatalogCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _managedCatalogCancellation = cancellation;
        RefreshManagedCatalogButton.IsEnabled = false;
        ManagedCatalogStateText.Text = "正在读取 Adoptium API…";
        try
        {
            var releases = await _managedToolchainService.GetTemurinReleasesAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            _managedReleases.Clear();
            _managedHistoricalReleases.Clear();
            foreach (var release in releases.Where(item => item.IsRecommended)) _managedReleases.Add(release);
            foreach (var release in releases.Where(item => item.IsHistorical)) _managedHistoricalReleases.Add(release);
            ManagedCatalogEmptyState.Visibility = releases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ManagedHistoricalSection.Visibility = _managedHistoricalReleases.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ManagedCatalogStateText.Text = releases.Count == 0 ? "Temurin 未返回版本" : $"Temurin {releases.Count} 个版本";

            try
            {
                var mysqlReleases = await _managedToolchainService.GetMysqlReleasesAsync(cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                _managedMysqlReleases.Clear();
                _managedHistoricalMysqlReleases.Clear();
                foreach (var release in mysqlReleases.Where(item => item.IsRecommended)) _managedMysqlReleases.Add(release);
                foreach (var release in mysqlReleases.Where(item => item.IsHistorical)) _managedHistoricalMysqlReleases.Add(release);
                ManagedMysqlEmptyState.Visibility = mysqlReleases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ManagedHistoricalMysqlSection.Visibility = _managedHistoricalMysqlReleases.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                ManagedMysqlStateText.Text = mysqlReleases.Count == 0 ? "MySQL 官方目录未返回版本" : $"已读取 {mysqlReleases.Count} 个 MySQL 版本";
                ManagedCatalogStateText.Text = $"已读取 {releases.Count + mysqlReleases.Count} 个官方版本";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _managedMysqlReleases.Clear();
                _managedHistoricalMysqlReleases.Clear();
                ManagedHistoricalMysqlSection.Visibility = Visibility.Collapsed;
                ManagedMysqlEmptyState.Visibility = Visibility.Visible;
                ManagedMysqlStateText.Text = ex.Message;
            }

            try
            {
                var desktopReleases = await _managedToolchainService.GetDockerDesktopReleasesAsync(cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                _managedDesktopReleases.Clear();
                foreach (var release in desktopReleases) _managedDesktopReleases.Add(release);
                ManagedDesktopEmptyState.Visibility = desktopReleases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                var cliReleases = await _managedToolchainService.GetDockerCliReleasesAsync(cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                _managedDockerCliReleases.Clear();
                foreach (var release in cliReleases) _managedDockerCliReleases.Add(release);
                ManagedDockerCliEmptyState.Visibility = cliReleases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                var desktopText = desktopReleases.Count > 0 ? $"Desktop {desktopReleases[0].Version}" : "Desktop 目录不可用";
                var cliText = cliReleases.Count > 0 ? $"CLI {cliReleases[0].Version}" : "CLI 目录不可用";
                ManagedDockerStateText.Text = $"{desktopText} · {cliText}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _managedDesktopReleases.Clear();
                _managedDockerCliReleases.Clear();
                ManagedDesktopEmptyState.Visibility = Visibility.Visible;
                ManagedDockerCliEmptyState.Visibility = Visibility.Visible;
                ManagedDockerStateText.Text = ex.Message;
            }

            try
            {
                var pythonReleases = await _managedToolchainService.GetUvPythonReleasesAsync(cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                _managedPythonReleases.Clear();
                _managedHistoricalPythonReleases.Clear();
                foreach (var release in pythonReleases.Where(item => item.IsRecommended)) _managedPythonReleases.Add(release);
                foreach (var release in pythonReleases.Where(item => item.IsHistorical)) _managedHistoricalPythonReleases.Add(release);
                ManagedPythonEmptyState.Visibility = pythonReleases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ManagedHistoricalPythonSection.Visibility = _managedHistoricalPythonReleases.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                ManagedPythonStateText.Text = pythonReleases.Count == 0 ? "uv 未返回可安装版本" : $"已读取 {pythonReleases.Count} 个 CPython 版本";
                ManagedCatalogStateText.Text = $"已读取 {releases.Count + pythonReleases.Count} 个官方版本";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _managedPythonReleases.Clear();
                _managedHistoricalPythonReleases.Clear();
                ManagedHistoricalPythonSection.Visibility = Visibility.Collapsed;
                ManagedPythonEmptyState.Visibility = Visibility.Visible;
                ManagedPythonStateText.Text = ex.Message;
            }

            try
            {
                var nodeReleases = await _managedToolchainService.GetVoltaNodeReleasesAsync(cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                _managedNodeReleases.Clear();
                foreach (var release in nodeReleases) _managedNodeReleases.Add(release);
                ManagedNodeEmptyState.Visibility = nodeReleases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                var voltaAvailable = nodeReleases.Any(release => release.IsProviderAvailable);
                InstallVoltaButton.Visibility = voltaAvailable ? Visibility.Collapsed : Visibility.Visible;
                ManagedNodeStateText.Text = nodeReleases.Count == 0
                    ? "Node.js 官方目录未返回受支持版本"
                    : voltaAvailable ? $"已读取 {nodeReleases.Count} 个受支持版本" : "未安装 Volta；可先通过 WinGet 安装";
                ManagedCatalogStateText.Text = $"已读取 {_managedReleases.Count + _managedHistoricalReleases.Count + _managedMysqlReleases.Count + _managedHistoricalMysqlReleases.Count + _managedDesktopReleases.Count + _managedDockerCliReleases.Count + _managedPythonReleases.Count + nodeReleases.Count} 个官方版本";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _managedNodeReleases.Clear();
                ManagedNodeEmptyState.Visibility = Visibility.Visible;
                InstallVoltaButton.Visibility = Visibility.Collapsed;
                ManagedNodeStateText.Text = ex.Message;
            }
        }
        catch (OperationCanceledException)
        {
            ManagedCatalogStateText.Text = "版本读取已取消";
        }
        catch (Exception ex)
        {
            ManagedCatalogStateText.Text = $"读取失败：{ex.Message}";
            ManagedCatalogEmptyState.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_managedCatalogCancellation, cancellation))
            {
                _managedCatalogCancellation.Dispose();
                _managedCatalogCancellation = null;
                RefreshManagedCatalogButton.IsEnabled = true;
            }
        }
    }

    private async void ManagedReleaseAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not ManagedToolchainRelease release || _managedOperationCancellation is not null) return;
        if (!release.CanExecuteAction) return;
        var wasInstalled = release.IsInstalled;
        if (release.IsInstalled)
        {
            var confirmation = MessageBox.Show(
                $"将卸载 X-Tool 托管的 {release.DisplayName} {release.Version}。\n\n如果 PATH、配套变量或运行中的进程仍引用它，操作会被拒绝。是否继续？",
                "确认卸载托管工具链", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) return;
        }
        else
        {
            var confirmation = MessageBox.Show(
                release.ProviderId == "uv"
                    ? $"将调用本机已验证的 uv 安装：\n\n{release.DisplayName} {release.Version}\n{release.Architecture}\n\n{(release.IsHistorical ? "警告：该 Python 分支已经结束官方安全维护，只建议用于无法升级的旧项目。\n\n" : string.Empty)}uv 将按其官方目录下载并校验，且只安装到 X-Tool 的当前用户托管目录，不注册系统 Python。是否继续？"
                    : release.ProviderId == "volta"
                        ? $"将调用本机已验证的 Volta 缓存：\n\n{release.DisplayName} {release.Version}\n{release.Architecture}\n\n此操作只执行 volta fetch，不会改变当前默认 Node.js，也不会修改 PATH。是否继续？"
                    : release.ProviderId == "docker-desktop"
                        ? $"将下载 Docker Desktop 安装器：\n\n{release.DisplayName} {release.Version}\n{release.Architecture} · {release.SizeText}\n\n下载完成后校验官方数字签名并启动安装向导；安装需要管理员权限与 WSL2，X-Tool 不会静默安装。是否继续？"
                    : release.ProviderId == "docker-cli"
                        ? $"将从 Docker 官方下载静态 CLI 包：\n\n{release.DisplayName} {release.Version}\n{release.Architecture} · {release.SizeText}\n\n解压到 X-Tool 托管目录后仅提供 docker CLI（无守护进程），容器引擎仍需 Docker Desktop 或远程 DOCKER_HOST。是否继续？"
                    : release.ProviderId == "mysql"
                        ? $"将从 MySQL 官方 CDN 归档下载：\n\n{release.DisplayName} {release.Version}\n{release.Architecture} · {release.SizeText}\n\n{(release.IsHistorical ? "警告：该 MySQL 分支已经结束官方安全维护，只建议用于无法升级的旧项目。\n\n" : string.Empty)}下载完成后校验官方 MD5，解压到当前用户的 X-Tool 托管目录；不初始化数据目录、不注册 Windows 服务、不修改 PATH。是否继续？"
                    : $"将安装：\n\n{release.DisplayName} {release.Version}\n{release.Architecture} · {release.SizeText}\n下载源：{_managedToolchainService.TemurinDownloadSourceText}\n\n下载完成后仍会校验 Adoptium API 提供的 SHA-256，安装到当前用户的 X-Tool 托管目录。是否继续？",
                "确认安装托管工具链", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes) return;
        }

        var cancellation = new CancellationTokenSource();
        _managedOperationCancellation = cancellation;
        release.IsBusy = true;
        CancelManagedOperationButton.Visibility = Visibility.Visible;
        RefreshManagedCatalogButton.IsEnabled = false;
        var progress = new Progress<ManagedInstallProgress>(value =>
        {
            release.ProgressPercentage = value.Percentage;
            release.ProgressText = value.DisplayText;
            ManagedCatalogStateText.Text = value.DisplayText;
        });
        try
        {
            var result = release.IsInstalled
                ? await _managedToolchainService.UninstallAsync(release, cancellation.Token)
                : await _managedToolchainService.InstallAsync(release, progress, cancellation.Token);
            release.ProgressText = result.Message;
            ManagedCatalogStateText.Text = result.Message;
            if (result.Succeeded)
            {
                if (release.ProviderId != "volta" && release.ProviderId != "docker-desktop") release.IsInstalled = !release.IsInstalled;
                await StartScanAsync();
                await RefreshManagedCatalogAsync();
                if (!wasInstalled && result.Entry is not null)
                {
                    var synchronized = _snapshot.Toolchains
                        .FirstOrDefault(toolchain => toolchain.Id == result.Entry.ToolchainId)?
                        .Installations.Any(installation =>
                            string.Equals(installation.ExecutablePath, result.Entry.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(installation.ResolvedExecutablePath, result.Entry.ExecutablePath, StringComparison.OrdinalIgnoreCase)) == true;
                    ManagedCatalogStateText.Text = synchronized
                        ? $"{release.DisplayName} 已同步到 SDK 与工具链"
                        : $"{release.DisplayName} 安装成功，但重新扫描尚未确认该路径";
                    if (synchronized) ToolchainsTab.IsChecked = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            release.ProgressText = "任务已取消；对应工具将处理未完成的下载内容。";
            ManagedCatalogStateText.Text = "任务已取消";
        }
        finally
        {
            release.IsBusy = false;
            CancelManagedOperationButton.Visibility = Visibility.Collapsed;
            RefreshManagedCatalogButton.IsEnabled = true;
            _managedOperationCancellation?.Dispose();
            _managedOperationCancellation = null;
        }
    }

    private void ManagedDownloadSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JdkDownloadSourceComboBox.SelectedItem is not ComboBoxItem item) return;
        _managedToolchainService.TemurinDownloadSource = item.Tag?.ToString() == "Tuna"
            ? ManagedDownloadSource.Tuna
            : ManagedDownloadSource.Official;
        JdkDownloadSourceStateText.Text = _managedToolchainService.TemurinDownloadSource == ManagedDownloadSource.Tuna
            ? "镜像失败会自动回退官方源，SHA-256 仍以 Adoptium API 为准"
            : "由 Adoptium API 提供下载地址与 SHA-256";
    }

    private async void InstallVolta_Click(object sender, RoutedEventArgs e)
    {
        if (_managedOperationCancellation is not null) return;
        var confirmation = MessageBox.Show(
            "将通过 Windows 程序包管理器执行：\n\nwinget install --id Volta.Volta\n\n这是 Volta 官方推荐的 Windows 安装方式。安装程序可能弹出 UAC；X-Tool 主程序仍保持普通权限。是否继续？",
            "确认安装 Volta", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;

        var cancellation = new CancellationTokenSource();
        _managedOperationCancellation = cancellation;
        InstallVoltaButton.IsEnabled = false;
        CancelManagedOperationButton.Visibility = Visibility.Visible;
        ManagedNodeStateText.Text = "正在通过 WinGet 安装 Volta…";
        try
        {
            var result = await _managedToolchainService.InstallVoltaWithWingetAsync(cancellation.Token);
            ManagedNodeStateText.Text = result.Message;
            if (result.Succeeded) await RefreshManagedCatalogAsync();
        }
        catch (OperationCanceledException)
        {
            ManagedNodeStateText.Text = "Volta 安装已取消";
        }
        finally
        {
            CancelManagedOperationButton.Visibility = Visibility.Collapsed;
            InstallVoltaButton.IsEnabled = true;
            _managedOperationCancellation?.Dispose();
            _managedOperationCancellation = null;
        }
    }

    private void CancelManagedOperation_Click(object sender, RoutedEventArgs e)
    {
        CancelManagedOperationButton.IsEnabled = false;
        ManagedCatalogStateText.Text = "正在取消并清理临时文件…";
        _managedOperationCancellation?.Cancel();
        CancelManagedOperationButton.IsEnabled = true;
    }

    private void OpenManagedRoot_Click(object sender, RoutedEventArgs e)
    {
        OpenManagedPath(_managedToolchainService.ManagedRoot, "托管目录尚未创建");
    }

    private void OpenManagedLog_Click(object sender, RoutedEventArgs e)
    {
        var window = new ManagedLogWindow(_managedToolchainService.OperationLogPath)
        {
            Owner = Window.GetWindow(this)
        };
        window.ShowDialog();
    }

    private void OpenManagedPath(string path, string missingMessage)
    {
        if (!Directory.Exists(path))
        {
            ManagedCatalogStateText.Text = missingMessage;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ManagedCatalogStateText.Text = "无法打开托管目录";
            ManagedCatalogStateText.ToolTip = ex.Message;
        }
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
        ConfigurationPathText.Text = $"{installation.Version}  ·  {installation.ExecutionPathText}";
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
                if (installation.HasResolvedTarget) report.AppendLine($"  实际执行：{installation.ResolvedExecutablePath}");
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
