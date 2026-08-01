using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ScreenshotApp.DeveloperTools;

public partial class DeveloperToolsView : UserControl
{
    private readonly DeveloperEnvironmentScanner _scanner = new();
    private CancellationTokenSource? _scanCancellation;
    private DeveloperEnvironmentSnapshot _snapshot = new();
    private bool _hasScanned;

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
