using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using ScreenshotApp.SystemTools;
using ScreenshotApp.NetworkWorkbench;
using ScreenshotApp.Translation;
using ScreenshotApp.DesktopPet;
using ScreenshotApp.Settings;
using ScreenshotApp.InformationVault;

namespace ScreenshotApp;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\JieYing.Desktop.SingleInstance.v1";
    private const string ActivationEventName = @"Local\JieYing.Desktop.Activate.v1";
    private Forms.NotifyIcon? _trayIcon;
    private Action? _trayBalloonAction;
    private Forms.NotifyIcon? _verificationBalloonIcon;
    private System.Windows.Threading.DispatcherTimer? _verificationBalloonTimer;
    private Icon? _trayDrawingIcon;
    private bool _trayHintShown;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private DesktopPetWindow? _desktopPetWindow;
    private PetSessionShelfService? _petShelfService;
    private PetAlarmService? _petAlarmService;
    private (string Title, string Detail)? _pendingScreenshotShelfNotice;
    private readonly SemaphoreSlim _alarmReminderGate = new(1, 1);
    private Forms.ToolStripMenuItem? _desktopPetMenuItem;
    private int _desktopPetCaptureHideDepth;
    private bool _restoreDesktopPetAfterCapture;
    private readonly Dictionary<string, ScreenshotApp.Collaboration.PhoneNotificationItem> _pendingPhoneAlerts = new();
    private System.Windows.Threading.DispatcherTimer? _phoneAlertTimer;

    internal bool IsExitRequested { get; private set; }

    private void PhoneSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock)
        {
            ScreenshotApp.Collaboration.PhoneNotificationHub.Instance.Locked = true;
            Dispatcher.BeginInvoke(() => { _pendingPhoneAlerts.Clear(); DismissPhoneAlertDisplays(); ClearMailAlerts(); });
        }
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) ScreenshotApp.Collaboration.PhoneNotificationHub.Instance.Locked = false;
    }
    private void PhoneNotificationsArrived(ScreenshotApp.Collaboration.PhoneNotificationItem[] items)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var hub = ScreenshotApp.Collaboration.PhoneNotificationHub.Instance;
            if (hub.Locked || !hub.AlertsEnabled) return;
            foreach (var item in items.Take(200)) _pendingPhoneAlerts[item.Key] = item;
            while (_pendingPhoneAlerts.Count > 200) _pendingPhoneAlerts.Remove(_pendingPhoneAlerts.Keys.First());
            _phoneAlertTimer ??= CreatePhoneAlertTimer();
            if (!_phoneAlertTimer.IsEnabled) _phoneAlertTimer.Start();
        });
    }
    private System.Windows.Threading.DispatcherTimer CreatePhoneAlertTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var hub = ScreenshotApp.Collaboration.PhoneNotificationHub.Instance;
            // 合并更新，丢弃等待期间已被移除或修改的通知。
            var items = _pendingPhoneAlerts.Values.Where(i => hub.State.Items.Contains(i)).OrderByDescending(i => i.PostedAt).ToArray();
            _pendingPhoneAlerts.Clear();
            if (hub.Locked || hub.Paused || !hub.AlertsEnabled || items.Length == 0) return;
            var preview = AppPreferences.Load().PhoneNotificationPreviewEnabled;
            // 同批到达的普通消息不能挤掉验证码；识别在正文截断之前执行，发送方与数字始终取自同一条。
            var codes = preview ? items.Select(i => (Item: i, Code: ScreenshotApp.Collaboration.PhoneVerificationCode.TryParse(i)))
                .Where(pair => pair.Code is not null).ToArray() : [];
            var item = codes.Length > 0 ? codes[0].Item : items[0];
            var verificationCode = codes.Length > 0 ? codes[0].Code : null;
            var title = items.Length == 1 ? item.App : $"手机通知 · {items.Length} 条";
            if (title.Length > 63) title = title[..62] + "…";
            var detail = preview ? item.Title + (string.IsNullOrWhiteSpace(item.Text) ? "" : "\n" + item.Text) : "收到新消息，点击查看";
            if (detail.Length > 180) detail = detail[..180] + "…";
            if (preview && verificationCode is null && ScreenshotApp.Collaboration.PhoneVerificationCode.LooksLikeVerification(item))
            {
                title = item.App;
                detail = "收到验证码消息，请在手机查看";
            }
            Action open = () => ScreenshotApp.Collaboration.PhoneNotificationsWindow.Open(MainWindow);
            if (verificationCode is not null)
            {
                DismissVerificationBalloon();
                if (IsDesktopPetVisible)
                    _desktopPetWindow!.ShowPhoneNotification(verificationCode.Sender, verificationCode.Code, open, item.Avatar, verificationCode);
                else ShowVerificationBalloon(verificationCode);
            }
            else if (IsDesktopPetVisible) _desktopPetWindow!.ShowPhoneNotification(title, detail, open, preview ? item.Avatar : null);
            else ShowTrayBalloon(title, detail, open);
        };
        return timer;
    }

    private void CalendarReminderDue(PhoneCalendar.CalendarEntry[] entries)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (IsExitRequested || ScreenshotApp.Collaboration.PhoneNotificationHub.Instance.Locked ||
                PhoneCalendar.PhoneCalendarService.Instance.Read().Paused || entries.Length == 0) return;
            var currentCalendar = PhoneCalendar.PhoneCalendarService.Instance.Read().Snapshot;
            entries = entries.Where(item => currentCalendar?.Enabled == true &&
                currentCalendar.Events.Any(e => e.Id == item.Id && e.Title == item.Title &&
                    e.Reminders.Any(r => item.Reminders.Contains(r)))).ToArray();
            if (entries.Length == 0) return;
            var first = entries[0];
            var title = entries.Length > 1 ? $"日程提醒 · {entries.Length} 项" : "日程提醒";
            var detail = first.Title + "\n" + first.When +
                (string.IsNullOrWhiteSpace(first.Location) ? "" : "\n" + first.Location);
            if (detail.Length > 220) detail = detail[..219] + "…";
            Action open = PhoneCalendar.PhoneCalendarWindow.Open;
            if (IsDesktopPetVisible) _desktopPetWindow!.ShowPhoneNotification(title, detail, open);
            else ShowTrayBalloon(title, detail, open);
        });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (e.Args.Length == 2 && string.Equals(e.Args[0], "--apply-elevated-system-action", StringComparison.Ordinal))
        {
            Shutdown(SystemToolsService.ApplyElevatedSystemActionRequest(e.Args[1]));
            return;
        }

        if (e.Args.Length == 2 && string.Equals(e.Args[0], "--network-etw-helper", StringComparison.Ordinal))
        {
            Shutdown(NetworkEtwTrafficHelper.Run(e.Args[1]));
            return;
        }

        const string desktopPetValidationPrefix = "--desktop-pet-validation=";
        var desktopPetValidationArgument = e.Args.FirstOrDefault(argument =>
            argument.StartsWith(desktopPetValidationPrefix, StringComparison.OrdinalIgnoreCase));
        if (desktopPetValidationArgument is not null)
        {
            var outputDirectory = desktopPetValidationArgument[desktopPetValidationPrefix.Length..].Trim('"');
            var validationWindow = new DesktopPetWindow();
            MainWindow = validationWindow;
            validationWindow.Show();
            _ = RunDesktopPetValidationAndExitAsync(validationWindow, outputDirectory);
            return;
        }

        const string settingsWindowValidationPrefix = "--settings-window-validation=";
        var settingsWindowValidationArgument = e.Args.FirstOrDefault(argument =>
            argument.StartsWith(settingsWindowValidationPrefix, StringComparison.OrdinalIgnoreCase));
        if (settingsWindowValidationArgument is not null)
        {
            var outputDirectory = settingsWindowValidationArgument[settingsWindowValidationPrefix.Length..].Trim('"');
            var validationMainWindow = new MainWindow();
            MainWindow = validationMainWindow;
            validationMainWindow.Show();
            _ = RunSettingsWindowValidationAndExitAsync(validationMainWindow, outputDirectory);
            return;
        }

        _singleInstanceCoordinator = new SingleInstanceCoordinator(
            SingleInstanceMutexName,
            ActivationEventName,
            () => Dispatcher.BeginInvoke(ShowMainWindow));
        if (!_singleInstanceCoordinator.IsPrimaryInstance)
        {
            _ = _singleInstanceCoordinator.NotifyPrimaryInstance(ActivationEventName);
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        CreateTrayIcon(mainWindow);
        ScreenshotApp.Collaboration.PhoneNotificationHub.Instance.AlertsEnabled = AppPreferences.Load().PhoneNotificationAlertsEnabled;
        ScreenshotApp.Collaboration.PhoneNotificationHub.Instance.NotificationsArrived += PhoneNotificationsArrived;
        StartMail();
        PhoneCalendar.PhoneCalendarService.Instance.Start();
        ScreenshotApp.Collaboration.PhoneNotificationHub.Instance.CalendarExchange = PhoneCalendar.PhoneCalendarService.Instance.Exchange;
        ScreenshotApp.Collaboration.PhoneNotificationHub.Instance.PairingRevoked += PhoneCalendar.PhoneCalendarService.Instance.Clear;
        PhoneCalendar.PhoneCalendarService.Instance.Due += CalendarReminderDue;
        Microsoft.Win32.SystemEvents.SessionSwitch += PhoneSessionSwitch;
        _petAlarmService = new PetAlarmService();
        _petAlarmService.ReminderTriggered += PetAlarmService_ReminderTriggered;

#if SCROLL_CAPTURE_TEST
        mainWindow.Loaded += async (_, _) =>
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await mainWindow.RunScrollCaptureForTestAsync();
            ExitApplication();
        };
