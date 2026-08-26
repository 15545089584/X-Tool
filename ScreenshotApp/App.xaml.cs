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

namespace ScreenshotApp;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\JieYing.Desktop.SingleInstance.v1";
    private const string ActivationEventName = @"Local\JieYing.Desktop.Activate.v1";
    private Forms.NotifyIcon? _trayIcon;
    private Action? _trayBalloonAction;
    private Icon? _trayDrawingIcon;
    private bool _trayHintShown;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private DesktopPetWindow? _desktopPetWindow;
    private Forms.ToolStripMenuItem? _desktopPetMenuItem;

    internal bool IsExitRequested { get; private set; }

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

#if SCROLL_CAPTURE_TEST
        mainWindow.Loaded += async (_, _) =>
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await mainWindow.RunScrollCaptureForTestAsync();
            ExitApplication();
        };
#endif

        mainWindow.Show();
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

    protected override void OnExit(ExitEventArgs e)
    {
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
                _desktopPetWindow = new DesktopPetWindow(AppPreferences.Load().DesktopPetScalePercent);
                _desktopPetWindow.PetVisibilityChanged += (_, _) =>
                {
                    if (!IsExitRequested)
                    {
                        PersistDesktopPetVisibility();
                    }
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
        UpdateDesktopPetMenuItem();
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

    internal bool ShouldUseDesktopPetTransferBubbles =>
        IsDesktopPetVisible && AppPreferences.Load().DesktopPetTakesOverTransferNotifications;

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
        try
        {
            Directory.CreateDirectory(outputDirectory);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(150);
            const string mainWindowFileName = "main-home.png";
            validationMainWindow.CaptureForValidation(Path.Combine(outputDirectory, mainWindowFileName));
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
            settingsWindow?.Close();
            IsExitRequested = true;
            validationMainWindow.Close();
            Shutdown(exitCode);
            Environment.Exit(exitCode);
        }
    }
}
