using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenshotApp.Mail;

/// <summary>授权码由用户在本机输入，不写入日志、不经手机或第三方中转。</summary>
public sealed class MailAccountDialog : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    public MailAccountDialog(MailAccountView? account = null)
    {
        Title = account is null ? "添加邮箱 · X-Tool" : "邮箱账户设置 · X-Tool";
        Width = account is null ? 900 : 560; Height = 650; MinHeight = 540;
        MaxHeight = SystemParameters.WorkArea.Height - 36;
        MaxWidth = SystemParameters.WorkArea.Width - 36;
        ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Background = Brushes.Transparent;
        Foreground = new SolidColorBrush(Color.FromRgb(21, 44, 72));
        Resources = new ResourceDictionary { Source = new Uri("/XTool;component/Mail/MailStyles.xaml", UriKind.Relative) };
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        { CaptionHeight = 48, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var frame = new Border { CornerRadius = new CornerRadius(16), Background = new LinearGradientBrush(Color.FromRgb(235,246,255), Color.FromRgb(249,239,251), 35), BorderThickness = new Thickness(0),
            BorderBrush = new SolidColorBrush(Color.FromRgb(217, 228, 243)), Child = layout };
        Content = frame;
        frame.SizeChanged += (_, _) => frame.Clip = new RectangleGeometry(new Rect(0,0,frame.ActualWidth,frame.ActualHeight),16,16);
        var caption = new DockPanel { Margin = new Thickness(24, 0, 12, 0) };
        var close = new Button { Content = "×", Width = 36, Height = 32, Padding = new Thickness(0), FontSize = 22, Background = Brushes.Transparent };
        close.Click += (_, _) => Close();
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        DockPanel.SetDock(close, Dock.Right); caption.Children.Add(close);
        caption.Children.Add(new TextBlock { Text = Title, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(116, 135, 162)), VerticalAlignment = VerticalAlignment.Center });
        layout.Children.Add(caption);
        var panel = new StackPanel { Margin = new Thickness(30, 4, 30, 24) };
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        panel.Children.Add(new TextBlock { Text = account is null ? "添加邮箱账户" : "账户设置", FontSize = 26, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = account is null ? "连接你的收件箱，让新邮件找到你。" : account.Address, Foreground = new SolidColorBrush(Color.FromRgb(116, 135, 162)), Margin = new Thickness(0, 0, 0, 24), TextWrapping = TextWrapping.Wrap });
        Closed += (_, _) => _cancel.Cancel();
        if (account is null) BuildAdd(panel); else BuildPreferences(panel, account);
        _status.Foreground = new SolidColorBrush(Color.FromRgb(177, 75, 51));
        panel.Children.Add(_status);
    }
    private static void Label(Panel panel, string text)
        => panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 6), TextWrapping = TextWrapping.Wrap });
    private static Button ActionButton(string text) => new() { Content = new TextBlock { Text = text, Foreground = Brushes.White }, Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)), Foreground = Brushes.White, Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(14, 10, 14, 10) };
    private void BuildAdd(StackPanel panel)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        panel.Children.Add(grid);
        var providers = new StackPanel();
        grid.Children.Add(providers);
        Label(providers, "选择邮箱服务");
        var qq = new Button { Content = "QQ 邮箱", HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(0, 4, 0, 10) };
        var gmail = new Button { Content = "Google (Gmail)", HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(18, 16, 18, 16) };
        var enterprise = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(18,16,18,16), Margin = new Thickness(0,10,0,0) };
        providers.Children.Add(qq); providers.Children.Add(gmail); providers.Children.Add(enterprise);
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(143, 104, 47)), FontSize = 12, LineHeight = 22 };
        providers.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(255, 249, 233)), BorderBrush = new SolidColorBrush(Color.FromRgb(248, 227, 169)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 24, 0, 0), Child = hint });
        var form = new StackPanel();
        Grid.SetColumn(form, 2); grid.Children.Add(form);
        var heading = new TextBlock { FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
        form.Children.Add(heading);
        var subtitle = new TextBlock { Text = "使用预设的安全收信配置", Foreground = new SolidColorBrush(Color.FromRgb(116, 135, 162)), FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
        form.Children.Add(subtitle);
        Label(form, "邮箱地址");
        var address = new TextBox { MinHeight = 44 }; form.Children.Add(address);
        Label(form, "账户备注（可选，最多 40 字）");
        var remark = new TextBox { MinHeight = 44, MaxLength = 40, ToolTip = "例如：教育邮箱、工作邮箱；留空显示邮箱服务名称" }; form.Children.Add(remark);
        var secretLabel = new TextBlock { Margin = new Thickness(0, 14, 0, 8) }; form.Children.Add(secretLabel);
        var password = new PasswordBox { MinHeight = 44 }; form.Children.Add(password);
        var server = new TextBlock { FontSize = 13, LineHeight = 23, Foreground = new SolidColorBrush(Color.FromRgb(64, 91, 126)) };
        form.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(237, 243, 255)), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 14, 0, 0), Child = server });
        form.Children.Add(new TextBlock { Text = "凭据仅在当前 Windows 用户下加密保存。\n首次同步不会弹出历史邮件提醒。", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(116, 135, 162)), FontSize = 12, LineHeight = 21, Margin = new Thickness(0, 16, 0, 0) });
        Label(form, "连接方式");
        var connection = new ComboBox { Style = (Style)FindResource("MailTimePicker"), ItemsSource = new[] { "直连（默认）", "系统代理" }, SelectedIndex = 0 };
        form.Children.Add(connection);
        form.Children.Add(new TextBlock { Text = "直连沿用系统网络（含 TUN）；系统代理显式使用 Windows 代理。", FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,0) });
        var save = ActionButton("验证并添加邮箱"); form.Children.Add(save);
        string selected = "QQ";
        void SelectProvider(string value)
        {
            selected = value;
            password.Clear();
            heading.Text = value == "TencentExmail" ? "腾讯企业邮箱配置" : value == "QQ" ? "QQ 邮箱配置" : "Gmail 邮箱配置";
            secretLabel.Text = value == "TencentExmail" ? "客户端专用密码" : value == "QQ" ? "IMAP 独立授权码" : "Google 应用专用密码（16 位，不是两步验证备用码）";
            hint.Text = value == "TencentExmail" ? "授权提示\n\n适用于腾讯企业邮托管的学校或企业邮箱。\n\n先在网页版开启 IMAP/SMTP 服务，再到微信绑定／邮箱绑定中生成客户端专用密码。\n\n填写完整邮箱地址；不要填写微信登录密码。" : value == "QQ" ? "授权提示\n\n在 QQ 网页邮箱的设置中开启 IMAP 服务，生成独立授权码。\n\n此处无需填写邮箱登录密码。" : "授权提示\n\n先开启 Google 两步验证，再生成应用专用密码。\n\n部分组织或安全策略不支持此方式。默认直连。如需显式使用 Windows 代理，请在连接方式中选择。";
            server.Text = "接收服务器 · IMAP\n" + (value == "TencentExmail" ? "imap.exmail.qq.com" : value == "QQ" ? "imap.qq.com" : "imap.gmail.com") + "\n端口 993   ·   SSL / TLS";
            foreach (var pair in new[] { (Button: qq, Selected: value == "QQ", Text: "QQ 邮箱"), (Button: gmail, Selected: value == "Gmail", Text: "Google (Gmail)"), (Button: enterprise, Selected: value == "TencentExmail", Text: "腾讯企业邮箱") })
            {
                pair.Button.Background = pair.Selected ? new SolidColorBrush(Color.FromRgb(37, 99, 235)) : Brushes.White;
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var icon = new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(MailIdentity.ProviderLogo(pair.Button == enterprise ? "TencentExmail" : pair.Button == qq ? "QQ" : "Gmail")!)), Width = 22, Height = 22 };
                row.Children.Add(new Border { Child = icon, Background = pair.Button == enterprise ? Brushes.White : Brushes.Transparent, CornerRadius = new CornerRadius(5), Padding = new Thickness(2), Margin = new Thickness(0,0,8,0) });
                row.Children.Add(new TextBlock { Text = pair.Text, VerticalAlignment = VerticalAlignment.Center, Foreground = pair.Selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(35, 60, 89)), FontWeight = FontWeights.SemiBold });
                pair.Button.Content = row;
            }
        }
        qq.Click += (_, _) => SelectProvider("QQ");
        gmail.Click += (_, _) => SelectProvider("Gmail");
        enterprise.Click += (_, _) => SelectProvider("TencentExmail");
        SelectProvider("QQ");
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false; qq.IsEnabled = gmail.IsEnabled = enterprise.IsEnabled = remark.IsEnabled = address.IsEnabled = password.IsEnabled = connection.IsEnabled = false;
            _status.Text = "正在验证连接…";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await MailService.Instance.AddAsync(selected, address.Text, password.Password, timeout.Token, connection.SelectedIndex == 1, remark.Text);
                password.Clear();
                if (!_cancel.IsCancellationRequested) Close();
            }
            catch (Exception ex) { if (!_cancel.IsCancellationRequested) _status.Text = MailService.FriendlyError(ex); }
            finally { save.IsEnabled = true; qq.IsEnabled = gmail.IsEnabled = enterprise.IsEnabled = remark.IsEnabled = address.IsEnabled = password.IsEnabled = connection.IsEnabled = true; }
        };
        Closed += (_, _) => password.Clear();
    }
    private void BuildPreferences(StackPanel panel, MailAccountView account)
    {
        Label(panel, "账户备注（可选，最多 40 字）");
        var remark = new TextBox { Text = account.Remark ?? "", MaxLength = 40, ToolTip = "仅修改本机显示名称，不改变邮箱地址、密码或服务器邮件" }; panel.Children.Add(remark);
        var alerts = new CheckBox { Style = (Style)FindResource("MailToggle"), Content = "新邮件提醒", IsChecked = account.Alerts, Margin = new Thickness(0, 12, 0, 12) };
        var preview = new CheckBox { Style = (Style)FindResource("MailToggle"), Content = "提醒中显示发件人和主题", IsChecked = account.Preview, Margin = new Thickness(0, 12, 0, 12) };
        var quiet = new CheckBox { Style = (Style)FindResource("MailToggle"), Content = "启用免打扰时段（电脑本地时间）", IsChecked = account.Quiet, Margin = new Thickness(0, 12, 0, 12) };
        panel.Children.Add(alerts); panel.Children.Add(preview); panel.Children.Add(quiet);
        var hours = Enumerable.Range(0, 24).Select(h => h.ToString("00") + ":00").ToArray();
        var start = new ComboBox { Style = (Style)FindResource("MailTimePicker"), ItemsSource = hours, SelectedIndex = account.QuietStart, Width = 140, Padding = new Thickness(8) };
        var end = new ComboBox { Style = (Style)FindResource("MailTimePicker"), ItemsSource = hours, SelectedIndex = account.QuietEnd, Width = 140, Padding = new Thickness(8) };
        var times = new StackPanel { Orientation = Orientation.Horizontal };
        times.IsEnabled = account.Quiet;
        quiet.Checked += (_, _) => times.IsEnabled = true;
        quiet.Unchecked += (_, _) => times.IsEnabled = false;
        times.Margin = new Thickness(0,8,0,8);
        times.Children.Add(start); times.Children.Add(new TextBlock { Text = "  至  ", VerticalAlignment = VerticalAlignment.Center }); times.Children.Add(end);
        panel.Children.Add(times);
        Label(panel, "相同开始与结束时间表示全天免打扰。免打扰和锁屏期间仍收信，不补发气泡。");
        Label(panel, "连接方式");
        var connection = new ComboBox { Style = (Style)FindResource("MailTimePicker"), ItemsSource = new[] { "直连（默认）", "系统代理" }, SelectedIndex = account.UseSystemProxy ? 1 : 0 };
        panel.Children.Add(connection);
        BuildStorage(panel, account.Id);
        var save = ActionButton("保存设置"); panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            try { MailService.Instance.Preferences(account.Id, alerts.IsChecked == true, preview.IsChecked == true, quiet.IsChecked == true, start.SelectedIndex, end.SelectedIndex, connection.SelectedIndex == 1, remark.Text); Close(); }
            catch (Exception ex) { _status.Text = MailService.FriendlyError(ex); }
        };
        var remove = new Button { Content = "移除此账户", Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(12) };
        panel.Children.Add(remove);
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "从本机移除账户及其摘要？已下载的加密邮件文件将保留在存储目录，但不再显示。邮箱服务器上的邮件不会删除。", "移除邮箱", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { MailService.Instance.Remove(account.Id); Close(); }
            catch (Exception ex) { _status.Text = MailService.FriendlyError(ex); }
        };
    }
    private void BuildStorage(StackPanel panel, string accountId)
    {
        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(217, 228, 243)), Margin = new Thickness(0, 22, 0, 4) });
        Label(panel, "本地邮件存储");
        var location = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 12, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), Text = "正在读取…" };
        var pathRow = new DockPanel { LastChildFill = true };
        var openFolder = new Button { Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "打开邮箱缓存文件夹", IsEnabled = false, Content = new TextBlock { Text = "\uE8B7", FontFamily = new FontFamily("Segoe Fluent Icons"), Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235)), FontSize = 17 } };
        System.Windows.Automation.AutomationProperties.SetName(openFolder, "打开邮箱缓存文件夹");
        DockPanel.SetDock(openFolder, Dock.Right); pathRow.Children.Add(openFolder); pathRow.Children.Add(location);
        panel.Children.Add(pathRow);
        var usage = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), LineHeight = 21 };
        panel.Children.Add(usage);
        var warning = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(177, 75, 51)), Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(warning);
        string? storagePath = null;
        openFolder.Click += (_, _) =>
        {
            if (storagePath is null) return;
            try
            {
                System.IO.Directory.CreateDirectory(storagePath);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(storagePath) { UseShellExecute = true });
            }
            catch { warning.Text = "无法打开缓存文件夹，请检查目录权限。"; }
        };
        bool reading = false;
        async Task Update()
        {
            if (reading || _cancel.IsCancellationRequested) return;
            reading = true;
            try
            {
                var result = await MailService.Instance.StorageUsageAsync(accountId, _cancel.Token);
                if (_cancel.IsCancellationRequested) return;
                location.Text = result.Path;
                storagePath = result.Path;
                openFolder.IsEnabled = true;
                usage.Text = $"所有邮箱占用 {FormatBytes(result.Bytes)}\n此账户缓存 {result.Messages:N0} 封 · {FormatBytes(result.AccountBytes)}\n完整邮件 · 当前 Windows 用户加密 · 不自动清理\n单封上限 20 MiB · 自动缓存当前同步范围";
                warning.Text = result.Error;
            }
            catch (OperationCanceledException) { }
            catch { if (!_cancel.IsCancellationRequested) warning.Text = "暂时无法读取存储信息。"; }
            finally { reading = false; }
        }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) => await Update();
        Loaded += async (_, _) => { timer.Start(); await Update(); };
        Closed += (_, _) => timer.Stop();
    }
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):N2} GiB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):N2} MiB" : $"{bytes / 1024d:N1} KiB";
}
