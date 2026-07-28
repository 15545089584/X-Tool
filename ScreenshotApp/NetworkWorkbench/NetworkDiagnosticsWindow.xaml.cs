using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace ScreenshotApp.NetworkWorkbench;

public partial class NetworkDiagnosticsWindow : Window
{
    private readonly ObservableCollection<NetworkDiagnosticResult> _results = new();
    private CancellationTokenSource? _cancellation;

    public NetworkDiagnosticsWindow()
    {
        InitializeComponent();
        ResultsListBox.ItemsSource = _results;
        Closed += (_, _) => _cancellation?.Cancel();
    }

    private async void Diagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind } button) return;
        if (_cancellation is not null)
        {
            MessageBox.Show(this, "已有诊断任务正在执行，请先取消或等待完成。", "连接诊断", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var target = TargetTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "请输入域名、IP 或 HTTP 地址。", "连接诊断", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (kind != "HTTP" && Uri.TryCreate(target, UriKind.Absolute, out var uri)) target = uri.Host;
        var port = int.TryParse(PortTextBox.Text, out var parsedPort) && parsedPort is > 0 and <= 65535 ? parsedPort : 443;

        _cancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        StatusText.Text = kind == "Full" ? "正在执行分层完整诊断…" : $"正在执行 {kind}…";
        NetworkDiagnosticResult? pending = null;
        if (kind == "Full")
        {
            button.IsEnabled = false;
            button.Content = "诊断中…";
            pending = new NetworkDiagnosticResult(DateTime.Now, "完整诊断", target, true, "正在检查默认路由、DNS、TCP、HTTP 与当前代理…", 0);
            _results.Insert(0, pending);
        }

        try
        {
            if (kind == "Full")
            {
                var results = await NetworkWorkbenchService.RunFullDiagnosticAsync(target, port, _cancellation.Token);
                foreach (var result in results.Reverse()) _results.Insert(0, result);
            }
            else
            {
                _results.Insert(0, await NetworkWorkbenchService.RunDiagnosticAsync(kind, target, port, _cancellation.Token));
            }
            StatusText.Text = "诊断完成";
        }
        catch (OperationCanceledException)
        {
            _results.Insert(0, new NetworkDiagnosticResult(DateTime.Now, kind == "Full" ? "完整诊断" : kind, target, false, "已取消", 0));
            StatusText.Text = "诊断已取消";
        }
        catch (Exception exception)
        {
            _results.Insert(0, new NetworkDiagnosticResult(DateTime.Now, kind == "Full" ? "完整诊断" : kind, target, false, exception.Message, 0));
            StatusText.Text = "诊断失败";
        }
        finally
        {
            if (pending is not null) _results.Remove(pending);
            if (kind == "Full")
            {
                button.Content = "完整诊断";
                button.IsEnabled = true;
            }
            _cancellation.Dispose();
            _cancellation = null;
            CancelButton.IsEnabled = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void Clear_Click(object sender, RoutedEventArgs e) => _results.Clear();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
