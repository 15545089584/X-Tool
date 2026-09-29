using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ScreenshotApp.GitHubCenter;

public partial class GitHubConnectionHelp : Window
{
    public GitHubConnectionHelp() => InitializeComponent();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url }) return;
        // 帮助链接限定官方域名，仓库内容不能扩展浏览器打开范围。
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || (uri.Host != "github.com" && uri.Host != "docs.github.com")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { MessageBox.Show(this, "无法打开默认浏览器，请稍后重试。", Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
