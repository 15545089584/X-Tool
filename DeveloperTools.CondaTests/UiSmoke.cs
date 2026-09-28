using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotApp.DeveloperTools;

internal static class UiSmoke
{
    internal static void Run(string? buildFolder = null)
    {
        var root = Directory.GetCurrentDirectory();
        buildFolder ??= "conda-integration";
        var assembly = Assembly.LoadFrom(Path.Combine(root, $"ScreenshotApp/bin/{buildFolder}/Release/XTool.dll"));
        var app = new Application();
        app.Resources["SecondaryTextBrush"] = new SolidColorBrush(Color.FromRgb(104, 125, 145));
        var view = (UserControl)Activator.CreateInstance(assembly.GetType("ScreenshotApp.DeveloperTools.DeveloperToolsView")!)!;
        view.Background = new SolidColorBrush(Color.FromRgb(221, 229, 236));
        // 不附加 Window，不触发 Loaded 扫描，也不启动完整应用和后台服务。
        var snapshot = new DeveloperEnvironmentSnapshot();
        foreach (var name in new[] { "Java", "Python", "Node.js", ".NET SDK", "Git", "Docker", "MySQL", "Maven", "Gradle", "JMeter", "Nginx", "Conda" })
        {
            var id = name switch { ".NET SDK" => "dotnet", "Node.js" => "node", _ => name.ToLowerInvariant() };
            var summary = new ToolchainSummary { Id = id, DisplayName = name, IconSource = $"pack://application:,,,/XTool;component/Assets/Toolchains/{id}.png", IconGlyph = name[..1], Description = name == "Conda" ? "Miniconda、Anaconda 与 Miniforge 环境管理" : "界面核验样本" };
            summary.Installations.Add(new ToolchainInstallation { Version = "26.7.2", IsVerified = true, IsActive = true });
            snapshot.Toolchains.Add(summary);
        }
        view.DataContext = snapshot;
        ((FrameworkElement)view.FindName("EmptyState")).Visibility = Visibility.Collapsed;
        var output = Path.Combine(root, $"artifacts/{buildFolder}-ui-validation");
        Directory.CreateDirectory(output);
        void Render(string name)
        {
            view.Width = 1180; view.Height = 900;
            view.Measure(new Size(1180, 900)); view.Arrange(new Rect(0, 0, 1180, 900)); view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1180, 900, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        Render("overview");
        ((FrameworkElement)view.FindName("OverviewView")).Visibility = Visibility.Collapsed;
        var managed = (ScrollViewer)view.FindName("ManagedInstallView");
        managed.Visibility = Visibility.Visible; managed.Opacity = 1;
        ((FrameworkElement)view.FindName("ManagedCondaEmptyState")).Visibility = Visibility.Collapsed;
        ((TextBlock)view.FindName("ManagedCondaStateText")).Text = "两个发行版均已就绪";
        ((ItemsControl)view.FindName("ManagedCondaReleasesItemsControl")).ItemsSource = new[]
        {
            new ManagedToolchainRelease { ProviderId = "miniconda", DisplayName = "Miniconda3", Version = "py314_26.7.1-1", IsRecommended = true, ReleaseChannelText = "最新稳定安装器", DownloadSize = 132540006 },
            new ManagedToolchainRelease { ProviderId = "miniforge", DisplayName = "Miniforge3", Version = "26.7.2-0", IsRecommended = true, ReleaseChannelText = "最新稳定安装器", DownloadSize = 148256480 }
        };
        Render("managed-before-scroll");
        var target = (FrameworkElement)view.FindName("ManagedCondaReleasesItemsControl");
        managed.ScrollToVerticalOffset(target.TranslatePoint(new Point(), (UIElement)managed.Content).Y - 100);
        Render("managed-conda");
        managed.Visibility = Visibility.Collapsed;
        var toolchains = (FrameworkElement)view.FindName("ToolchainsView");
        toolchains.Visibility = Visibility.Visible; toolchains.Opacity = 1;
        view.DataContext = new DeveloperEnvironmentSnapshot();
        var condaOnly = (DeveloperEnvironmentSnapshot)view.DataContext;
        condaOnly.Toolchains.Add(snapshot.Toolchains.Last());
        Render("sdk-conda");
        toolchains.Visibility = Visibility.Collapsed;
        var diagnostics = (FrameworkElement)view.FindName("DiagnosticsView");
        diagnostics.Visibility = Visibility.Visible; diagnostics.Opacity = 1;
        condaOnly.Issues.Add(new DeveloperDiagnosticIssue { Severity = DeveloperIssueSeverity.Warning, Title = "Conda Python 目录位于全局 PATH", Description = "基础或项目环境可能抢占其他 Python；建议仅配置 condabin，在终端按需激活环境。", Evidence = "界面核验样本" });
        Render("diagnostics-conda");
        if (view.IsLoaded) throw new Exception("核验不得加载完整窗口。");
        Console.WriteLine("已离屏渲染总览与双发行版安装卡片：" + output);
    }
}
