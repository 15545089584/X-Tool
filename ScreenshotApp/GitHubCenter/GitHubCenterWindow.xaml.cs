using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ScreenshotApp.GitHubCenter;

public partial class GitHubCenterWindow : Window
{
    private static GitHubCenterWindow? _instance;
    private readonly GitRepositoryService _git = new();
    private readonly GitHubStore _store;
    private readonly GitHubApi _api = new();
    private GitHubPreferences _preferences = new();
    private GitHubCredential? _credential;
    private GitState? _state;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _preview;
    private bool _binding;
    private bool _ready;
    private bool _preferencesLoaded;
    private readonly Dictionary<string, string> _drafts = new(StringComparer.OrdinalIgnoreCase);
    private int _remotePage;
    private bool _more;
    private readonly List<RemoteRepository> _remoteRepositories = [];

    public static void Open()
    {
        if (_instance is { IsLoaded: true }) { if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal; _instance.Activate(); return; }
        _instance = new(); _instance.Show();
    }
    public GitHubCenterWindow() : this(new GitHubStore()) { }
    public GitHubCenterWindow(GitHubStore store)
    {
        _store = store;
        InitializeComponent();
        Loaded += LoadedAsync;
        Closing += OnClosing;
        Closed += (_, _) => { _preview?.Cancel(); _api.Dispose(); if (ReferenceEquals(_instance, this)) _instance = null; };
    }
    private async void LoadedAsync(object sender, RoutedEventArgs e)
    {
        if (_ready) return;
        _ready = true;
        await WorkAsync("读取仓库配置", async ct =>
        {
            _preferences = await Task.Run(_store.LoadPreferences, ct);
            _preferencesLoaded = true;
            Width = Math.Clamp(_preferences.Width, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
            Height = Math.Clamp(_preferences.Height, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
            try { _credential = await Task.Run(_store.LoadCredential, ct); }
            catch (Exception ex) { StatusText.Text = "凭据读取失败，请重新连接：" + ex.Message; }
            UpdateAccount(); BindRepositories();
            var entry = _preferences.Repositories.FirstOrDefault(r => r.Path == _preferences.SelectedPath) ?? _preferences.Repositories.FirstOrDefault();
            if (entry is not null) { _binding = true; Repositories.SelectedItem = entry; _binding = false; await RefreshRepositoryAsync(entry, ct); }
            await LoadRemoteCacheAsync(ct);
        });
    }
    private RepositoryEntry? Selected => Repositories.SelectedItem as RepositoryEntry;
    private string Remote => RemotePicker.SelectedItem as string ?? throw new InvalidOperationException("请选择远程仓库。");
    private GitState State => _state ?? throw new InvalidOperationException("请先添加或选择本地仓库。");
    private void UpdateAccount() => AccountButton.Content = _credential is null ? "连接 GitHub 账户" : "GitHub · " + _credential.Login;
    private void BindRepositories()
    {
        _binding = true;
        var selected = Selected;
        Repositories.ItemsSource = _preferences.Repositories.Where(r => (r.Name + r.Path).Contains(RepositorySearch.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        Repositories.SelectedItem = selected;
        _binding = false;
    }
    private Task SaveAsync() => _preferencesLoaded ? Task.Run(() => _store.SavePreferences(_preferences)) : throw new InvalidOperationException("仓库配置尚未成功读取，不会覆盖原文件。");
    private async Task WorkAsync(string title, Func<CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        _preview?.Cancel();
        using var cts = new CancellationTokenSource();
        _operation = cts; Workspace.IsEnabled = false; BusyProgress.Visibility = CancelButton.Visibility = Visibility.Visible; StatusText.Text = title + "…";
        try { await action(cts.Token); StatusText.Text = title + "完成"; }
        catch (OperationCanceledException) { StatusText.Text = "操作已取消；写操作可能已部分完成，请刷新检查。"; }
        catch (Exception ex) { ShowError(ex); }
        finally { _operation = null; Workspace.IsEnabled = true; BusyProgress.Visibility = CancelButton.Visibility = Visibility.Collapsed; UpdateButtons(); }
    }
    private void ShowError(Exception ex)
    {
        string message = GitRepositoryService.Sanitize(ex.Message);
        StatusText.Text = message;
        MessageBox.Show(this, message, "GitHub 仓库中心", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void UpdateButtons()
    {
        GitToolbar.IsEnabled = _state is not null;
        StageButton.IsEnabled = Changes.SelectedItem is GitChange { Staged: false, Conflict: false };
        UnstageButton.IsEnabled = Changes.SelectedItem is GitChange { Staged: true, Conflict: false };
        CommitButton.IsEnabled = _state?.Changes.Any(c => c.Staged) == true && !_state.Changes.Any(c => c.Conflict);
        LoadMoreButton.IsEnabled = _more;
    }
    private async Task RefreshRepositoryAsync(RepositoryEntry entry, CancellationToken ct)
    {
        ClearRepository();
        RepositoryTitle.Text = entry.Name; RepositoryPath.Text = entry.Path;
        try
        {
            var state = await _git.StatusAsync(entry.Path, ct);
            var branches = await _git.BranchesAsync(state.Root, ct);
            var remotes = await _git.RemotesAsync(state.Root, ct);
            var history = await _git.HistoryAsync(state, ct);
            _state = state; entry.Summary = state.Summary; entry.LastChecked = DateTimeOffset.Now;
            CommitMessage.Text = _drafts.GetValueOrDefault(state.Root, "");
            _preferences.SelectedPath = entry.Path;
            Changes.ItemsSource = state.Changes; ChangesSummary.Text = state.Summary;
            BranchPicker.ItemsSource = branches; BranchPicker.SelectedItem = state.Branch;
            RemotePicker.ItemsSource = remotes; RemotePicker.SelectedItem = remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault();
            History.ItemsSource = history;
            RepositoryPath.Text = state.Root + (entry.LastFetched is { } fetched ? $" · 上次获取 {fetched.LocalDateTime:MM-dd HH:mm}" : " · 尚未获取远程状态");
            BindRepositories();
            await SaveAsync();
            if (Views.SelectedIndex == 2) await LoadOverviewCacheAsync(ct);
        }
        catch { entry.Summary = "读取失败 · 状态未知"; BindRepositories(); throw; }
    }
    private void ClearRepository()
    {
        if (_state is { } previous) _drafts[previous.Root] = CommitMessage.Text;
        _state = null; Changes.ItemsSource = null; History.ItemsSource = null; BranchPicker.ItemsSource = null; RemotePicker.ItemsSource = null;
        PullRequests.ItemsSource = null; Runs.ItemsSource = null; OverviewTime.Text = "尚未读取"; PullStatus.Text = "开放的 PR"; RunStatus.Text = "最近构建";
        Diff.Text = "选择文件查看差异"; CommitDiff.Clear(); CommitMessage.Clear(); ChangesSummary.Text = "未读取";
    }
    private async void Repository_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_binding || !_ready || _operation is not null) return;
        if (Selected is { } entry) await WorkAsync("读取仓库", ct => RefreshRepositoryAsync(entry, ct));
        else { ClearRepository(); RepositoryTitle.Text = "仓库工作台"; RepositoryPath.Text = "尚未选择本地仓库"; UpdateButtons(); }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _binding) return;
        BindRepositories();
        if (Selected is null) { ClearRepository(); RepositoryTitle.Text = "仓库工作台"; RepositoryPath.Text = "尚未选择本地仓库"; UpdateButtons(); }
    }
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "添加已有 Git 仓库" };
        if (dialog.ShowDialog(this) != true) return;
        await WorkAsync("添加仓库", async ct => await AddRepositoryAsync(dialog.FolderName, ct));
    }
    private async Task AddRepositoryAsync(string path, CancellationToken ct)
    {
        var state = await _git.StatusAsync(path, ct);
        var entry = _preferences.Repositories.FirstOrDefault(r => string.Equals(r.Path, state.Root, StringComparison.OrdinalIgnoreCase));
        if (entry is null) { entry = new() { Path = state.Root }; _preferences.Repositories.Add(entry); }
        _binding = true; RepositorySearch.Clear(); _binding = false;
        BindRepositories(); _binding = true; Repositories.SelectedItem = entry; _binding = false;
        await RefreshRepositoryAsync(entry, ct);
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } entry) await WorkAsync("刷新本地状态", ct => RefreshRepositoryAsync(entry, ct));
    }
    private async void RefreshAll_Click(object sender, RoutedEventArgs e) => await WorkAsync("检查所有仓库", async ct =>
    {
        foreach (var entry in _preferences.Repositories)
        {
            ct.ThrowIfCancellationRequested();
            try { entry.Summary = (await _git.StatusAsync(entry.Path, ct)).Summary; entry.LastChecked = DateTimeOffset.Now; }
            catch (OperationCanceledException) { throw; }
            catch { entry.Summary = "读取失败 · 状态未知"; }
        }
        BindRepositories(); await SaveAsync();
        if (Selected is { } current) await RefreshRepositoryAsync(current, ct);
    });
    private async void Change_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        if (_state is not { } state || Changes.SelectedItem is not GitChange change) return;
        _preview?.Cancel(); var cts = _preview = new(); Diff.Text = "正在读取差异…";
        try { string text = await _git.DiffAsync(state, change, cts.Token); if (!cts.IsCancellationRequested && ReferenceEquals(_preview, cts)) Diff.Text = text; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) Diff.Text = GitRepositoryService.Sanitize(ex.Message); }
    }
    private bool EnsureTrust()
    {
        var entry = Selected;
        if (entry is null) return false;
        if (entry.Trusted) return true;
        if (!Confirm("信任此仓库", $"{entry.Path}\n\nGit 操作可能执行该仓库或系统配置中的 hooks、过滤器和凭据工具。仅对你信任的项目启用写操作。\n\n是否信任并继续？")) return false;
        entry.Trusted = true;
        return true;
    }
    private async Task WriteAsync(string title, Func<GitState, CancellationToken, Task> action)
    {
        if (_state is null || Selected is not { } entry || !EnsureTrust()) return;
        var state = _state;
        await WorkAsync(title, async ct =>
        {
            await SaveAsync();
            try { await action(state, ct); }
            finally
            {
                // 取消或失败也重新读取，绝不假定 Git 子进程已回滚。
                try { await RefreshRepositoryAsync(entry, CancellationToken.None); }
                catch { ClearRepository(); entry.Summary = "状态未知，请刷新"; BindRepositories(); }
            }
        });
    }
    private async void Stage_Click(object sender, RoutedEventArgs e)
    {
        if (Changes.SelectedItem is GitChange change) await WriteAsync("暂存文件", (s, ct) => _git.StageAsync(s, change, true, ct));
    }
    private async void Unstage_Click(object sender, RoutedEventArgs e)
    {
        if (Changes.SelectedItem is GitChange change) await WriteAsync("取消暂存", (s, ct) => _git.StageAsync(s, change, false, ct));
    }
    private async void Commit_Click(object sender, RoutedEventArgs e)
    {
        string message = CommitMessage.Text.Trim();
        if (message.Length == 0) { CommitMessage.Focus(); return; }
        await WriteAsync("本地提交", async (s, ct) =>
        {
            var plan = await _git.PrepareCommitAsync(s, ct);
            if (Confirm("确认本地提交", plan.Preview + "\n\n提交说明：\n" + message))
            {
                await _git.CommitAsync(s, plan, message, ct);
                CommitMessage.Clear(); _drafts.Remove(s.Root);
            }
        });
    }
    private async void Fetch_Click(object sender, RoutedEventArgs e) => await WriteAsync("获取远程", async (s, ct) => { await _git.FetchAsync(s.Root, Remote, ct); Selected!.LastFetched = DateTimeOffset.Now; });
    private async void Pull_Click(object sender, RoutedEventArgs e) => await WriteAsync("快进拉取", async (s, ct) =>
    {
        if (!Confirm("确认快进拉取", $"{s.Branch} ← {s.Upstream}\n\n将获取远程并更新本地文件。分支分叉或有本地改动时停止，不自动合并、不自动 stash。")) return;
        await _git.PullAsync(s, Remote, ct); Selected!.LastFetched = DateTimeOffset.Now;
    });
    private async void Push_Click(object sender, RoutedEventArgs e) => await WriteAsync("推送", async (s, ct) =>
    {
        var plan = await _git.PreparePushAsync(s, Remote, ct);
        if (Confirm("确认推送至远程", plan.Preview)) await _git.PushAsync(s, plan, ct);
    });
    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (BranchPicker.SelectedItem is string branch) await WriteAsync("切换分支", (s, ct) => _git.SwitchAsync(s, branch, false, ct));
    }
    private async void NewBranch_Click(object sender, RoutedEventArgs e)
    {
        string? branch = Input("新建并切换分支", "分支名称", "codex/");
        if (branch is not null) await WriteAsync("新建分支", (s, ct) => _git.SwitchAsync(s, branch.Trim(), true, ct));
    }
    private async void History_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_state is not { } state || History.SelectedItem is not GitCommit commit) return;
        _preview?.Cancel(); var cts = _preview = new(); CommitDiff.Text = "正在读取提交…";
        try { string text = await _git.ShowCommitAsync(state.Root, commit, cts.Token); if (!cts.IsCancellationRequested) CommitDiff.Text = text.Length > 512 * 1024 ? text[..(512 * 1024)] + "\n（预览已截断）" : text; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) CommitDiff.Text = GitRepositoryService.Sanitize(ex.Message); }
    }
    private async void Account_Click(object sender, RoutedEventArgs e)
    {
        if (_credential is not null)
        {
            if (!Confirm("断开 GitHub 账户", $"当前账户：{_credential.Login}\n\n删除本机保存的 API 令牌及远程缓存？不会删除本地仓库，也不修改系统 Git 凭据。")) return;
            await WorkAsync("断开账户", async ct => { await Task.Run(_store.Disconnect, ct); _credential = null; _remoteRepositories.Clear(); RemoteRepositories.ItemsSource = null; PullRequests.ItemsSource = null; Runs.ItemsSource = null; OverviewTime.Text = "尚未读取"; RemoteStatus.Text = "未连接账户"; _more = false; UpdateAccount(); });
            return;
        }
        string? token = Input("连接 GitHub", "Personal Access Token（仅保存在当前 Windows 用户的加密凭据中）", "", true);
        if (token is null) return;
        await WorkAsync("验证 GitHub 账户", async ct =>
        {
            string login = await _api.LoginAsync(token, ct);
            var credential = new GitHubCredential(login, token.Trim());
            await Task.Run(() => _store.SaveCredential(credential), ct);
            _credential = credential; UpdateAccount();
            await ReadRemotesAsync(1, ct);
            Views.SelectedIndex = 3;
        });
    }
    private string AccountKey => _credential?.Login ?? "public";
    private async Task LoadRemoteCacheAsync(CancellationToken ct)
    {
        if (_credential is null) return;
        var cached = await Task.Run(() => _store.LoadCache<List<RemoteRepository>>(AccountKey + ":repos"), ct);
        if (cached is null) return;
        _remoteRepositories.Clear(); _remoteRepositories.AddRange(cached.Value);
        RemoteRepositories.ItemsSource = _remoteRepositories.ToArray();
        RemoteStatus.Text = $"缓存 · {cached.SavedAt.LocalDateTime:MM-dd HH:mm} · {_remoteRepositories.Count} 个仓库";
    }
    private async Task ReadRemotesAsync(int page, CancellationToken ct)
    {
        if (_credential is null) throw new InvalidOperationException("请先连接 GitHub 账户。");
        var entries = await _api.RepositoriesAsync(_credential.Token, page, ct);
        if (page == 1) _remoteRepositories.Clear();
        foreach (var entry in entries) if (!_remoteRepositories.Any(r => r.Name == entry.Name)) _remoteRepositories.Add(entry);
        _remotePage = page; _more = entries.Count == 100;
        RemoteRepositories.ItemsSource = _remoteRepositories.ToArray();
        RemoteStatus.Text = $"已读取 {_remoteRepositories.Count} 个 · {DateTime.Now:HH:mm} · {(_more ? "可继续加载" : "列表结束")}";
        await Task.Run(() => _store.SaveCache(AccountKey + ":repos", _remoteRepositories), ct);
    }
    private async void RemoteRefresh_Click(object sender, RoutedEventArgs e) => await WorkAsync("读取远程仓库", ct => ReadRemotesAsync(1, ct));
    private async void RemoteMore_Click(object sender, RoutedEventArgs e) => await WorkAsync("加载下一页", ct => ReadRemotesAsync(_remotePage + 1, ct));
    private async Task<string> SlugAsync(CancellationToken ct)
    {
        string url = await _git.RemoteUrlAsync(State.Root, Remote, false, ct);
        return GitRepositoryService.GitHubSlug(url) ?? throw new InvalidOperationException("当前远程不是支持的 github.com 仓库地址。");
    }
    private void DisplayOverview(GitHubOverview value)
    {
        PullRequests.ItemsSource = value.PullRequests; Runs.ItemsSource = value.Runs; PullStatus.Text = value.PullStatus; RunStatus.Text = value.RunStatus;
    }
    private async Task LoadOverviewCacheAsync(CancellationToken ct)
    {
        if (_state is null || RemotePicker.SelectedItem is null) return;
        string slug;
        try { slug = await SlugAsync(ct); } catch (InvalidOperationException) { return; }
        var cached = await Task.Run(() => _store.LoadCache<GitHubOverview>(AccountKey + ":" + slug), ct);
        if (cached is not null) { DisplayOverview(cached.Value); OverviewTime.Text = $"缓存 · {cached.SavedAt.LocalDateTime:MM-dd HH:mm}"; }
    }
    private async void Overview_Click(object sender, RoutedEventArgs e) => await WorkAsync("读取 GitHub 状态", async ct =>
    {
        string slug = await SlugAsync(ct);
        var value = await _api.OverviewAsync(slug, _credential?.Token, ct);
        DisplayOverview(value); OverviewTime.Text = $"{slug} · {DateTime.Now:HH:mm}";
        await Task.Run(() => _store.SaveCache(AccountKey + ":" + slug, value), ct);
    });
    private async void View_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Views || !_ready || _operation is not null) return;
        if (Views.SelectedIndex == 2) await WorkAsync("读取状态缓存", LoadOverviewCacheAsync);
    }
    private async void Remote_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _operation is not null) return;
        PullRequests.ItemsSource = null; Runs.ItemsSource = null; OverviewTime.Text = "尚未读取";
        PullStatus.Text = "开放的 PR"; RunStatus.Text = "最近构建";
        if (Views.SelectedIndex == 2) await WorkAsync("读取状态缓存", LoadOverviewCacheAsync);
    }
    private async Task CloneInteractiveAsync(string url)
    {
        string? value = Input("克隆 GitHub 仓库", "HTTPS 仓库地址（Git 凭据沿用系统配置）", url);
        if (value is null) return;
        var folder = new OpenFolderDialog { Title = "选择克隆位置的父目录" };
        if (folder.ShowDialog(this) != true) return;
        string? name = Input("新建克隆目录", "新目录名称", "repository");
        if (name is null) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || string.IsNullOrWhiteSpace(name)) { ShowError(new InvalidOperationException("目录名称无效。")); return; }
        string path = Path.Combine(folder.FolderName, name);
        await WorkAsync("克隆仓库", async ct => { await _git.CloneAsync(value.Trim(), path, ct); await AddRepositoryAsync(path, ct); });
    }
    private async void Clone_Click(object sender, RoutedEventArgs e) => await CloneInteractiveAsync("");
    private async void RemoteClone_Click(object sender, RoutedEventArgs e) { if (RemoteRepositories.SelectedItem is RemoteRepository repo) await CloneInteractiveAsync(repo.CloneUrl); }
    private void RemoteWeb_Click(object sender, RoutedEventArgs e) { if (RemoteRepositories.SelectedItem is RemoteRepository repo) OpenWeb(repo.Url); }
    private void Activity_Open(object sender, MouseButtonEventArgs e) { if (sender is ListBox { SelectedItem: GitHubActivity item }) OpenWeb(item.Url); }
    private void Activity_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && sender is ListBox { SelectedItem: GitHubActivity item }) { OpenWeb(item.Url); e.Handled = true; } }
    private void OpenWeb(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || uri.UserInfo.Length != 0) throw new InvalidOperationException("仅允许打开 github.com 的 HTTPS 页面。");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void Web_Click(object sender, RoutedEventArgs e) => await WorkAsync("打开仓库网页", async ct => OpenWeb("https://github.com/" + await SlugAsync(ct)));
    private void Folder_Click(object sender, RoutedEventArgs e) { if (Selected is { } entry) OpenFolder(entry.Path); }
    private void OpenFolder(string path, bool create = false)
    {
        try { if (create) Directory.CreateDirectory(path); if (!Directory.Exists(path)) throw new DirectoryNotFoundException("目录已不存在，不会自动重建代码目录。"); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false }); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Terminal_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        try { Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = entry.Path, UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_state is not { } state || Changes.SelectedItem is not GitChange change) return;
        try
        {
            string path = Path.Combine(state.Root, change.Path);
            if (!File.Exists(path)) throw new FileNotFoundException("文件不存在，可能已删除或重命名。");
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry || !Confirm("移除仓库记录", $"仅从列表移除 {entry.Name}，不删除本地文件。")) return;
        await WorkAsync("移除记录", async ct => { _preferences.Repositories.Remove(entry); _preferences.SelectedPath = ""; BindRepositories(); ClearRepository(); RepositoryTitle.Text = "仓库工作台"; RepositoryPath.Text = "尚未选择本地仓库"; await SaveAsync(); });
    }
    private async void Settings_Click(object sender, RoutedEventArgs e) => await WorkAsync("读取存储设置", async ct =>
    {
        long size = await Task.Run(_store.CacheSize, ct);
        var dialog = Dialog("GitHub 中心设置", out var panel);
        panel.Children.Add(new TextBlock { Text = $"远程缓存  ·  {size / 1024d / 1024d:F2} MiB", FontSize = 16, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBox { Text = _store.CacheDirectory, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
        var open = DialogButton("打开缓存目录", false); open.Click += (_, _) => OpenFolder(_store.CacheDirectory, true); panel.Children.Add(open);
        var clear = DialogButton("清理远程缓存", false); clear.Click += async (_, _) => { if (!Confirm("清理缓存", "只删除远程列表与状态缓存，不删除凭据和本地代码。")) return; clear.IsEnabled = false; try { await Task.Run(_store.ClearCache); dialog.Close(); } catch (Exception ex) { ShowError(ex); clear.IsEnabled = true; } }; panel.Children.Add(clear);
        var close = DialogButton("关闭", true); close.Click += (_, _) => dialog.Close(); panel.Children.Add(close);
        dialog.ShowDialog();
    });
    private Window Dialog(string title, out StackPanel panel)
    {
        panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 18) });
        var dialog = new Window { Title = title, Owner = this, Width = 620, SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(500, ActualHeight - 40), WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, FontFamily = FontFamily, Foreground = Foreground, Background = new SolidColorBrush(Color.FromRgb(239, 242, 255)), Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/XTool;component/Mail/MailStyles.xaml", UriKind.Relative) });
        return dialog;
    }
    private Button DialogButton(string text, bool primary) => new() { Content = text, Style = (Style)FindResource(primary ? "GitPrimary" : "GitButton"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0), IsDefault = primary };
    private string? Input(string title, string label, string value, bool secret = false)
    {
        var dialog = Dialog(title, out var panel);
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var text = new TextBox { Text = value }; var password = new PasswordBox();
        panel.Children.Add(secret ? password : text);
        if (secret)
        {
            var help = DialogButton("创建细粒度令牌", false); help.Click += (_, _) => OpenWeb("https://github.com/settings/personal-access-tokens/new"); panel.Children.Add(help);
        }
        var ok = DialogButton(secret ? "连接" : "确定", true); ok.Click += (_, _) => dialog.DialogResult = true; panel.Children.Add(ok);
        var cancel = DialogButton("取消", false); cancel.IsCancel = true; panel.Children.Add(cancel);
        dialog.Loaded += (_, _) => { if (secret) password.Focus(); else { text.Focus(); text.SelectAll(); } };
        bool accepted = dialog.ShowDialog() == true;
        string result = secret ? password.Password : text.Text;
        password.Clear(); return accepted ? result : null;
    }
    private bool Confirm(string title, string content)
    {
        var dialog = Dialog(title, out var panel);
        panel.Children.Add(new TextBox { Text = content, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var ok = DialogButton("确认", true); ok.Click += (_, _) => dialog.DialogResult = true; panel.Children.Add(ok);
        var cancel = DialogButton("取消", false); cancel.IsCancel = true; panel.Children.Add(cancel);
        return dialog.ShowDialog() == true;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_operation is not null) { e.Cancel = true; StatusText.Text = "请等待当前操作完成，或先取消操作再关闭。"; return; }
        if (!_preferencesLoaded) return;
        try { _preferences.Width = RestoreBounds.Width; _preferences.Height = RestoreBounds.Height; _store.SavePreferences(_preferences); } catch (Exception ex) { e.Cancel = true; ShowError(ex); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
