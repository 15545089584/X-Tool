using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using ScreenshotApp.SystemTools;
using ScreenshotApp.NetworkWorkbench;
using ScreenshotApp.Translation;

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
}