#endif

        mainWindow.Show();
        if (e.Args.Contains("--mail-center")) Mail.MailWindow.Open();
        if (e.Args.Contains("--phone-notifications")) ScreenshotApp.Collaboration.PhoneNotificationsWindow.Open(mainWindow);
        _ = Task.Run(() => { try { ScreenshotApp.Collaboration.PhoneNotificationHub.Instance.ResumeIfConfigured(); } catch { /* 接收失败由通知页面提示，不创建第二个监听实例。 */ } });
        if (AppPreferences.Load().DesktopPetVisible)
        {
            Dispatcher.BeginInvoke(ShowDesktopPet, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    internal void ShowMainWindow()
    {
        if (MainWindow is not MainWindow mainWindow)
        {
            return;
        }

        mainWindow.Show();
        if (mainWindow.WindowState == WindowState.Minimized)
        {
            var handle = new WindowInteropHelper(mainWindow).Handle;
            if (!NativeWindowAnimation.Restore(handle))
            {
                mainWindow.WindowState = WindowState.Normal;
            }
        }

        mainWindow.Activate();
    }

    internal void HideMainWindowToTray()
    {
        MainWindow?.Hide();
        if (_trayIcon is null || _trayHintShown)
        {
            return;
        }

        _trayHintShown = true;
        _trayIcon.BalloonTipTitle = "X-Tool 仍在后台运行";
        _trayIcon.BalloonTipText = "可使用快捷键截图，或双击托盘图标打开主界面。";
        _trayIcon.ShowBalloonTip(2200);
    }

    internal void ExitApplication()
    {
        IsExitRequested = true;
        if (_desktopPetWindow is not null)
        {
            _desktopPetWindow.Close();
            _desktopPetWindow = null;
        }
        _petShelfService?.Dispose();
        _petShelfService = null;
        if (_petAlarmService is not null)
        {
            _petAlarmService.ReminderTriggered -= PetAlarmService_ReminderTriggered;
            _petAlarmService.Dispose();
            _petAlarmService = null;
        }
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.Close();
        }

        Shutdown();
    }

    /// <summary>托盘气泡通知（协作中心收到手机内容时使用）。</summary>
    internal void ShowTrayBalloon(string title, string text, Action? onClick = null)
    {
        if (_trayIcon is null)
        {
            return;
        }
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = text;
        _trayBalloonAction = onClick;
        _trayIcon.ShowBalloonTip(2600);
    }

    internal void DismissPhoneAlertDisplays()
    {
        _desktopPetWindow?.DismissPhoneNotification();
        DismissVerificationBalloon();
    }

    private void DismissVerificationBalloon()
    {
        _verificationBalloonTimer?.Stop();
        _verificationBalloonTimer = null;
        _verificationBalloonIcon?.Dispose();
        _verificationBalloonIcon = null;
    }

    private void ShowVerificationBalloon(ScreenshotApp.Collaboration.PhoneVerificationCode code)
    {
        DismissVerificationBalloon();
        // 原生托盘通知不支持自定义按钮，单击通知即复制；独立实例防止旧通知误复制新的验证码。
        var icon = new Forms.NotifyIcon
        {
            Icon = _trayDrawingIcon ?? SystemIcons.Application,
            Text = "X-Tool · 验证码",
            BalloonTipTitle = code.Sender,
            BalloonTipText = code.Code + "\n点击复制验证码",
            Visible = true
        };
        _verificationBalloonIcon = icon;
        icon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() =>
        {
            var hub = ScreenshotApp.Collaboration.PhoneNotificationHub.Instance;
            var preferences = AppPreferences.Load();
            if (!ReferenceEquals(_verificationBalloonIcon, icon) || hub.Locked || hub.Paused ||
                !hub.AlertsEnabled || !preferences.PhoneNotificationPreviewEnabled) return;
            try
            {
                ScreenshotApp.ClipboardUi.ClipboardService.SetSensitiveText(code.Code);
                DismissVerificationBalloon();
            }
            catch
            {
                icon.BalloonTipText = code.Code + "\n复制失败，点击重试";
                icon.ShowBalloonTip(5000);
            }
        });
        _verificationBalloonTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _verificationBalloonTimer.Tick += (_, _) => DismissVerificationBalloon();
        _verificationBalloonTimer.Start();
        icon.ShowBalloonTip(10000);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DismissVerificationBalloon();
        Microsoft.Win32.SystemEvents.SessionSwitch -= PhoneSessionSwitch;
        ScreenshotApp.Collaboration.PhoneNotificationHub.Instance.NotificationsArrived -= PhoneNotificationsArrived;
        _phoneAlertTimer?.Stop(); _pendingPhoneAlerts.Clear();
        ScreenshotApp.Collaboration.PhoneNotificationHub.Instance.CalendarSnapshotReceived = null;
        ScreenshotApp.Collaboration.PhoneNotificationHub.Instance.CalendarExchange = null;
        ScreenshotApp.Collaboration.PhoneNotificationHub.Instance.PairingRevoked -= PhoneCalendar.PhoneCalendarService.Instance.Clear;
        PhoneCalendar.PhoneCalendarService.Instance.Due -= CalendarReminderDue;
        StopMail();
        PhoneCalendar.PhoneCalendarService.Instance.Dispose();
        ScreenshotApp.Collaboration.PhoneNotificationHub.Instance.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayDrawingIcon?.Dispose();
        _trayDrawingIcon = null;
        _singleInstanceCoordinator?.Dispose();
        _singleInstanceCoordinator = null;
        _desktopPetWindow = null;
        _desktopPetMenuItem = null;
        _petShelfService?.Dispose();
        _petShelfService = null;
        if (_petAlarmService is not null)
        {
            _petAlarmService.ReminderTriggered -= PetAlarmService_ReminderTriggered;
            _petAlarmService.Dispose();
            _petAlarmService = null;
        }
        TranslationEngineProvider.Dispose();
        base.OnExit(e);
    }

    private void CreateTrayIcon(MainWindow mainWindow)
    {
        var iconResource = GetResourceStream(new Uri("pack://application:,,,/Assets/XTool.ico"));
        if (iconResource?.Stream is not null)
        {
            using var iconStream = iconResource.Stream;
            using var loadedIcon = new Icon(iconStream);
            _trayDrawingIcon = (Icon)loadedIcon.Clone();
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        _desktopPetMenuItem = new Forms.ToolStripMenuItem("显示桌面宠物")
        {
            Checked = true
        };
        _desktopPetMenuItem.Click += (_, _) => Dispatcher.Invoke(ToggleDesktopPet);
        menu.Items.Add(_desktopPetMenuItem);
        menu.Items.Add("邮箱中心", null, (_, _) => Dispatcher.Invoke(Mail.MailWindow.Open));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("截图", null, (_, _) => Dispatcher.Invoke(mainWindow.BeginRegionCapture));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 X-Tool", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "X-Tool · 桌面工具箱",
            Icon = _trayDrawingIcon ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            var action = _trayBalloonAction;
            _trayBalloonAction = null;
            action?.Invoke();
        };
    }

    /// <summary>复用宠物暂存服务；复制失败不影响已经保存的截图和剪贴板。</summary>
    internal async Task StoreScreenshotInPetShelfAsync(string savedPath)
    {
        if (IsExitRequested) return;
        try
        {
            _petShelfService ??= new PetSessionShelfService();
            var result = await _petShelfService.AddFilesAsync([savedPath]);
            if (IsExitRequested) return;
            if (result.AddedCount > 0)
                _pendingScreenshotShelfNotice = ("截图已存入文件暂存区", $"暂存区现有 {_petShelfService.Items.Count} 个文件 · 仅本次运行保留");
            else if (result.DuplicateCount == 0)
                _pendingScreenshotShelfNotice = ("截图未能自动暂存", result.LimitMessage ?? "截图原文件已保存，可稍后手动加入暂存区");
        }
        catch (OperationCanceledException) { return; }
        catch (Exception)
        {
            if (IsExitRequested) return;
            _pendingScreenshotShelfNotice = ("截图未能自动暂存", "截图原文件已保存，可稍后手动加入暂存区");
        }
        FlushScreenshotShelfNotice();
    }

    private void FlushScreenshotShelfNotice()
    {
        // 保存可能先于截图隐藏租约结束；不能把提醒显示在截图中，也不能强行开启隐藏的宠物。
        if (IsExitRequested || _desktopPetCaptureHideDepth > 0 || _pendingScreenshotShelfNotice is not { } notice) return;
        _pendingScreenshotShelfNotice = null;
        if (_desktopPetWindow?.IsVisible == true)
            _ = _desktopPetWindow.ShowScreenshotShelfNoticeAsync(notice.Title, notice.Detail);
        else
            ShowTrayBalloon(notice.Title, notice.Detail);
    }

    private void ToggleDesktopPet()
    {
        SetDesktopPetVisible(!IsDesktopPetVisible);
    }

    private void ShowDesktopPet()
    {
        if (_desktopPetWindow is null)
        {
            try
            {
                _petShelfService ??= new PetSessionShelfService();
                _petAlarmService ??= new PetAlarmService();
                _petAlarmService.ReminderTriggered -= PetAlarmService_ReminderTriggered;
                _petAlarmService.ReminderTriggered += PetAlarmService_ReminderTriggered;
                _desktopPetWindow = new DesktopPetWindow(
                    _petShelfService,
                    _petAlarmService,
                    AppPreferences.Load().DesktopPetScalePercent);
                _desktopPetWindow.PetVisibilityChanged += (_, _) =>
                {
                    // 可见性是运行状态；退出、截图和加载失败的隐藏不能修改用户开关。
                    UpdateDesktopPetMenuItem();
                };
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"桌面宠物创建失败：{exception}");
                UpdateDesktopPetMenuItem();
                return;
            }
        }

        _desktopPetWindow.Show();
        _desktopPetWindow.EnsureTopmostWithoutActivation();
        UpdateDesktopPetMenuItem();
    }

    private async void PetAlarmService_ReminderTriggered(PetAlarmTrigger trigger)
    {
        var service = _petAlarmService;
        if (service is null)
        {
            return;
        }

        await _alarmReminderGate.WaitAsync();
        try
        {
            var screenBounds = _desktopPetWindow?.GetCurrentScreenBounds() ?? new Rect(
                0,
                0,
                SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight);
            var workingArea = _desktopPetWindow?.GetCurrentWorkingAreaForValidation() ?? SystemParameters.WorkArea;
            Task messageTask;
            if (_desktopPetWindow?.IsVisible == true)
            {
                messageTask = _desktopPetWindow.ShowAlarmReminderAsync(trigger);
            }
            else
            {
                var notification = new PetAlarmSystemNotificationWindow(trigger, workingArea);
                messageTask = trigger.IsDue
                    ? notification.ShowUntilClickedAsync()
                    : notification.ShowForAsync(TimeSpan.FromSeconds(5));
            }

            var glow = new PetAlarmEdgeGlowWindow(screenBounds);
            var glowTask = trigger.IsDue
                ? glow.ShowUntilAsync(messageTask)
                : glow.ShowForAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(glowTask, messageTask);
            service.CompleteReminder(trigger);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"桌宠闹钟提醒显示失败：{exception.GetBaseException().Message}");
        }
        finally
        {
            _alarmReminderGate.Release();
        }
    }

    private void UpdateDesktopPetMenuItem()
    {
        if (_desktopPetMenuItem is null)
        {
            return;
        }

        var isVisible = _desktopPetWindow?.IsVisible == true;
        _desktopPetMenuItem.Checked = isVisible;
        _desktopPetMenuItem.Text = isVisible ? "隐藏桌面宠物" : "显示桌面宠物";
    }

    internal void UpdateDesktopPetScale(int scalePercent) =>
        _desktopPetWindow?.SetScalePercent(scalePercent);

    internal bool IsDesktopPetVisible => _desktopPetWindow?.IsVisible == true;

    /// <summary>
    /// 主窗口切换页面或重新激活后恢复桌宠的预期可见性与顶层层级。
    /// 截图临时隐藏期间不会提前显示，用户主动关闭后也不会重新开启。
    /// </summary>
    internal void EnsureDesktopPetVisibleAndTopmost()
    {
        if (IsExitRequested || _desktopPetCaptureHideDepth > 0 || !AppPreferences.Load().DesktopPetVisible)
        {
            return;
        }

        ShowDesktopPet();
    }

    internal bool ShouldUseDesktopPetTransferBubbles =>
        IsDesktopPetVisible && AppPreferences.Load().DesktopPetTakesOverTransferNotifications;

    /// <summary>
    /// 截图会话期间临时隐藏桌面宠物。释放后只恢复截图前本来可见的宠物，
    /// 不写入用户的长期显示偏好；嵌套调用也只会在最外层结束时恢复。
    /// </summary>
    internal IDisposable SuspendDesktopPetForCapture()
    {
        Dispatcher.VerifyAccess();
        if (_desktopPetCaptureHideDepth++ == 0)
        {
            _restoreDesktopPetAfterCapture = IsDesktopPetVisible;
            if (_restoreDesktopPetAfterCapture)
            {
                _desktopPetWindow?.Hide();
            }
        }

        return new DesktopPetCaptureSuspension(this);
    }

    private void ResumeDesktopPetAfterCapture()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ResumeDesktopPetAfterCapture);
            return;
        }

        if (_desktopPetCaptureHideDepth <= 0 || --_desktopPetCaptureHideDepth > 0)
        {
            return;
        }

        var shouldRestore = _restoreDesktopPetAfterCapture;
        _restoreDesktopPetAfterCapture = false;
        if (!shouldRestore || IsExitRequested)
        {
            FlushScreenshotShelfNotice();
            return;
        }

        ShowDesktopPet();
        FlushScreenshotShelfNotice();
    }

    private sealed class DesktopPetCaptureSuspension : IDisposable
    {
        private App? _owner;

        internal DesktopPetCaptureSuspension(App owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ResumeDesktopPetAfterCapture();
        }
    }

    internal bool SetDesktopPetVisible(bool visible)
    {
        if (visible)
        {
            ShowDesktopPet();
        }
        else
        {
            _desktopPetWindow?.Hide();
        }

        PersistDesktopPetVisibility();
        return IsDesktopPetVisible;
    }

    private void PersistDesktopPetVisibility()
    {
        var preferences = AppPreferences.Load();
        preferences.DesktopPetVisible = IsDesktopPetVisible;
        preferences.Save();
        UpdateDesktopPetMenuItem();
    }

    private async Task RunDesktopPetValidationAndExitAsync(
        DesktopPetWindow validationWindow,
        string outputDirectory)
    {
        var exitCode = 0;
        try
        {
            await DesktopPetValidationRunner.RunAsync(validationWindow, outputDirectory);
        }
        catch (Exception exception)
        {
            exitCode = 10;
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "desktop-pet-validation-error.txt"),
                exception.ToString());
        }
        finally
        {
            validationWindow.Close();
            Shutdown(exitCode);
        }
    }

    private async Task RunSettingsWindowValidationAndExitAsync(
        MainWindow validationMainWindow,
        string outputDirectory)
    {
        var exitCode = 0;
        SettingsWindow? settingsWindow = null;
        InformationVaultEntryDialog? informationVaultEntryDialog = null;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(150);
            const string mainWindowFileName = "main-home.png";
            validationMainWindow.CaptureForValidation(Path.Combine(outputDirectory, mainWindowFileName));
            validationMainWindow.NavigateToEfficiencyToolsForValidation();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(650);
            const string efficiencyToolsFileName = "main-efficiency-tools.png";
            validationMainWindow.CaptureForValidation(Path.Combine(outputDirectory, efficiencyToolsFileName));
            validationMainWindow.NavigateToSystemCenterForValidation();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(300);
            const string systemCenterFileName = "main-system-center.png";
            validationMainWindow.CaptureForValidation(Path.Combine(outputDirectory, systemCenterFileName));
            var informationVaultValidation = InformationVaultValidation.Run(outputDirectory);
            validationMainWindow.NavigateToInformationVaultForValidation();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(450);
            const string informationVaultFileName = "information-vault-locked.png";
            validationMainWindow.CaptureForValidation(Path.Combine(outputDirectory, informationVaultFileName));
            validationMainWindow.PrepareInformationVaultUnlockedValidationState();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(120);
            const string informationVaultUnlockedFileName = "information-vault-unlocked.png";
            validationMainWindow.CaptureForValidation(Path.Combine(outputDirectory, informationVaultUnlockedFileName));
            validationMainWindow.PrepareInformationVaultDetailValidationState();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            const string informationVaultDetailFileName = "information-vault-detail.png";
            validationMainWindow.CaptureForValidation(Path.Combine(outputDirectory, informationVaultDetailFileName));
            validationMainWindow.PrepareInformationVaultSingleEntryValidationState();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            const string informationVaultSingleEntryFileName = "information-vault-single-entry.png";
            validationMainWindow.CaptureForValidation(Path.Combine(outputDirectory, informationVaultSingleEntryFileName));
            validationMainWindow.PrepareInformationVaultSearchInputValidationState();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            const string informationVaultSearchInputFileName = "information-vault-search-input.png";
            validationMainWindow.CaptureForValidation(Path.Combine(outputDirectory, informationVaultSearchInputFileName));
            informationVaultEntryDialog = new InformationVaultEntryDialog(new InformationVaultEntry
            {
                Type = InformationVaultEntryType.Steam,
                Title = "测试用 Steam 账号",
                Account = "validation@example.invalid",
                Secret = "not-a-real-password",
                Notes = "用于验证表单控件、按钮边界与页面比例的模拟记录。"
            })
            {
                Owner = validationMainWindow
            };
            informationVaultEntryDialog.Show();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(120);
            const string informationVaultEntryDialogFileName = "information-vault-entry-dialog.png";
            informationVaultEntryDialog.CaptureForValidation(Path.Combine(outputDirectory, informationVaultEntryDialogFileName));
            const string informationVaultEntryDialogDropDownFileName = "information-vault-entry-dialog-dropdown.png";
            informationVaultEntryDialog.OpenTypeDropDownForValidation();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(120);
            informationVaultEntryDialog.CaptureScreenForValidation(Path.Combine(outputDirectory, informationVaultEntryDialogDropDownFileName));
            informationVaultEntryDialog.Close();
            informationVaultEntryDialog = null;
            informationVaultEntryDialog = new InformationVaultEntryDialog(new InformationVaultEntry
            {
                Type = InformationVaultEntryType.DeepSeekApiKey,
                Title = "DeepSeek API 密钥",
                Secret = "sk-validation-not-real",
                Notes = "仅显示 API Key 与可选备注。"
            })
            {
                Owner = validationMainWindow
            };
            informationVaultEntryDialog.Show();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(120);
            const string informationVaultDeepSeekDialogFileName = "information-vault-deepseek-dialog.png";
            informationVaultEntryDialog.CaptureForValidation(Path.Combine(outputDirectory, informationVaultDeepSeekDialogFileName));
            informationVaultEntryDialog.Close();
            informationVaultEntryDialog = null;
            informationVaultEntryDialog = new InformationVaultEntryDialog(new InformationVaultEntry
            {
                Type = InformationVaultEntryType.GitHubCredential,
                Title = "GitHub 凭据",
                Account = "validation@example.invalid",
                Secret = "not-a-real-password",
                GitHubPushKey = "github-validation-push-key",
                GitHubTwoFactorEnabled = true,
                RecoveryCodes =
                [
                    new InformationVaultRecoveryCode { Value = "VALIDATION-ONE" },
                    new InformationVaultRecoveryCode { Value = "VALIDATION-TWO" }
                ],
                Notes = "账号、密码、推送密钥、二次验证与恢复码归入同一条 GitHub 凭据。"
            })
            {
                Owner = validationMainWindow
            };
            informationVaultEntryDialog.Show();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(120);
            const string informationVaultGitHubDialogFileName = "information-vault-github-dialog.png";
            informationVaultEntryDialog.CaptureForValidation(Path.Combine(outputDirectory, informationVaultGitHubDialogFileName));
            informationVaultEntryDialog.Close();
            informationVaultEntryDialog = null;
            informationVaultEntryDialog = new InformationVaultEntryDialog(new InformationVaultEntry
            {
                Type = InformationVaultEntryType.VirtualMachine,
                Title = "Linux 测试虚拟机",
                Account = "root",
                Secret = "not-a-real-password",
                Host = "192.0.2.10",
                Port = "22",
                OperatingSystem = "Linux",
                OperatingSystemDistribution = "CentOS",
                OperatingSystemVersion = "7.6",
                Notes = "用于验证操作系统、发行版和版本号的联动字段。"
            })
            {
                Owner = validationMainWindow
            };
            informationVaultEntryDialog.Show();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(120);
            const string informationVaultVirtualMachineDialogFileName = "information-vault-virtual-machine-dialog.png";
            informationVaultEntryDialog.CaptureForValidation(Path.Combine(outputDirectory, informationVaultVirtualMachineDialogFileName));
            informationVaultEntryDialog.Close();
            informationVaultEntryDialog = null;
            settingsWindow = validationMainWindow.CreateSettingsWindowForValidation();
            settingsWindow.Show();
            await Task.Delay(350);

            var captures = new List<object>();
            foreach (var category in SettingsWindow.ValidationCategories)
            {
                settingsWindow.SelectCategoryForValidation(category);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                await Task.Delay(100);
                var fileName = $"settings-{category.ToLowerInvariant()}.png";
                settingsWindow.CaptureForValidation(Path.Combine(outputDirectory, fileName));
                captures.Add(new
                {
                    category,
                    fileName,
                    width = settingsWindow.ActualWidth,
                    height = settingsWindow.ActualHeight
                });
            }

            var result = new
            {
                succeeded = true,
                mainWindowFileName,
                efficiencyToolsFileName,
                systemCenterFileName,
                informationVaultFileName,
                informationVaultUnlockedFileName,
                informationVaultDetailFileName,
                informationVaultSingleEntryFileName,
                informationVaultSearchInputFileName,
                informationVaultEntryDialogFileName,
                informationVaultEntryDialogDropDownFileName,
                informationVaultDeepSeekDialogFileName,
                informationVaultGitHubDialogFileName,
                informationVaultVirtualMachineDialogFileName,
                informationVaultValidation,
                windowWidth = settingsWindow.ActualWidth,
                windowHeight = settingsWindow.ActualHeight,
                captures
            };
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "settings-window-validation.json"),
                System.Text.Json.JsonSerializer.Serialize(
                    result,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            exitCode = 11;
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "settings-window-validation-error.txt"),
                exception.ToString());
        }
        finally
        {
            informationVaultEntryDialog?.Close();
            settingsWindow?.Close();
            IsExitRequested = true;
            validationMainWindow.Close();
            Shutdown(exitCode);
            Environment.Exit(exitCode);
        }
    }
}
