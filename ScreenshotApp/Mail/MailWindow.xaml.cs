using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MimeKit;
using ScreenshotApp.Translation;
using System.Windows.Documents;

namespace ScreenshotApp.Mail;

public partial class MailWindow : Window
{
    private static MailWindow? _current;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _translation;
    private FlowDocument? _originalDocument;
    private string? _translatedText;
    private string? _translatedSubject;
    private int _retainedTranslationSegments;
    private bool _showingTranslation;
    private readonly CancellationTokenSource _lifetime = new();
    private long _revision = -1;
    private bool _updating;
    private string? _account;
    private string _mode = "all";
    private MailRow? _selected;
    private MailSnapshot _snapshot = new(0, [], [], "");
    public static void Open()
    {
        if (_current is null) _current = new();
        _current.Show();
        if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
        _current.Activate();
    }
    public static void HidePrivateContent()
    {
        if (_current is null) return;
        _current.ClearReader();
        _current.Hide();
    }
    public MailWindow()
    {
        InitializeComponent();
        WindowSurface.SizeChanged += (_, _) => WindowSurface.Clip = new System.Windows.Media.RectangleGeometry(new Rect(0,0,WindowSurface.ActualWidth,WindowSurface.ActualHeight), WindowState == WindowState.Maximized ? 0 : 16, WindowState == WindowState.Maximized ? 0 : 16);
        RestoreLayout();
        MailSmoothScroll.Attach(Messages, ListBottomFade, ListScrollHint);
        _timer.Tick += (_, _) => UpdateSnapshot();
        Loaded += (_, _) => { UpdateSnapshot(); _timer.Start(); };
        Closing += (_, _) => SaveLayout();
        Closed += (_, _) => { _timer.Stop(); _loading?.Cancel(); _translation?.Cancel(); _lifetime.Cancel(); _current = null; };
    }
    private sealed record LayoutData(double Account, double ListRatio, double Width, double Height);
    private static string LayoutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "Mail", "layout.json");
    private void RestoreLayout()
    {
        try
        {
            if (!File.Exists(LayoutPath)) return;
            var value = System.Text.Json.JsonSerializer.Deserialize<LayoutData>(File.ReadAllText(LayoutPath, System.Text.Encoding.UTF8));
            if (value is null || !double.IsFinite(value.Account) || !double.IsFinite(value.ListRatio)) return;
            AccountColumn.Width = new GridLength(Math.Clamp(value.Account,170,300));
            var ratio = Math.Clamp(value.ListRatio,0.20,0.65);
            ListColumn.Width = new GridLength(ratio,GridUnitType.Star); ReaderColumn.Width = new GridLength(1-ratio,GridUnitType.Star);
            if(double.IsFinite(value.Width) && double.IsFinite(value.Height))
            { Width = Math.Clamp(value.Width,MinWidth,Math.Max(MinWidth,SystemParameters.WorkArea.Width)); Height = Math.Clamp(value.Height,MinHeight,Math.Max(MinHeight,SystemParameters.WorkArea.Height)); }
        }
        catch { /* 损坏布局不影响邮箱数据加载。 */ }
    }
    private void SaveLayout()
    {
        if (!IsLoaded || ListColumn.ActualWidth + ReaderColumn.ActualWidth <= 0) return;
        try
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(0,0,ActualWidth,ActualHeight) : RestoreBounds;
            var data = new LayoutData(AccountColumn.ActualWidth,ListColumn.ActualWidth/(ListColumn.ActualWidth+ReaderColumn.ActualWidth),bounds.Width,bounds.Height);
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
            File.WriteAllText(LayoutPath+".tmp",System.Text.Json.JsonSerializer.Serialize(data),new System.Text.UTF8Encoding(false));
            File.Move(LayoutPath+".tmp",LayoutPath,true);
        }
        catch { /* 布局写入失败不打断收信和窗口关闭。 */ }
    }
    private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => SaveLayout();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ShowAccount(MailAccountView? account = null)
    {
        ModalShade.Visibility = Visibility.Visible;
        try { new MailAccountDialog(account) { Owner = this }.ShowDialog(); }
        finally { ModalShade.Visibility = Visibility.Collapsed; UpdateSnapshot(); }
    }
    private void UpdateSnapshot()
    {
        var state = MailService.Instance.Read();
        if (state.Revision == _revision) return;
        _snapshot = state; _revision = state.Revision;
        _updating = true;
        if (_account is null && state.Accounts.Length == 1) _account = state.Accounts[0].Id;
        Accounts.ItemsSource = state.Accounts;
        Accounts.SelectedItem = state.Accounts.FirstOrDefault(a => a.Id == _account);
        if (_account is not null && Accounts.SelectedItem is null) { _account = null; ClearReader(); }
        _updating = false;
        Notice.Text = state.Error.Length > 0 ? state.Error : state.Accounts.Length == 0
            ? "还没有邮箱。点击右上角“添加邮箱”开始；凭据仅在当前 Windows 用户下加密保存。"
            : $"{state.Accounts.Length} 个账户 · 最近邮件中 {state.Rows.Count(r => !r.Seen)} 封未读";
        AccountCount.Text = state.Accounts.Length.ToString();
        InboxCount.Text = state.Rows.Length.ToString();
        StarredCount.Text = state.Rows.Count(r => r.Flagged).ToString();
        Filter();
    }
    private void Filter()
    {
        if (!IsLoaded || _updating) return;
        var query = Search.Text.Trim();
        UnreadFilter.Content = $"未读 ({_snapshot.Rows.Count(r => (_account is null || r.AccountId == _account) && !r.Seen)})";
        var rows = _snapshot.Rows.Where(r => (_account is null || r.AccountId == _account) &&
            (_mode != "unread" || !r.Seen) && (_mode != "attachment" || r.HasAttachments) && (_mode != "flagged" || r.Flagged) && (query.Length == 0 ||
            r.Sender.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Select(r => r with { AccountLabel = _snapshot.Accounts.FirstOrDefault(a => a.Id == r.AccountId)?.Address ?? "" }).ToArray();
        ListCount.Text = $"{rows.Length}";
        var selectedAccount = _snapshot.Accounts.FirstOrDefault(a => a.Id == _account);
        if (_snapshot.Error.Length == 0 && selectedAccount is not null)
        {
            var synced = _snapshot.Rows.Count(r => r.AccountId == _account);
            Notice.Text = selectedAccount.InboxTotal is int total
                ? $"{selectedAccount.DisplayName} · 服务器收件箱 {total} 封 · 已同步 {synced} 封 · 当前显示 {rows.Length} 封"
                : $"{selectedAccount.DisplayName} · 缓存 {synced} 封 · {selectedAccount.Status}";
        }
        else if (_snapshot.Error.Length == 0 && _snapshot.Accounts.Length > 0)
            Notice.Text = $"{_snapshot.Accounts.Length} 个账户 · 最近邮件中 {_snapshot.Rows.Count(r => !r.Seen)} 封未读";
        ListEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListEmpty.Text = _snapshot.Accounts.Length == 0 ? "暂无邮件\n添加账户后，来信会显示在这里" : "没有符合条件的邮件";
        var keep = rows.FirstOrDefault(r => r.Key == _selected?.Key);
        _updating = true; Messages.ItemsSource = rows; Messages.SelectedItem = keep; _updating = false;
        if (keep is null && _selected is not null) ClearReader();
        else if (keep is not null) { _selected = keep; ReadButton.IsEnabled = !keep.Seen; }
        UpdateNavigation();
    }
    private void ClearReader()
    {
        _translation?.Cancel(); _translation = null;
        _originalDocument = null; _translatedText = null; _translatedSubject = null; _retainedTranslationSegments = 0; _showingTranslation = false;
        TranslateButton.IsEnabled = false; TranslateButton.ToolTip = "翻译为中文（本地离线）";
        _loading?.Cancel(); _selected = null;
        Reader.Visibility = Visibility.Collapsed; ReaderEmpty.Visibility = Visibility.Visible;
        Subject.Text = "选择一封邮件"; Sender.Text = ""; Body.Document = MailDocument.Create(""); Attachments.Children.Clear(); ReadButton.IsEnabled = false;
    }
    private void Account_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        _account = (Accounts.SelectedItem as MailAccountView)?.Id; Filter();
    }
    private void Filter_Changed(object sender, RoutedEventArgs e) => Filter();
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string mode }) _mode = mode;
        if (IsLoaded) Filter();
    }
    private void UpdateNavigation()
    {
        var index = Messages.SelectedIndex;
        Previous.IsEnabled = index > 0;
        Next.IsEnabled = index >= 0 && index + 1 < Messages.Items.Count;
        Position.Text = index < 0 ? "" : $"第 {index + 1} 封 / 共 {Messages.Items.Count} 封";
    }
    private void Previous_Click(object sender, RoutedEventArgs e) { if (Messages.SelectedIndex > 0) Messages.SelectedIndex--; Messages.ScrollIntoView(Messages.SelectedItem); }
    private void Next_Click(object sender, RoutedEventArgs e) { if (Messages.SelectedIndex + 1 < Messages.Items.Count) Messages.SelectedIndex++; Messages.ScrollIntoView(Messages.SelectedItem); }
    private void All_Click(object sender, RoutedEventArgs e)
    { Accounts.SelectedItem = null; _account = null; AllFilter.IsChecked = true; _mode = "all"; Filter(); }
    private void Starred_Click(object sender, RoutedEventArgs e)
    {
        Accounts.SelectedItem = null; _account = null;
        StarredFilter.IsChecked = true; _mode = "flagged"; Filter();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) { MailService.Instance.Refresh(); Notice.Text = "已请求检查新邮件；连接恢复后自动同步。"; }
    private void Add_Click(object sender, RoutedEventArgs e) => ShowAccount();
    private void Account_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: MailAccountView account } card) return;
        var menu = new ContextMenu { PlacementTarget = card };
        var edit = new MenuItem { Header = "编辑备注与账户设置" };
        edit.Click += (_, _) => ShowAccount(account);
        menu.Items.Add(edit);
        menu.IsOpen = true;
        e.Handled = true;
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (Accounts.SelectedItem is not MailAccountView account) { Notice.Text = "先在左侧选择要设置的邮箱。"; return; }
        ShowAccount(account);
    }
    private async void Message_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        ClearReader();
        if (Messages.SelectedItem is not MailRow row) return;
        _selected = row;
        Reader.Visibility = Visibility.Visible; ReaderEmpty.Visibility = Visibility.Collapsed;
        var cancel = _loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Subject.Text = row.Subject;
        SenderName.Text = row.SenderName; SenderInitial.Text = row.Logo is null ? row.Initial : "";
        SenderLogo.Source = row.Logo is null ? null : new System.Windows.Media.Imaging.BitmapImage(new Uri(row.Logo));
        Sender.Text = row.SenderAddress + "\n收件邮箱：" + row.AccountLabel + "  ·  " + row.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        UpdateNavigation();
        Body.Document = MailDocument.Create("正在读取…");
        try
        {
            var message = await Task.Run(() => MailService.Instance.LoadMessage(row, cancel.Token), cancel.Token);
            if (cancel.IsCancellationRequested || _selected?.Key != row.Key) return;
            // 仅解释白名单文本标签；没有浏览器和外部资源加载。
            Body.Document = MailDocument.Create(message.HtmlBody ?? message.TextBody ?? "", message.HtmlBody is not null);
            _originalDocument = Body.Document;
            TranslateButton.IsEnabled = !string.IsNullOrWhiteSpace(new TextRange(_originalDocument.ContentStart, _originalDocument.ContentEnd).Text);
            ReadButton.IsEnabled = !row.Seen;
            var parts = message.Attachments.Take(15).ToArray();
            if (parts.Length > 0) Attachments.Children.Add(new TextBlock { Text = $"随附附件 · {parts.Length} 个", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,12,0,4) });
            foreach (var part in parts)
            {
                var name = part is MimePart mp ? mp.FileName : part.ContentDisposition?.FileName;
                var fileName = MailRules.SafeFileName(name);
                var panel = new DockPanel();
                var type = new TextBlock { Text = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant(), FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.RoyalBlue, Width = 42, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(type, Dock.Left); panel.Children.Add(type);
                var save = new TextBlock { Text = "↓ 保存", Foreground = System.Windows.Media.Brushes.SlateGray, Margin = new Thickness(12,0,0,0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(save, Dock.Right); panel.Children.Add(save);
                panel.Children.Add(new TextBlock { Text = fileName, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var button = new Button { Content = panel, Margin = new Thickness(0,6,0,0), ToolTip = fileName, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(246,248,254)) };
                button.Click += async (_, _) =>
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog { FileName = fileName, Filter = "所有文件|*.*" };
                    if (dialog.ShowDialog(this) != true) return;
                    button.IsEnabled = false;
                    try
                    {
                        await using var output = File.Create(dialog.FileName);
                        if (part is MimePart attachment) await attachment.Content!.DecodeToAsync(output, _lifetime.Token);
                        else if (part is MessagePart attached) await attached.Message!.WriteToAsync(output, _lifetime.Token);
                        Notice.Text = "附件已保存。";
                    }
                    catch (Exception ex) { Notice.Text = "保存失败：" + MailService.FriendlyError(ex); }
                    finally { button.IsEnabled = true; }
                };
                Attachments.Children.Add(button);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancel.IsCancellationRequested) Body.Document = MailDocument.Create(MailService.FriendlyError(ex)); }
        finally { if (ReferenceEquals(_loading, cancel)) _loading = null; cancel.Dispose(); }
    }
    private async void Read_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } row) return;
        ReadButton.IsEnabled = false;
        try { await MailService.Instance.MarkRead(row, _lifetime.Token); UpdateSnapshot(); }
        catch (Exception ex) { Notice.Text = MailService.FriendlyError(ex); if (_selected?.Key == row.Key) ReadButton.IsEnabled = true; }
    }
    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (_translation is not null) { _translation.Cancel(); return; }
        if (_selected is not { } row || _originalDocument is null) return;
        if (_showingTranslation)
        {
            Body.Document = _originalDocument; _showingTranslation = false;
            Subject.Text = row.Subject;
            TranslateButton.ToolTip = "翻译为中文（本地离线）";
            return;
        }
        if (_translatedText is not null) { ShowTranslation(_translatedText); return; }
        var text = new TextRange(_originalDocument.ContentStart, _originalDocument.ContentEnd).Text.Trim();
        if (text.Length > 20000) { Notice.Text = "正文超过 20,000 字符，暂不支持整封离线翻译。"; return; }
        var engine = TranslationEngineProvider.Default;
        if (!engine.IsReady) { Notice.Text = engine.UnavailableReason; return; }
        var cancel = _translation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        TranslateButton.ToolTip = "正在翻译，点击取消";
        Notice.Text = "正在本地翻译为中文…";
        try
        {
            var progress = new Progress<int>(count =>
            {
                if (ReferenceEquals(_translation, cancel) && !cancel.IsCancellationRequested)
                    Notice.Text = $"正在本地翻译标题与正文 · 已处理 {count} 个片段…";
            });
            var result = await Task.Run(() => MailTranslation.TranslateAsync(row.Subject, text, engine, cancel.Token, progress), cancel.Token);
            if (cancel.IsCancellationRequested || _selected?.Key != row.Key) return;
            if (string.IsNullOrWhiteSpace(result.Body)) { Notice.Text = "未生成译文，已保留原文。"; return; }
            _translatedText = result.Body;
            _translatedSubject = result.Subject;
            _retainedTranslationSegments = result.RetainedSegments;
            ShowTranslation(_translatedText);
        }
        catch (OperationCanceledException) { if (_selected?.Key == row.Key) Notice.Text = "已取消翻译，保留原文。"; }
        catch (Exception) { if (_selected?.Key == row.Key) Notice.Text = "本地翻译失败，已保留原文，请稍后重试。"; }
        finally
        {
            if (ReferenceEquals(_translation, cancel))
            {
                _translation = null;
                TranslateButton.ToolTip = _showingTranslation ? "显示原文" : "翻译为中文（本地离线）";
            }
            cancel.Dispose();
        }
    }
    private void ShowTranslation(string text)
    {
        Body.Document = MailDocument.Create(text);
        Subject.Text = _translatedSubject ?? _selected?.Subject ?? "";
        _showingTranslation = true;
        TranslateButton.ToolTip = "显示原文";
        Notice.Text = _retainedTranslationSegments > 0
            ? $"本地译文 · {_retainedTranslationSegments} 个片段翻译异常，已保留对应原文"
            : "本地译文 · 重要信息请核对原文";
    }
}
