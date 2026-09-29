using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenshotApp.GitHubCenter;

internal static class Program
{
    private static int _checks;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "XTool-GitHub-Ui-" + Guid.NewGuid().ToString("N"));
    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(Root);
        PrepareAsync().GetAwaiter().GetResult();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // 模拟主程序的全局按钮颜色，避免独立窗口测试遗漏样式继承污染。
        var globalButton = new Style(typeof(Button)); globalButton.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        app.Resources[typeof(Button)] = globalButton;
        var globalText = new Style(typeof(TextBlock)); globalText.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Black)); app.Resources[typeof(TextBlock)] = globalText;
        var window = new GitHubCenterWindow(new GitHubStore(Path.Combine(Root, "store")), discoverOnLoad: false, apiHandler: new CommitHandler());
        window.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var started = DateTime.UtcNow;
        timer.Tick += async (_, _) =>
        {
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(30)) { timer.Stop(); Console.Error.WriteLine("加载超时"); app.Shutdown(1); return; }
            if (((TextBlock)window.FindName("StatusText")).Text != "读取仓库配置完成") return;
            timer.Stop();
            try
            {
                await ValidateAsync(window);
                Console.WriteLine($"PASS UI 共 {_checks} 项；测试目录 {Root}");
                if (!args.Contains("--interactive")) { window.Close(); app.Shutdown(); }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
        };
        timer.Start(); app.Run();
    }
    private static async Task PrepareAsync()
    {
        var git = new GitRepositoryService();
        string repo = Path.Combine(Root, "X-Tool 示例仓库"); Directory.CreateDirectory(repo);
        var ct = CancellationToken.None;
        await git.RunAsync(repo, ct, "init", "--initial-branch=main");
        await git.RunAsync(repo, ct, "config", "user.name", "UI Test");
        await git.RunAsync(repo, ct, "config", "user.email", "ui@example.invalid");
        await git.RunAsync(repo, ct, "config", "commit.gpgsign", "false");
        await git.RunAsync(repo, ct, "config", "core.hooksPath", Path.Combine(Root, "empty-hooks"));
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# X-Tool\n\n项目初始内容\n");
        await git.RunAsync(repo, ct, "add", "--", "README.md");
        await git.RunAsync(repo, ct, "commit", "-m", "添加项目说明");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# X-Tool\n\nGitHub 仓库中心第一版\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "其他任务.txt"), "这份文件不应进入本次提交。\n");
        await git.RunAsync(repo, ct, "remote", "add", "origin", "https://github.com/example/xtool.git");
        var store = new GitHubStore(Path.Combine(Root, "store"));
        store.SavePreferences(new() { SelectedPath = repo, Repositories = [new() { Path = repo, Trusted = true }] });
        store.SaveCache("public:example/xtool", new GitHubOverview([new("#12 增加仓库工作台", "codex/github-center → main · 草稿", "https://github.com/example/xtool/pull/12")], [new("仓库中心回归", "Build · main · completed / success", "https://github.com/example/xtool/actions/runs/1")], "开放的 PR（合成数据）", "最近构建（合成数据）"));
    }
    private static void Check(bool result, string name) { if (!result) throw new Exception(name); _checks++; Console.WriteLine("PASS " + name); }
    private static async Task IdleAsync(GitHubCenterWindow window)
    {
        var workspace = (Grid)window.FindName("Workspace");
        for (int i = 0; i < 300; i++) { await Task.Delay(50); if (workspace.IsEnabled) return; }
        throw new Exception("UI 操作未结束");
    }
    private static async Task ValidateAsync(GitHubCenterWindow window)
    {
        var changes = (ListBox)window.FindName("Changes");
        var tabs = (TabControl)window.FindName("Views");
        Check(changes.Items.Count == 2, "启动恢复本地仓库");
        changes.SelectedIndex = 0;
        await Task.Delay(500);
        Check(((TextBox)window.FindName("Diff")).Text.Contains("diff --git"), "选择文件异步加载差异");
        Check(((TextBox)window.FindName("Diff")).Padding.Left == 12 && ((TextBox)window.FindName("Diff")).Foreground is SolidColorBrush { Color: var ink } && ink == Color.FromRgb(35, 60, 89), "输入框保留主题内边距和文字颜色");
        var draft = (TextBox)window.FindName("CommitMessage"); draft.Text = "暂存期间保留的提交说明";
        ((Button)window.FindName("StageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await IdleAsync(window);
        Check(draft.Text == "暂存期间保留的提交说明", "暂存后提交说明不丢失");
        Check(((ScrollViewer)draft.Template.FindName("PART_ContentHost", draft)).ComputedVerticalScrollBarVisibility != Visibility.Visible, "单行提交说明不显示多余滚动条");
        Check(changes.Items.Cast<GitChange>().Count(c => c.Staged) == 1, "界面暂存只处理选中文件");
        changes.SelectedIndex = 0; await Task.Delay(300);
        foreach (var size in new[] { new Size(1260, 800), new Size(1040, 680) })
        {
            window.Width = size.Width; window.Height = size.Height; window.UpdateLayout(); await Task.Delay(100);
            var toolbar = (WrapPanel)window.FindName("GitToolbar");
            foreach (FrameworkElement child in toolbar.Children)
            {
                var bounds = child.TransformToAncestor(toolbar).TransformBounds(new Rect(child.RenderSize));
                Check(bounds.Right <= toolbar.ActualWidth + 1 && bounds.Bottom <= toolbar.ActualHeight + 1, "工具栏控件位于边界内 " + child.GetType().Name);
            }
            Check(((TextBox)window.FindName("Diff")).ActualWidth > 280, "差异区域保持可读宽度");
            Save(window, $"workspace-{size.Width}.png");
        }
        tabs.SelectedIndex = 1; await Task.Delay(100);
        var history = (ListBox)window.FindName("History"); Check(history.Items.Count == 1, "历史页加载提交");
        history.SelectedIndex = 0; await Task.Delay(400);
        Check(((TextBox)window.FindName("CommitDiff")).Text.Contains("添加项目说明"), "历史页显示详情");
        tabs.SelectedIndex = 2; await IdleAsync(window);
        Check(((ListBox)window.FindName("PullRequests")).Items.Count == 1 && ((TextBlock)window.FindName("OverviewTime")).Text.Contains("缓存"), "离线 PR 与构建缓存显示时间");
        Save(window, "overview.png");
        tabs.SelectedIndex = 3; window.UpdateLayout();
        Check(!((Button)window.FindName("LoadMoreButton")).IsEnabled, "未连接账户不提供虚假分页");
        var remote = (ListBox)window.FindName("RemoteRepositories");
        remote.ItemsSource = new[] { new RemoteRepository("example/xtool", "https://github.com/example/xtool", "https://github.com/example/xtool.git", false) };
        remote.SelectedIndex = 0;
        for (int i = 0; i < 100 && ((ListBox)window.FindName("RemoteCommits")).Items.Count == 0; i++) await Task.Delay(20);
        var remoteCommits = (ListBox)window.FindName("RemoteCommits");
        Check(remoteCommits.Items.Count == 1, "选择远程仓库直接加载提交");
        remoteCommits.SelectedIndex = 0;
        Check(((TextBox)window.FindName("RemoteCommitDetail")).Text.Contains("详细说明"), "远程提交显示完整消息");
        Check(!((Button)window.FindName("MoreCommits")).IsEnabled, "远程提交末页禁用更多");
        Save(window, "remote-history.png");
        remote.SelectedIndex = -1;
        Check(remoteCommits.Items.Count == 0, "取消仓库选择清理旧提交");
        string discovered = Path.Combine(Root, "新发现仓库"); Directory.CreateDirectory(Path.Combine(discovered, ".git"));
        await (Task)typeof(GitHubCenterWindow).GetMethod("ScanAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [new[] { Root }])!;
        var savedRepositories = new GitHubStore(Path.Combine(Root, "store")).LoadPreferences().Repositories;
        Check(savedRepositories.Any(r => r.Path == discovered && !r.Trusted), "发现仓库持久保存但不自动信任");
        Check(((ListBox)window.FindName("Repositories")).Items.Count == 2, "扫描结果直接显示在本地列表");
        tabs.SelectedIndex = 0; window.Width = 1260; window.Height = 800;
        var search = (TextBox)window.FindName("RepositorySearch"); search.Text = "不存在的仓库";
        Check(!((WrapPanel)window.FindName("GitToolbar")).IsEnabled && changes.Items.Count == 0, "搜索无结果清理旧仓库操作状态");
        Check(!((StackPanel)window.FindName("RepositoryActions")).IsEnabled, "空仓库禁用右上操作");
        Check(((StackPanel)window.FindName("RepositoryEmptyState")).Visibility == Visibility.Visible && ((WrapPanel)window.FindName("GitToolbar")).Visibility == Visibility.Collapsed, "空状态不展示无效下拉框与操作区");
        Check(((Button)window.FindName("AccountButton")).Foreground is SolidColorBrush { Color: var accountInk } && accountInk == Color.FromRgb(64, 91, 126), "账户按钮不继承全局黑色样式");
        Check(((Button)window.FindName("ConnectionHelpButton")).IsEnabled, "空仓库仍可打开绑定帮助");
        Check(((Grid)window.FindName("RepositoryWorkspace")).Margin.Left == 16 && ((Border)window.FindName("SidebarDivider")).BorderThickness.Right == 1, "侧栏独立分界与内容留白");
        Save(window, "empty.png");
        search.Clear(); ((ListBox)window.FindName("Repositories")).SelectedIndex = 0; await IdleAsync(window);
        // 仅构造对话框验证模板，不填写凭据或触发外部操作。
        object?[] values = ["连接 GitHub", null];
        var dialog = (Window)typeof(GitHubCenterWindow).GetMethod("Dialog", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, values)!;
        var panel = (StackPanel)values[1]!;
        panel.Children.Add(new PasswordBox());
        var primary = (Button)typeof(GitHubCenterWindow).GetMethod("DialogButton", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, ["连接", true])!;
        panel.Children.Add(primary); dialog.Show(); dialog.UpdateLayout();
        Check(primary.Foreground is SolidColorBrush { Color: var color } && color == Colors.White, "主按钮白色文字");
        Check(primary.Content is TextBlock { Foreground: SolidColorBrush { Color: var actualColor } } && actualColor == Colors.White, "主按钮实际文字不受全局 TextBlock 黑色污染");
        Check(dialog.WindowStyle == WindowStyle.None && dialog.AllowsTransparency && dialog.Content is Border { CornerRadius.TopLeft: 14 }, "弹窗使用自绘圆角边框而非系统标题栏");
        Save(dialog, "dialog.png"); dialog.Close();
        Exception? dialogError = null;
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            var input = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "连接 GitHub" && w != window);
            try
            {
                input.UpdateLayout();
                var password = Descendants(input).OfType<PasswordBox>().Single();
                Check(password.ActualHeight == 44, "真实连接弹窗密码框高度稳定");
                var connect = Descendants(input).OfType<Button>().Single(b => b.Content is TextBlock { Text: "连接" });
                Check(connect.Content is TextBlock { Foreground: SolidColorBrush { Color: var foreground } } && foreground == Colors.White, "真实连接按钮白字");
                Save(input, "connect-dialog.png");
            }
            catch (Exception ex) { dialogError = ex; }
            finally { input.Close(); }
        }));
        typeof(GitHubCenterWindow).GetMethod("Input", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, ["连接 GitHub", "Personal Access Token", "", true]);
        if (dialogError is not null) throw dialogError;
        var help = new GitHubConnectionHelp { Owner = window }; help.Show(); help.UpdateLayout();
        var helpTabs = (TabControl)help.FindName("HelpTabs");
        Check(helpTabs.Items.Count == 3, "帮助包含 PAT、App 与排查页");
        Save(help, "help-pat.png");
        var patScroll = (ScrollViewer)help.FindName("PatScroll"); patScroll.ScrollToBottom(); help.UpdateLayout();
        Check(patScroll.VerticalOffset > 0, "PAT 长教程可滚动");
        Save(help, "help-pat-bottom.png");
        helpTabs.SelectedIndex = 1; help.Width = 600; help.Height = 500; help.UpdateLayout(); Save(help, "help-app.png");
        Check(help.ActualWidth == 600 && help.ActualHeight == 500, "帮助最小窗口布局");
        help.Close();
        var wheelType = typeof(ScreenshotApp.DesktopPet.DesktopPetWindow).Assembly.GetType("ScreenshotApp.DesktopPet.PetCommandWheelWindow")!;
        var wheel = (Window)Activator.CreateInstance(wheelType, BindingFlags.Instance | BindingFlags.NonPublic, null, [new Rect(700, 650, 150, 150), new Rect(0, 0, 1280, 900), 0, 5], null)!;
        var sector = (FrameworkElement)wheelType.GetMethod("CreateSectorContent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(wheel, [2, 0, 5])!;
        var labels = Descendants(sector).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(labels.Contains("GitHub\n仓库中心") && !labels.Contains("静默闹钟") && !labels.Contains("5"), "轮盘仓库入口替换闹钟且无闹钟角标");
        Check(wheelType.GetEvent("GitHubRequested", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "轮盘提供仓库中心事件");
        wheel.Show(); await Task.Delay(600); wheel.UpdateLayout(); Save(wheel, "github-wheel.png"); wheel.Close();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void Save(Window window, string name)
    {
        window.UpdateLayout(); var view = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        if (name == "github-wheel.png")
        {
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            Check(Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] > 0) > 1000, "轮盘动画结束后画面非空");
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string directory = Path.GetFullPath("artifacts/github-center-validation"); Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
}

internal sealed class CommitHandler : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        const string json = "[{\"sha\":\"1234567890\",\"html_url\":\"https://github.com/example/xtool/commit/1234567890\",\"commit\":{\"message\":\"增加仓库自动发现\\n详细说明\",\"author\":{\"name\":\"UI Test\",\"date\":\"2026-09-30T00:00:00Z\"}}}]";
        return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json) });
    }
}
