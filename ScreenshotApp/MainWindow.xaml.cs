using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using System.ComponentModel;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using ScreenshotApp.Capture;
using ScreenshotApp.ClipboardUi;
using ScreenshotApp.Converters;
using ScreenshotApp.History;
using ScreenshotApp.Recording;
using ScreenshotApp.Settings;
using ScreenshotApp.Shortcuts;
using ScreenshotApp.StorageAnalysis;
using ScreenshotApp.Translation;
using ScreenshotApp.VoiceInput;
using ScreenshotApp.NetworkWorkbench;
using ScreenshotApp.Motion;

namespace ScreenshotApp;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _toastTimer;
    private readonly ICaptureBackend _captureBackend = new CaptureCoordinator(
        new DxgiDesktopCaptureBackend(),
        new GdiScreenCaptureBackend());
    private readonly ICaptureBackend _scrollCaptureBackend;
    private readonly ScrollCaptureService _scrollCaptureService;
    private readonly ScreenRecordingService _screenRecordingService;
    private readonly AppPreferences _preferences = AppPreferences.Load();
    private readonly ScreenshotHistoryStore _historyStore;
    private readonly ObservableCollection<ScreenshotHistoryItem> _historyItems = new();
    private readonly ObservableCollection<ScreenshotHistoryItem> _textHistoryItems = new();
    private readonly ObservableCollection<ImageConversionQueueItem> _imageConversionFiles = new();
    private string? _imageOutputDirectory;
    private IReadOnlyList<ScreenshotHistoryItem> _allHistoryItems = Array.Empty<ScreenshotHistoryItem>();
    private HistoryEntryKind? _historyFilter;
    private HwndSource? _windowSource;
    private bool _hotKeyRegistered;
    private ScreenshotHotKeySuppressor? _screenshotHotKeySuppressor;
    private long _screenshotHookTriggeredAt;
    private bool _fullScreenHotKeyRegistered;
    private RightControlHotKeyMonitor? _fullScreenHotKeyMonitor;
    private bool _clipboardHotKeyRegistered;
    private bool _voiceInputHotKeyRegistered;
    private RightAltHotKeyMonitor? _voiceInputHotKeyMonitor;
    private bool _clipboardListenerRegistered;
    private bool _captureInProgress;
    private bool _historyRefreshInProgress;
    private bool _historyRefreshPending;
    private bool _suppressClipboardCapture;
    private string? _lastExternalClipboardSignature;
    private ClipboardPickerWindow? _clipboardPicker;
    private IntPtr _clipboardPasteTarget;
    private readonly VoiceInputService _voiceInputService = new();
    private VoiceInputOverlayWindow? _voiceInputOverlay;
    private IntPtr _voiceInputPasteTarget;
    private CancellationTokenSource? _voiceInputCancellation;
    private string _voiceInputCommittedText = string.Empty;
    private CancellationTokenSource? _voiceTranslationCancellation;
    private bool _voiceTranslationPreviewActive;
    private bool _voiceInputAwaitingConfirmation;
    private string _voiceInputTranslatedText = string.Empty;
    private string _currentPage = "Home";
    private FrameworkElement? _visiblePageView;
    private int _pageTransitionGeneration;
    private int _toastAnimationGeneration;
    private GlobalShortcut _screenshotShortcut;
    private GlobalShortcut _fullScreenShortcut;
    private GlobalShortcut _clipboardShortcut;
    private GlobalShortcut _voiceInputShortcut;

    public MainWindow()
    {
        InitializeComponent();
        _visiblePageView = HomeView;
        _historyStore = new ScreenshotHistoryStore(_preferences);
        QrCodeConverterViewHost.HistoryStore = _historyStore;
        ClipboardService.TextRecordRequested += async content =>
        {
            try
            {
                var path = await _historyStore.SaveClipboardTextAsync(content);
                InsertClipboardItem(path, isText: true);
                LogClipboardCapture("主动复制文本已写入历史");
            }
            catch (Exception exception)
            {
                LogClipboardCapture($"主动复制文本入史失败：{exception.GetBaseException().Message}");
            }
        };
        ClipboardService.ImageRecordRequested += async image =>
        {
            try
            {
                var path = await _historyStore.SaveClipboardImageAsync(image);
                InsertClipboardItem(path, isText: false);
                LogClipboardCapture("主动复制图片已写入历史");
            }
            catch (Exception exception)
            {
                LogClipboardCapture($"主动复制图片入史失败：{exception.GetBaseException().Message}");
            }
        };
        _ = GlobalShortcut.TryParse(_preferences.ScreenshotShortcut, GlobalShortcut.ScreenshotDefault, out _screenshotShortcut);
        _ = GlobalShortcut.TryParse(_preferences.FullScreenShortcut, GlobalShortcut.FullScreenDefault, out _fullScreenShortcut);
        _ = GlobalShortcut.TryParse(_preferences.ClipboardShortcut, GlobalShortcut.ClipboardDefault, out _clipboardShortcut);
        _ = GlobalShortcut.TryParse(_preferences.VoiceInputShortcut, GlobalShortcut.VoiceDefault, out _voiceInputShortcut);
        StickerTopmostCheckBox.IsChecked = _preferences.StickerTopmost;
        DesktopPetScaleSlider.Value = Math.Clamp(_preferences.DesktopPetScalePercent, 60, 160);
        UpdateDesktopPetScaleText((int)Math.Round(DesktopPetScaleSlider.Value));
        StartWithWindowsCheckBox.IsChecked = AutoStartService.IsEnabled();
        _preferences.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        VoiceInputEnabledCheckBox.IsChecked = _preferences.VoiceInputEnabled;
        VoiceInputPasteAutomaticallyCheckBox.IsChecked = _preferences.VoiceInputPasteAutomatically;
        VoiceInputModelStatusText.Text = _voiceInputService.IsModelAvailable
            ? "标准中文离线模型已就绪"
            : "本地模型缺失，请修复或重新安装 X-Tool";
        _voiceInputService.AutoStopRequested += VoiceInputService_AutoStopRequested;
        _voiceInputService.SoundLevelChanged += VoiceInputService_SoundLevelChanged;
        _voiceInputService.RecordingFaulted += VoiceInputService_RecordingFaulted;
        _voiceInputService.PartialResultAvailable += VoiceInputService_PartialResultAvailable;
        SystemToolsView.FileWorkbenchRequested += SystemToolsView_FileWorkbenchRequested;
        UpdateStorageLocationText();
        // 长截图需要连续拿到“此刻”的画面。每次重新创建桌面复制会话时，
        // 部分显卡驱动可能先返回上一帧，因此滚动采集优先使用同步的 GDI 帧，
        // 若遇到不可捕获或空帧内容，再回退到 DXGI。
        _scrollCaptureBackend = new CaptureCoordinator(
            new GdiScreenCaptureBackend(),
            new DxgiDesktopCaptureBackend());
        _scrollCaptureService = new ScrollCaptureService(_scrollCaptureBackend);
        _screenRecordingService = new ScreenRecordingService(_scrollCaptureBackend, _preferences);
        HistoryItemsControl.ItemsSource = _historyItems;
        TextHistoryItemsControl.ItemsSource = _textHistoryItems;
        ImageFileList.ItemsSource = _imageConversionFiles;
        ImageFileList.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        ConverterOutputFolderText.Text = "请选择本次任务的输出文件夹";
        UpdateImageConversionControls();

        _toastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.2)
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideToast();
        };
        UpdateSettingsShortcutSummary();

        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        StateChanged += (_, _) => UpdateMaximizeButton();
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;

#if DEBUG
        Loaded += CapturePreviewWhenRequested;
#endif
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!NativeWindowAnimation.Minimize(handle))
        {
            WindowState = WindowState.Minimized;
        }
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeButton();
    }

    private void UpdateMaximizeButton()
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\u2750" : "\u25A1";
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateMaximizeButton();
        EnlargeImageConversionHintText(this);
        NormalizeImageConverterLabels(this);
        await RefreshHistoryAsync();
        await RefreshNetworkEtwAuthorizationStateAsync();
        if (_preferences.NetworkEtwAutoStart && await Task.Run(NetworkEtwAutoStartService.IsRegistered))
        {
            // 恢复原有后台连接时序，避免主窗口加载被 ETW 管道等待阻塞。
            _ = NetworkWorkbenchView.StartPersistentTrafficAsync(silent: true);
        }
    }

    private async Task RefreshNetworkEtwAuthorizationStateAsync()
    {
        var registered = await Task.Run(NetworkEtwAutoStartService.IsRegistered);
        _preferences.NetworkEtwAutoStart = registered;
        if (!registered) _preferences.Save();
        NetworkEtwAuthorizationButton.Content = registered ? "取消授权" : "授权并自动获取";
        NetworkEtwAuthorizationStatusText.Text = registered
            ? "已授权：启动 X-Tool 时自动运行独立 ETW 辅助进程"
            : "尚未授权网络流量自动获取";
    }

    private async void NetworkEtwAuthorizationButton_Click(object sender, RoutedEventArgs e)
    {
        NetworkEtwAuthorizationButton.IsEnabled = false;
        try
        {
            var registered = await Task.Run(NetworkEtwAutoStartService.IsRegistered);
            if (registered)
            {
                NetworkWorkbenchView.StopTrafficMonitoring();
                var result = await NetworkEtwAutoStartService.UnregisterAsync();
                if (result.Success)
                {
                    _preferences.NetworkEtwAutoStart = false;
                    _preferences.Save();
                    NetworkEtwAuthorizationButton.Content = "授权并自动获取";
                    NetworkEtwAuthorizationStatusText.Text = "尚未授权网络流量自动获取";
                }
                ShowToast(result.Message);
            }
            else
            {
                var result = await NetworkEtwAutoStartService.RegisterAsync();
                if (result.Success)
                {
                    _preferences.NetworkEtwAutoStart = true;
                    _preferences.Save();
                    NetworkEtwAuthorizationButton.Content = "取消授权";
                    NetworkEtwAuthorizationStatusText.Text = "已授权：启动 X-Tool 时自动运行独立 ETW 辅助进程";
                    var startResult = await NetworkWorkbenchView.StartPersistentTrafficAsync();
                    if (!startResult.Success)
                    {
                        NetworkEtwAuthorizationStatusText.Text = $"已授权，但暂未连接：{startResult.Message}";
                    }
                }
                ShowToast(result.Message);
            }
        }
        catch (Exception exception)
        {
            // 授权结果或管道启动异常不能导致主窗口退出。
            ShowToast($"网络流量授权操作失败：{exception.GetBaseException().Message}");
            await RefreshNetworkEtwAuthorizationStateAsync();
        }
        finally
        {
            NetworkEtwAuthorizationButton.IsEnabled = true;
        }
    }

    private static void EnlargeImageConversionHintText(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBlock textBlock && textBlock.Text is "体积小" or "质量高" or "按比例缩放，保留原图比例")
            {
                textBlock.FontSize = 12;
            }

            EnlargeImageConversionHintText(child);
        }
    }

    private static void NormalizeImageConverterLabels(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBlock textBlock && textBlock.Text.StartsWith("预览对比", StringComparison.Ordinal))
            {
                textBlock.Text = "预览对比";
            }

            if (child is TextBlock fileListText && fileListText.Text == "文件列表" && VisualTreeHelper.GetParent(fileListText) is Grid headerGrid && headerGrid.Tag is null)
            {
                headerGrid.Tag = "已调整文件列表表头";
                var originalHeader = headerGrid.Children.OfType<StackPanel>().FirstOrDefault();
                if (originalHeader is not null)
                {
                    originalHeader.Visibility = Visibility.Collapsed;
                }

                var columns = new Grid { Margin = new Thickness(60, 0, 20, 0) };
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
                columns.Children.Add(new TextBlock { Text = "文件名", VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(120, 144, 166)) });
                var format = new TextBlock { Text = "格式", Margin = new Thickness(20, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(120, 144, 166)) };
                Grid.SetColumn(format, 1);
                columns.Children.Add(format);
                var size = new TextBlock { Text = "大小", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(120, 144, 166)) };
                Grid.SetColumn(size, 2);
                columns.Children.Add(size);
                headerGrid.Children.Add(columns);
            }

            NormalizeImageConverterLabels(child);
        }
    }

    private void NavButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton radioButton)
        {
            return;
        }

        NavigateToPage(radioButton.Tag?.ToString() ?? "Home");
    }

    private void NavigateToPage(string page)
    {
        var targetView = GetPageView(page);
        if (targetView is null)
        {
            return;
        }

        var outgoingView = _visiblePageView;
        if (!ReferenceEquals(outgoingView, targetView))
        {
            var transitionGeneration = ++_pageTransitionGeneration;
            var direction = GetPageOrder(page) >= GetPageOrder(_currentPage) ? 1 : -1;
            foreach (var view in GetPageViews())
            {
                if (!ReferenceEquals(view, outgoingView) && !ReferenceEquals(view, targetView))
                {
                    AppMotion.ResetPage(view);
                    view.Visibility = Visibility.Collapsed;
                }
            }

            _visiblePageView = targetView;
            _currentPage = page;
            AppMotion.AnimatePageTransition(outgoingView, targetView, direction, () =>
            {
                if (transitionGeneration != _pageTransitionGeneration)
                {
                    return;
                }

                if (outgoingView is not null && !ReferenceEquals(outgoingView, targetView))
                {
                    outgoingView.Visibility = Visibility.Collapsed;
                    outgoingView.IsHitTestVisible = true;
                    AppMotion.ResetPage(outgoingView);
                }

                AppMotion.ResetPage(targetView);
            });
        }
        else
        {
            targetView.Visibility = Visibility.Visible;
            _currentPage = page;
        }

        if (page == "ImageConverter")
        {
            Dispatcher.BeginInvoke(new Action(() => NormalizeImageConverterLabels(ImageConverterView)), DispatcherPriority.Loaded);
        }

        if (page is "History" or "ScreenWorkbench")
        {
            _ = RefreshHistoryAsync();
        }
    }

    private FrameworkElement? GetPageView(string page) => page switch
    {
        "Home" => HomeView,
        "ScreenWorkbench" => ScreenWorkbenchView,
        "ConverterWorkbench" => ConverterWorkbenchView,
        "ImageConverter" => ImageConverterView,
        "AudioConverter" => AudioConverterView,
        "VideoConverter" => VideoConverterView,
        "PdfConverter" => PdfConverterView,
        "EncodingConverter" => EncodingConverterView,
        "QrCodeConverter" => QrCodeConverterView,
        "FileWorkbench" => FileWorkbenchView,
        "NetworkWorkbench" => NetworkWorkbenchView,
        "ResourceManagement" => ResourceManagementView,
        "SystemTools" => SystemToolsView,
        "DeveloperTools" => DeveloperToolsView,
        "History" => HistoryView,
        "Settings" => SettingsView,
        _ => null
    };

    private FrameworkElement[] GetPageViews() =>
    [
        HomeView,
        ScreenWorkbenchView,
        ConverterWorkbenchView,
        ImageConverterView,
        AudioConverterView,
        VideoConverterView,
        PdfConverterView,
        EncodingConverterView,
        QrCodeConverterView,
        FileWorkbenchView,
        NetworkWorkbenchView,
        ResourceManagementView,
        SystemToolsView,
        DeveloperToolsView,
        HistoryView,
        SettingsView
    ];

    private static int GetPageOrder(string page) => page switch
    {
        "Home" => 0,
        "ScreenWorkbench" => 10,
        "History" => 11,
        "ConverterWorkbench" => 20,
        "ImageConverter" => 21,
        "AudioConverter" => 22,
        "VideoConverter" => 23,
        "PdfConverter" => 24,
        "EncodingConverter" => 25,
        "QrCodeConverter" => 26,
        "FileWorkbench" => 30,
        "NetworkWorkbench" => 40,
        "ResourceManagement" => 50,
        "SystemTools" => 60,
        "DeveloperTools" => 70,
        "Settings" => 80,
        _ => 0
    };

    private void InteractiveCard_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            AppMotion.AnimateCard(element, 1.024, -5, 220);
        }
    }

    private void InteractiveCard_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            AppMotion.AnimateCard(element, 1, 0, 260);
        }
    }

    /// <summary>存储页仅负责发现空间来源；用户点击后才切到文件工作台继续搜索或批处理。</summary>
    private async void SystemToolsView_FileWorkbenchRequested(object? sender, FileWorkbenchNavigationRequestedEventArgs e)
    {
        FileWorkbenchNav.IsChecked = true;
        NavigateToPage("FileWorkbench");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await FileWorkbenchView.OpenFolderFromStorageAsync(e.FolderPath, e.SearchKeyword, e.KnownFilePath);
    }

    private async void CaptureAction_Click(object sender, RoutedEventArgs e)
    {
        var action = (sender as FrameworkElement)?.Tag?.ToString() ?? "截图";
        if (action is "截图" or "普通截图" or "区域截图")
        {
            await StartRegionCaptureAsync();
            return;
        }

        if (action == "长截图")
        {
            await StartScrollCaptureAsync();
            return;
        }

        ShowToast($"{action}将在后续阶段接入");
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _ = NativeWindowAnimation.EnableSystemTransitions(handle);
        _ = WindowBackdrop.TryApply(handle);
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);

        _hotKeyRegistered = RegisterStandardShortcut(handle, NativeMethods.HotKeyId, _screenshotShortcut);
        InstallScreenshotHotKeySuppressor();
        RegisterFullScreenHotKey(handle);
        _clipboardHotKeyRegistered = RegisterStandardShortcut(handle, NativeMethods.ClipboardHotKeyId, _clipboardShortcut);
        RegisterVoiceInputHotKey(handle);
        _clipboardListenerRegistered = NativeMethods.AddClipboardFormatListener(handle);

        if (!_hotKeyRegistered && _screenshotHotKeySuppressor?.IsInstalled != true)
        {
            Dispatcher.BeginInvoke(() => ShowToast($"{_screenshotShortcut.DisplayText} 已被其他程序占用"), DispatcherPriority.Loaded);
        }
        if (!_clipboardHotKeyRegistered)
        {
            Dispatcher.BeginInvoke(() => ShowToast($"{_clipboardShortcut.DisplayText} 已被其他程序占用"), DispatcherPriority.Loaded);
        }
        if (!_fullScreenHotKeyRegistered)
        {
            Dispatcher.BeginInvoke(() => ShowToast($"{_fullScreenShortcut.DisplayText} 已被其他程序占用"), DispatcherPriority.Loaded);
        }
        if (_preferences.VoiceInputEnabled && !_voiceInputHotKeyRegistered)
        {
            Dispatcher.BeginInvoke(() => ShowToast($"{_voiceInputShortcut.DisplayText} 已被其他程序占用"), DispatcherPriority.Loaded);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_windowSource is null)
        {
            return;
        }

        if (_hotKeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_windowSource.Handle, NativeMethods.HotKeyId);
        }
        _screenshotHotKeySuppressor?.Dispose();
        _screenshotHotKeySuppressor = null;
        UnregisterFullScreenHotKey();
        if (_clipboardHotKeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_windowSource.Handle, NativeMethods.ClipboardHotKeyId);
        }
        if (_voiceInputHotKeyRegistered && !_voiceInputShortcut.IsRightAlt)
        {
            NativeMethods.UnregisterHotKey(_windowSource.Handle, NativeMethods.VoiceHotKeyId);
        }
        _voiceInputHotKeyMonitor?.Dispose();
        _voiceInputHotKeyMonitor = null;
        _voiceInputHotKeyRegistered = false;
        if (_clipboardListenerRegistered)
        {
            NativeMethods.RemoveClipboardFormatListener(_windowSource.Handle);
        }

        _windowSource.RemoveHook(WindowMessageHook);
        _windowSource = null;
        _voiceInputService.Dispose();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (Application.Current is App { IsExitRequested: false })
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    private void HideToTray()
    {
        if (Application.Current is App app)
        {
            app.HideMainWindowToTray();
        }
        else
        {
            Hide();
        }
    }

    internal void BeginRegionCapture()
    {
        _ = StartRegionCaptureAsync();
    }

#if SCROLL_CAPTURE_TEST
    /// <summary>
    /// 专用回归构建入口：等待整次长截图真正结束，便于自动关闭测试进程。
    /// </summary>
    internal Task RunScrollCaptureForTestAsync()
    {
        return StartScrollCaptureAsync();
    }
#endif

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WindowSizing.WmNcHitTest &&
            WindowSizing.TryGetResizeHit(hwnd, lParam, out var resizeHit))
        {
            handled = true;
            return resizeHit;
        }

        if (message == WindowSizing.WmGetMinMaxInfo)
        {
            handled = WindowSizing.TryConstrainToWorkArea(hwnd, lParam);
            return IntPtr.Zero;
        }

        if (message == NativeMethods.WmClipboardUpdate)
        {
            _ = CaptureExternalClipboardAsync();
            return IntPtr.Zero;
        }

        if (message == NativeMethods.WmHotKey && wParam.ToInt32() == NativeMethods.HotKeyId)
        {
            if (ConsumeScreenshotHookTrigger())
            {
                handled = true;
                return IntPtr.Zero;
            }

            handled = true;
            _ = StartRegionCaptureAsync();
        }
        else if (message == NativeMethods.WmHotKey && wParam.ToInt32() == NativeMethods.FullScreenHotKeyId)
        {
            handled = true;
            _ = StartFullScreenCaptureAsync();
        }
        else if (message == NativeMethods.WmHotKey && wParam.ToInt32() == NativeMethods.ClipboardHotKeyId)
        {
            handled = true;
            _ = ShowClipboardPickerAsync();
        }
        else if (message == NativeMethods.WmHotKey && wParam.ToInt32() == NativeMethods.VoiceHotKeyId)
        {
            handled = true;
            _ = ToggleVoiceInputAsync();
        }
        return IntPtr.Zero;
    }

    private async Task StartRegionCaptureAsync()
    {
        if (_captureInProgress)
        {
            return;
        }

        _captureInProgress = true;
        var wasVisible = IsVisible;
        var previousState = WindowState;
        var restoreWindowAfterCapture = true;

        try
        {
            var sourceWindow = NativeMethods.GetForegroundWindow();
            NativeMethods.DismissForegroundTransientUi(sourceWindow);
            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            NativeMethods.DwmFlush();
            await Task.Delay(120);
            // 某些应用会在收到完整快捷键后的下一轮消息循环才创建菜单，
            // 再清理一次可覆盖这类晚到的瞬态 UI。
            NativeMethods.DismissForegroundTransientUi(sourceWindow);
            await Task.Delay(40);

            var frame = await _captureBackend.CaptureCurrentMonitorAsync();
            var overlay = new SelectionOverlayWindow(frame);
            overlay.HistoryTextCreated += Overlay_HistoryTextCreated;
            var confirmed = overlay.ShowDialog() == true;

            if (confirmed &&
                overlay.IsScreenRecordingRequested &&
                overlay.SelectedScreenBounds is Int32Rect recordingRegion)
            {
                var synthesisStartedInBackground = await CaptureScreenRecordingAsync(
                    recordingRegion,
                    overlay.RecordingMode,
                    overlay.RecordSystemAudio,
                    overlay.RecordMicrophone);
                if (synthesisStartedInBackground)
                {
                    // GIF 已经完成帧采集，主窗口继续留在托盘，避免后台合成期间被关闭。
                    restoreWindowAfterCapture = false;
                }
                return;
            }

            if (confirmed &&
                overlay.IsScrollCaptureRequested &&
                overlay.SelectedScreenBounds is Int32Rect scrollRegion)
            {
                await CaptureScrollRegionAsync(scrollRegion);
                // 长截图已完成，保持主界面隐藏，避免打断用户当前工作。
                restoreWindowAfterCapture = false;
                return;
            }

            if (confirmed && overlay.SelectedBitmap is not null)
            {
                await SetClipboardImageWithRetryAsync(overlay.SelectedBitmap);
                var savedPath = await TrySaveCaptureAsync(overlay.SelectedBitmap, false);
                // 普通截图已完成，保持主界面隐藏，避免抢占前台焦点。
                restoreWindowAfterCapture = false;
                if (savedPath is not null)
                {
                    ShowToast($"已保存到 {savedPath}");
                }
                else
                {
                    ShowToast("截图已复制，但无法保存到 E 盘");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 用户取消截图时不显示错误。
        }
        catch (Exception exception)
        {
            ShowToast($"截图失败：{exception.Message}");
        }
        finally
        {
            if (wasVisible && restoreWindowAfterCapture)
            {
                Show();
                WindowState = previousState;
                if (previousState != WindowState.Minimized)
                {
                    Activate();
                }
            }

            _captureInProgress = false;
        }
    }

    /// <summary>直接截取鼠标所在显示器，不显示选区或标注界面。</summary>
    private async Task StartFullScreenCaptureAsync()
    {
        if (_captureInProgress)
        {
            return;
        }

        _captureInProgress = true;
        var wasVisible = IsVisible;
        var previousState = WindowState;
        var clipboardSaved = false;

        try
        {
            var sourceWindow = NativeMethods.GetForegroundWindow();
            NativeMethods.DismissForegroundTransientUi(sourceWindow);
            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            NativeMethods.DwmFlush();
            await Task.Delay(120);
            NativeMethods.DismissForegroundTransientUi(sourceWindow);
            await Task.Delay(40);

            var frame = await _captureBackend.CaptureCurrentMonitorAsync();
            var saveTask = TrySaveCaptureAsync(frame.Bitmap, false);
            await SetClipboardImageWithRetryAsync(frame.Bitmap);
            clipboardSaved = true;
            var savedPath = await saveTask;
            if (savedPath is null)
            {
                ShowToast("全屏截图已复制，但无法保存到截图目录");
            }

            if (Application.Current is App app)
            {
                app.ShowTrayBalloon("全屏截图完成", "截图已保存到剪贴板");
            }
        }
        catch (Exception exception)
        {
            ShowToast($"全屏截图失败：{exception.Message}");
        }
        finally
        {
            if (wasVisible && !clipboardSaved)
            {
                Show();
                WindowState = previousState;
                if (previousState != WindowState.Minimized)
                {
                    Activate();
                }
            }

            _captureInProgress = false;
        }
    }

    private async Task StartScrollCaptureAsync()
    {
        if (_captureInProgress)
        {
            return;
        }

        _captureInProgress = true;
        var wasVisible = IsVisible;
        var previousState = WindowState;
        var restoreWindowAfterCapture = true;

        try
        {
            var sourceWindow = NativeMethods.GetForegroundWindow();
            NativeMethods.DismissForegroundTransientUi(sourceWindow);
            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            NativeMethods.DwmFlush();
            await Task.Delay(240);
            NativeMethods.DismissForegroundTransientUi(sourceWindow);
            await Task.Delay(40);

            var frame = await _scrollCaptureBackend.CaptureCurrentMonitorAsync();
            var overlay = new SelectionOverlayWindow(frame, SelectionPurpose.ScrollCaptureRegion);
            var confirmed = overlay.ShowDialog() == true;
            if (!confirmed || overlay.SelectedScreenBounds is not Int32Rect screenRegion)
            {
                return;
            }

            await CaptureScrollRegionAsync(screenRegion);
            // 长截图已完成，保持主界面隐藏，避免抢占前台焦点。
            restoreWindowAfterCapture = false;
        }
        catch (OperationCanceledException)
        {
            // 用户按 Esc 取消长截图时不显示错误。
        }
        catch (Exception exception)
        {
            ShowToast($"长截图失败：{exception.Message}");
        }
        finally
        {
            if (wasVisible && restoreWindowAfterCapture)
            {
                Show();
                WindowState = previousState;
                if (previousState != WindowState.Minimized)
                {
                    Activate();
                }
            }

            _captureInProgress = false;
        }
    }

    /// <summary>
    /// 复用同一个已确认区域进入自由长截图会话。
    /// 该入口同时服务于独立长截图和普通截图工具栏中的“长截图”。
    /// </summary>
    private async Task CaptureScrollRegionAsync(Int32Rect screenRegion)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        NativeMethods.DwmFlush();
        await Task.Delay(140);

#if SCROLL_CAPTURE_TEST
        _ = DriveScrollCaptureTestInputAsync();
#endif
        var result = await _scrollCaptureService.CaptureInteractiveAsync(screenRegion);
        // 保存历史与写入系统剪贴板互不依赖，先并行启动磁盘编码，
        // 避免用户必须等待“保存 PNG + 刷新历史”后才能完成复制。
        var saveTask = TrySaveCaptureAsync(result.Bitmap, true);
        await SetClipboardImageWithRetryAsync(result.Bitmap);
        var savedPath = await saveTask;
        if (savedPath is not null)
        {
            ShowToast($"长截图已保存 · {result.FrameCount} 帧 · {result.StopReason}");
        }
        else
        {
            ShowToast("长截图已复制，但无法保存到 E 盘");
        }
    }

    private async Task<bool> CaptureScreenRecordingAsync(
        Int32Rect screenRegion,
        ScreenRecordingMode recordingMode,
        bool recordSystemAudio,
        bool recordMicrophone)
    {
        var regionWindow = new ScreenRecordingRegionWindow(screenRegion);
        regionWindow.Show();
        GifRecordingCapture? gifCapture = null;
        try
        {
            await ShowRecordingCountdownAsync(screenRegion);
            var controlWindow = new ScreenRecordingControlWindow(screenRegion);
            controlWindow.Show();
            try
            {
                var options = new ScreenRecordingOptions(
                    screenRegion,
                    recordingMode == ScreenRecordingMode.Mp4 && recordSystemAudio,
                    recordingMode == ScreenRecordingMode.Mp4 && recordMicrophone,
                    recordingMode == ScreenRecordingMode.Gif ? 12 : 15,
                    Mode: recordingMode);
                if (recordingMode == ScreenRecordingMode.Gif)
                {
                    gifCapture = await _screenRecordingService.CaptureGifAsync(
                        options,
                        () => controlWindow.IsStopRequested || NativeMethods.IsEscapePressed(),
                        controlWindow.SetElapsed);
                }
                else
                {
                    var result = await _screenRecordingService.RecordAsync(
                        options,
                        () => controlWindow.IsStopRequested || NativeMethods.IsEscapePressed(),
                        controlWindow.SetElapsed);
                    var audioDescription = result.IncludesSystemAudio && result.IncludesMicrophone
                        ? "电脑声音 + 麦克风"
                        : result.IncludesSystemAudio
                            ? "电脑声音"
                            : result.IncludesMicrophone
                                ? "麦克风"
                                : "静音";
                    ShowToast($"录像已保存（{audioDescription}）");
                }
                if (gifCapture is null)
                {
                    await RefreshHistoryAsync();
                }
            }
            finally
            {
                controlWindow.Close();
            }

            if (gifCapture is not null)
            {
                StartGifSynthesisInBackground(gifCapture);
                return true;
            }
        }
        finally
        {
            regionWindow.Close();
        }

        return false;
    }

    private void StartGifSynthesisInBackground(GifRecordingCapture capture)
    {
        ShowGifSynthesisNotification("GIF 开始合成", "录制已完成，正在后台合成 GIF。", null);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _screenRecordingService.SynthesizeGifAsync(capture);
                ShowGifSynthesisNotification(
                    "GIF 合成完成",
                    $"{Path.GetFileName(result.FilePath)} 已生成，点击查看成品。",
                    () => Dispatcher.BeginInvoke(new Action(() => OpenFilePath(result.FilePath))));
                _ = Dispatcher.BeginInvoke(new Action(() => _ = RefreshHistoryAsync()));
            }
            catch (Exception exception)
            {
                ShowGifSynthesisNotification("GIF 合成失败", exception.Message, null);
            }
        });
    }

    private void ShowGifSynthesisNotification(string title, string text, Action? onClick)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Application.Current is App app)
            {
                app.ShowTrayBalloon(title, text, onClick);
            }
        }));
    }

    private static async Task ShowRecordingCountdownAsync(Int32Rect screenRegion)
    {
        var countdownWindow = new ScreenRecordingCountdownWindow(screenRegion);
        countdownWindow.Show();
        try
        {
            for (var remainingSeconds = 3; remainingSeconds >= 1; remainingSeconds--)
            {
                countdownWindow.SetRemainingSeconds(remainingSeconds);
                await Task.Delay(1000);
            }
        }
        finally
        {
            countdownWindow.Close();
        }
    }

    private async Task SetClipboardImageWithRetryAsync(BitmapSource bitmap)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                ClipboardService.SetImage(bitmap);
                _suppressClipboardCapture = true;
                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
                await Task.Delay(60);
            }
        }

        throw new InvalidOperationException("剪贴板暂时被其他程序占用。", lastError);
    }

    private async Task CaptureExternalClipboardAsync()
    {
        if (_suppressClipboardCapture)
        {
            _suppressClipboardCapture = false;
            return;
        }

        try
        {
            await Task.Delay(80);
            if (Clipboard.ContainsData(ClipboardService.InternalFormat))
            {
                LogClipboardCapture("跳过：X-Tool 内部内容");
                return;
            }
            if (Clipboard.ContainsText())
            {
                var content = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(content))
                {
                    return;
                }

                var signature = $"text:{content}";
                if (signature == _lastExternalClipboardSignature)
                {
                    LogClipboardCapture("跳过：文本签名重复");
                    return;
                }

                _lastExternalClipboardSignature = signature;
                LogClipboardCapture($"文本复制 {content.Length} 字符，开始保存");
                var savedTextPath = await _historyStore.SaveClipboardTextAsync(content);
                LogClipboardCapture($"文本已保存 {savedTextPath}");
                InsertClipboardItem(savedTextPath, isText: true);
                LogClipboardCapture("文本缓存插入完成");
            }
            else if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is null)
                {
                    return;
                }

                var signature = $"image:{image.PixelWidth}x{image.PixelHeight}";
                if (signature == _lastExternalClipboardSignature)
                {
                    LogClipboardCapture("跳过：图片签名重复");
                    return;
                }

                _lastExternalClipboardSignature = signature;
                LogClipboardCapture($"图片复制 {image.PixelWidth}x{image.PixelHeight}，开始保存");
                var savedImagePath = await _historyStore.SaveClipboardImageAsync(image);
                LogClipboardCapture($"图片已保存 {savedImagePath}");
                InsertClipboardItem(savedImagePath, isText: false);
                LogClipboardCapture("图片缓存插入完成");
            }
            else
            {
                return;
            }

            await RefreshHistoryAsync();
            LogClipboardCapture("全量刷新完成");
        }
        catch (Exception exception)
        {
            LogClipboardCapture($"捕获异常：{exception.GetBaseException().Message}");
            // 剪贴板可能被其他程序短暂占用，下一次复制时会自然重试。
        }
    }

    private static void LogClipboardCapture(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "X-Tool",
                "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "clipboard-capture.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志写入失败不影响剪贴板功能。
        }
    }

    /// <summary>复制内容保存后立即进入剪贴板浮窗缓存，不等全量扫描完成。</summary>
    private void InsertClipboardItem(string savedPath, bool isText)
    {
        try
        {
            var item = _historyStore.CreateClipboardItem(savedPath, isText);
            _allHistoryItems = new[] { item }.Concat(_allHistoryItems).Take(200).ToArray();
            // 历史页正在展示时同步更新列表，剪贴板历史与浮窗都能立即看到新内容。
            ApplyHistoryFilter();
        }
        catch (Exception exception)
        {
            LogClipboardCapture($"缓存插入异常：{exception.GetBaseException().Message}");
            // 条目构造失败时由随后的全量刷新兜底。
        }
    }

#if SCROLL_CAPTURE_TEST
    /// <summary>
    /// 专用回归构建以标准滚轮事件模拟真实手动滚动，并包含一次回滚。
    /// 每步保留充足的稳定时间，避免测试驱动本身制造丢帧。
    /// </summary>
    private static async Task DriveScrollCaptureTestInputAsync()
    {
        await Task.Delay(1200);
        await SendTestWheelSequenceAsync(-120, 7);
        await SendTestWheelSequenceAsync(120, 2);
        await SendTestWheelSequenceAsync(-120, 5);
    }

    private static async Task SendTestWheelSequenceAsync(int delta, int count)
    {
        for (var index = 0; index < count; index++)
        {
            _ = NativeMethods.SendMouseWheel(delta);
            await Task.Delay(700);
        }
    }
#endif

    private async Task<string?> TrySaveCaptureAsync(BitmapSource bitmap, bool isLongCapture)
    {
        try
        {
            var savedPath = await _historyStore.SaveAsync(bitmap, isLongCapture);
            // 新文件已经落盘即可结束保存阶段；历史扫描在后台刷新，
            // 不再把遍历目录和解码缩略图时间叠加到复制完成路径。
            _ = RefreshHistoryAsync();
            return savedPath;
        }
        catch
        {
            return null;
        }
    }

    private async Task RefreshHistoryAsync()
    {
        if (_historyRefreshInProgress)
        {
            _historyRefreshPending = true;
            return;
        }

        _historyRefreshInProgress = true;
        try
        {
            _allHistoryItems = await _historyStore.LoadAsync();
            ApplyHistoryFilter();
        }
        catch (Exception exception)
        {
            HistoryEmptyPanel.Visibility = Visibility.Visible;
            HistoryScrollViewer.Visibility = Visibility.Collapsed;
            ShowToast($"剪贴板加载失败：{exception.Message}");
        }
        finally
        {
            _historyRefreshInProgress = false;
            if (_historyRefreshPending)
            {
                _historyRefreshPending = false;
                _ = RefreshHistoryAsync();
            }
        }
    }

    private async void Overlay_HistoryTextCreated(object? sender, HistoryTextContent content)
    {
        try
        {
            await _historyStore.SaveTextAsync(content);
            await RefreshHistoryAsync();
        }
        catch (Exception exception)
        {
            ShowToast($"保存{(content.Kind == HistoryEntryKind.Translation ? "翻译" : "文字提取")}记录失败：{exception.Message}");
        }
    }

    private void HistoryFilterButton_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag?.ToString();
        _historyFilter = tag switch
        {
            "Screenshot" => HistoryEntryKind.Screenshot,
            "LongScreenshot" => HistoryEntryKind.LongScreenshot,
            "TextExtraction" => HistoryEntryKind.TextExtraction,
            "Translation" => HistoryEntryKind.Translation,
            "ScreenRecording" => HistoryEntryKind.ScreenRecording,
            "ExternalClipboard" => HistoryEntryKind.ExternalClipboard,
            _ => null
        };
        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        var filteredItems = _historyFilter is null
            ? _allHistoryItems
            : _allHistoryItems.Where(item => item.Kind == _historyFilter.Value).ToArray();
        _historyItems.Clear();
        _textHistoryItems.Clear();
        foreach (var item in filteredItems)
        {
            if (item.IsTextRecord)
            {
                _textHistoryItems.Add(item);
            }
            else
            {
                _historyItems.Add(item);
            }
        }

        var hasItems = _historyItems.Count + _textHistoryItems.Count > 0;
        HistoryEmptyPanel.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        HistoryScrollViewer.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        UpdateHistoryFilterStates();
    }

    private void UpdateHistoryFilterStates()
    {
        var buttons = new (Button Button, HistoryEntryKind? Kind)[]
        {
            (AllHistoryFilterButton, null),
            (ScreenshotHistoryFilterButton, HistoryEntryKind.Screenshot),
            (LongScreenshotHistoryFilterButton, HistoryEntryKind.LongScreenshot),
            (TextExtractionHistoryFilterButton, HistoryEntryKind.TextExtraction),
            (TranslationHistoryFilterButton, HistoryEntryKind.Translation),
            (ScreenRecordingHistoryFilterButton, HistoryEntryKind.ScreenRecording),
            (ExternalClipboardHistoryFilterButton, HistoryEntryKind.ExternalClipboard)
        };
        foreach (var (button, kind) in buttons)
        {
            var selected = kind == _historyFilter;
            button.Background = new SolidColorBrush(selected ? Color.FromRgb(222, 236, 255) : Color.FromArgb(140, 255, 255, 255));
            button.BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(102, 158, 255) : Color.FromArgb(180, 255, 255, 255));
            button.Foreground = new SolidColorBrush(selected ? Color.FromRgb(35, 111, 224) : Color.FromRgb(86, 96, 108));
        }
    }

    private void OpenStorageFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var historyRoot = Path.GetDirectoryName(_preferences.ScreenshotDirectory) ?? _preferences.ScreenshotDirectory;
            Directory.CreateDirectory(historyRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = historyRoot,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ShowToast($"无法打开存储位置：{exception.Message}");
        }
    }

    private void StickerTopmostCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _preferences.StickerTopmost = StickerTopmostCheckBox.IsChecked == true;
        _preferences.Save();
    }

    private void DesktopPetScaleSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (DesktopPetScaleSlider is null || DesktopPetScaleValueText is null)
        {
            return;
        }

        var scalePercent = Math.Clamp((int)Math.Round(e.NewValue / 5d) * 5, 60, 160);
        UpdateDesktopPetScaleText(scalePercent);
        if (_preferences.DesktopPetScalePercent != scalePercent)
        {
            _preferences.DesktopPetScalePercent = scalePercent;
            _preferences.Save();
        }

        if (Application.Current is App app)
        {
            app.UpdateDesktopPetScale(scalePercent);
        }
    }

    private void UpdateDesktopPetScaleText(int scalePercent)
    {
        if (DesktopPetScaleValueText is null)
        {
            return;
        }

        var displayPixels = (int)Math.Round(288 * scalePercent / 100d);
        DesktopPetScaleValueText.Text = $"{scalePercent}% · {displayPixels} DIP";
    }

    private void StartWithWindowsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var enabled = StartWithWindowsCheckBox.IsChecked == true;
        if (!AutoStartService.TrySetEnabled(enabled, out var error))
        {
            StartWithWindowsCheckBox.IsChecked = AutoStartService.IsEnabled();
            ShowToast($"开机自启动设置失败：{error}");
            return;
        }

        _preferences.StartWithWindows = enabled;
        _preferences.Save();
        ShowToast(enabled ? "已开启开机自启动" : "已关闭开机自启动");
    }

    private async void StorageLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string category })
        {
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = $"选择{category}的保存位置",
            UseDescriptionForTitle = true,
            SelectedPath = GetStorageLocation(category)
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        SetStorageLocation(category, dialog.SelectedPath);
        _preferences.Save();
        UpdateStorageLocationText();
        await RefreshHistoryAsync();
        ShowToast($"已更新{category}的存储位置");
    }

    private string GetStorageLocation(string category) => category switch
    {
        "截图" => _preferences.ScreenshotDirectory,
        "长截图" => _preferences.LongScreenshotDirectory,
        "文字提取" => _preferences.TextExtractionDirectory,
        "翻译" => _preferences.TranslationDirectory,
        "屏幕录制" => _preferences.RecordingDirectory,
        "外部复制" => _preferences.ClipboardDirectory,
        _ => string.Empty
    };

    private void SetStorageLocation(string category, string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        switch (category)
        {
            case "截图":
                _preferences.ScreenshotDirectory = fullPath;
                break;
            case "长截图":
                _preferences.LongScreenshotDirectory = fullPath;
                break;
            case "文字提取":
                _preferences.TextExtractionDirectory = fullPath;
                break;
            case "翻译":
                _preferences.TranslationDirectory = fullPath;
                break;
            case "屏幕录制":
                _preferences.RecordingDirectory = fullPath;
                break;
            case "外部复制":
                _preferences.ClipboardDirectory = fullPath;
                break;
        }
    }

    private void UpdateStorageLocationText()
    {
        ScreenshotStoragePathText.Text = _preferences.ScreenshotDirectory;
        LongScreenshotStoragePathText.Text = _preferences.LongScreenshotDirectory;
        TextExtractionStoragePathText.Text = _preferences.TextExtractionDirectory;
        TranslationStoragePathText.Text = _preferences.TranslationDirectory;
        RecordingStoragePathText.Text = _preferences.RecordingDirectory;
        ClipboardStoragePathText.Text = _preferences.ClipboardDirectory;
    }

    private void OpenHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string filePath } || !File.Exists(filePath))
        {
            ShowToast("截图文件已不存在，历史列表将自动刷新");
            _ = RefreshHistoryAsync();
            return;
        }

        OpenFilePath(filePath);
    }

    private void OpenFilePath(string filePath)
    {
        if (!File.Exists(filePath))
        {
            ShowToast("GIF 文件已不存在，请刷新历史记录");
            _ = RefreshHistoryAsync();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ShowToast($"无法打开文件：{exception.Message}");
        }
    }

    private void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        NavigateToPage("History");
    }

    private async Task ShowClipboardPickerAsync()
    {
        if (_clipboardPicker is not null)
        {
            _clipboardPicker.Close();
            return;
        }

        // 打开浮窗前同步捕获最近一次外部复制，避免刚复制的内容还未写入历史缓存。
        try
        {
            if (!Clipboard.ContainsData(ClipboardService.InternalFormat))
            {
                await CaptureExternalClipboardAsync();
            }
        }
        catch
        {
            // 捕获失败时仍按现有缓存展示。
        }

        _clipboardPasteTarget = NativeMethods.GetForegroundWindow();
        var choices = _allHistoryItems
            .Where(item => item.Kind != HistoryEntryKind.ScreenRecording &&
                           (item.IsTextRecord || Path.GetExtension(item.FilePath).Equals(".png", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (choices.Length == 0)
        {
            ShowToast("剪贴板还没有可粘贴的内容");
            return;
        }

        var picker = new ClipboardPickerWindow(choices);
        if (NativeMethods.GetCursorPos(out var cursor))
        {
            var workArea = SystemParameters.WorkArea;
            picker.Left = Math.Clamp(cursor.X - picker.Width / 2, workArea.Left + 8, workArea.Right - picker.Width - 8);
            picker.Top = Math.Clamp(cursor.Y - 72, workArea.Top + 8, workArea.Bottom - picker.Height - 8);
        }
        _clipboardPicker = picker;
        picker.ItemSelected += ClipboardPicker_ItemSelected;
        picker.Closed += (_, _) =>
        {
            if (ReferenceEquals(_clipboardPicker, picker))
            {
                _clipboardPicker = null;
            }
        };
        picker.Show();
    }

    private async void ClipboardPicker_ItemSelected(object? sender, ScreenshotHistoryItem item)
    {
        if (sender is not ClipboardPickerWindow picker)
        {
            return;
        }

        picker.Close();
        try
        {
            if (item.IsTextRecord)
            {
                ClipboardService.SetText(await File.ReadAllTextAsync(item.FilePath));
            }
            else
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(item.FilePath, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                ClipboardService.SetImage(image);
            }
            _suppressClipboardCapture = true;

            if (_clipboardPasteTarget != IntPtr.Zero)
            {
                if (!await EnsurePasteTargetReadyAsync(_clipboardPasteTarget))
                {
                    ShowToast("无法恢复原输入窗口，内容已复制到系统剪贴板");
                    return;
                }

                if (!NativeMethods.SendPasteShortcut())
                {
                    ShowToast("未能自动粘贴，内容已复制到系统剪贴板");
                }
            }
        }
        catch (Exception exception)
        {
            ShowToast($"粘贴失败：{exception.Message}");
        }
    }

    private void RegisterVoiceInputHotKey(IntPtr handle)
    {
        if (!_preferences.VoiceInputEnabled || _voiceInputHotKeyRegistered)
        {
            return;
        }

        if (!_voiceInputShortcut.IsRightAlt)
        {
            _voiceInputHotKeyMonitor = new RightAltHotKeyMonitor(listenRightAlt: false);
            _voiceInputHotKeyMonitor.EscapePressed += VoiceInputHotKeyMonitor_EscapePressed;
            _voiceInputHotKeyMonitor.TranslationPressed += VoiceInputHotKeyMonitor_TranslationPressed;
            _voiceInputHotKeyRegistered = RegisterStandardShortcut(handle, NativeMethods.VoiceHotKeyId, _voiceInputShortcut);
            return;
        }

        _voiceInputHotKeyMonitor = new RightAltHotKeyMonitor();
        _voiceInputHotKeyMonitor.Pressed += VoiceInputHotKeyMonitor_Pressed;
        _voiceInputHotKeyMonitor.EscapePressed += VoiceInputHotKeyMonitor_EscapePressed;
        _voiceInputHotKeyMonitor.TranslationPressed += VoiceInputHotKeyMonitor_TranslationPressed;
        _voiceInputHotKeyRegistered = _voiceInputHotKeyMonitor.IsInstalled;
        if (!_voiceInputHotKeyRegistered)
        {
            _voiceInputHotKeyMonitor.Dispose();
            _voiceInputHotKeyMonitor = null;
        }
    }

    private void RegisterFullScreenHotKey(IntPtr handle)
    {
        UnregisterFullScreenHotKey();
        if (_fullScreenShortcut.IsRightCtrl)
        {
            var monitor = new RightControlHotKeyMonitor();
            if (!monitor.IsInstalled)
            {
                monitor.Dispose();
                _fullScreenHotKeyRegistered = false;
                return;
            }

            monitor.Pressed += FullScreenHotKeyMonitor_Pressed;
            _fullScreenHotKeyMonitor = monitor;
            _fullScreenHotKeyRegistered = true;
            return;
        }

        _fullScreenHotKeyRegistered = RegisterStandardShortcut(
            handle,
            NativeMethods.FullScreenHotKeyId,
            _fullScreenShortcut);
    }

    private void UnregisterFullScreenHotKey()
    {
        _fullScreenHotKeyMonitor?.Dispose();
        _fullScreenHotKeyMonitor = null;
        if (_windowSource is not null && _fullScreenHotKeyRegistered && !_fullScreenShortcut.IsRightCtrl)
        {
            NativeMethods.UnregisterHotKey(_windowSource.Handle, NativeMethods.FullScreenHotKeyId);
        }

        _fullScreenHotKeyRegistered = false;
    }

    private void FullScreenHotKeyMonitor_Pressed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // 语音输入期间右 Ctrl 保留为翻译操作，避免一次按键同时触发两个功能。
            if (_voiceInputService.IsRecording || _voiceInputAwaitingConfirmation || _voiceTranslationPreviewActive)
            {
                return;
            }

            _ = StartFullScreenCaptureAsync();
        });
    }

    private static bool RegisterStandardShortcut(IntPtr handle, int hotKeyId, GlobalShortcut shortcut) =>
        shortcut.IsSupportedGlobalCombination && NativeMethods.RegisterHotKey(
            handle,
            hotKeyId,
            shortcut.NativeModifiers,
            shortcut.VirtualKey);

    private void InstallScreenshotHotKeySuppressor()
    {
        _screenshotHotKeySuppressor?.Dispose();
        _screenshotHotKeySuppressor = null;
        Interlocked.Exchange(ref _screenshotHookTriggeredAt, 0);
        if (!_screenshotShortcut.IsSupportedGlobalCombination)
        {
            return;
        }

        var suppressor = new ScreenshotHotKeySuppressor(_screenshotShortcut);
        if (!suppressor.IsInstalled)
        {
            suppressor.Dispose();
            return;
        }

        suppressor.Pressed += ScreenshotHotKeySuppressor_Pressed;
        _screenshotHotKeySuppressor = suppressor;
    }

    private void ScreenshotHotKeySuppressor_Pressed(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _screenshotHookTriggeredAt, Environment.TickCount64);
        Dispatcher.BeginInvoke(() => _ = StartRegionCaptureAsync());
    }

    private bool ConsumeScreenshotHookTrigger()
    {
        var triggeredAt = Interlocked.Exchange(ref _screenshotHookTriggeredAt, 0);
        return triggeredAt > 0 && Environment.TickCount64 - triggeredAt <= 500;
    }

    private void VoiceInputHotKeyMonitor_Pressed(object? sender, VoiceHotKeyPressedEventArgs e)
    {
        // 低级键盘钩子里的前台窗口才是用户按下右 Alt 时的原始目标。
        // 不要等到 UI 队列执行后再重新读取，否则有机会把非激活浮窗或其它瞬时窗口当成目标。
        Dispatcher.BeginInvoke(() => _ = ToggleVoiceInputAsync(e.ForegroundWindow));
    }

    private void VoiceInputHotKeyMonitor_EscapePressed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(CancelVoiceInput);
    }

    private void VoiceInputHotKeyMonitor_TranslationPressed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => _ = TranslateAndFinishVoiceInputAsync());
    }

    private void CancelVoiceInput()
    {
        if (_voiceInputAwaitingConfirmation)
        {
            _voiceInputAwaitingConfirmation = false;
            _voiceTranslationPreviewActive = false;
            _voiceInputTranslatedText = string.Empty;
            _voiceInputCommittedText = string.Empty;
            CancelVoiceTranslation();
            CloseVoiceInputOverlay();
            ShowToast("已取消待确认的翻译结果");
            return;
        }

        if (!_voiceInputService.IsRecording)
        {
            return;
        }

        if (_voiceTranslationPreviewActive)
        {
            _voiceTranslationPreviewActive = false;
            _voiceInputTranslatedText = string.Empty;
            CancelVoiceTranslation();
            _voiceInputOverlay?.ClearTranslationPreview();
            _voiceInputOverlay?.SetRecognizedText(_voiceInputCommittedText);
            ShowToast("已返回中文输入；再次按 Esc 将取消语音输入");
            return;
        }

        _voiceInputCancellation?.Cancel();
        CancelVoiceTranslation();
        _voiceInputService.Cancel();
        _voiceInputCommittedText = string.Empty;
        CloseVoiceInputOverlay();
        ShowToast("语音输入已取消");
    }

    private async Task ToggleVoiceInputAsync(IntPtr capturedPasteTarget = default)
    {
        if (!_preferences.VoiceInputEnabled)
        {
            ShowToast("本地语音输入已在设置中关闭");
            return;
        }

        if (_voiceInputAwaitingConfirmation)
        {
            await ConfirmVoiceInputAsync();
            return;
        }

        if (_voiceInputService.IsRecording)
        {
            await FinishVoiceInputAsync();
            return;
        }

        try
        {
            _voiceInputPasteTarget = capturedPasteTarget != IntPtr.Zero
                ? capturedPasteTarget
                : NativeMethods.GetForegroundWindow();
            _voiceInputCancellation?.Cancel();
            _voiceInputCancellation?.Dispose();
            _voiceInputCancellation = new CancellationTokenSource();
            _voiceInputCommittedText = string.Empty;
            _voiceInputTranslatedText = string.Empty;
            _voiceTranslationPreviewActive = false;
            _voiceInputAwaitingConfirmation = false;
            _voiceInputService.Start();
            ShowVoiceInputOverlay("正在聆听…", "再次按右 Alt 结束");
            _voiceInputOverlay?.ClearRecognizedText();
        }
        catch (Exception exception)
        {
            CloseVoiceInputOverlay();
            ShowToast($"无法启动语音输入：{exception.Message}");
        }
    }

    private void VoiceInputService_AutoStopRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(async () => await FinishVoiceInputAsync());
    }

    private void VoiceInputService_SoundLevelChanged(double level)
    {
        Dispatcher.BeginInvoke(() => _voiceInputOverlay?.UpdateLevel(level));
    }

    private void VoiceInputService_RecordingFaulted(string message)
    {
        Dispatcher.BeginInvoke(() => ShowToast(message));
    }

    private void VoiceInputService_PartialResultAvailable(string hypothesis)
    {
        Dispatcher.BeginInvoke(() => CommitVoiceInputPartial(hypothesis));
    }

    private void CommitVoiceInputPartial(string hypothesis)
    {
        if (!_voiceInputService.IsRecording)
        {
            return;
        }

        // 气泡属于 X-Tool 自己的预览界面，可直接替换为最新片段，避免队列回改时出现缩回和重打。
        _voiceInputCommittedText = hypothesis;
        if (_voiceTranslationPreviewActive)
        {
            _ = RefreshVoiceTranslationPreviewAsync(hypothesis);
        }
        else
        {
            _voiceInputOverlay?.SetRecognizedText(hypothesis);
        }
    }

    private async Task TranslateAndFinishVoiceInputAsync()
    {
        if (!_voiceInputService.IsRecording)
        {
            return;
        }

        if (!TranslationEngineProvider.ChineseToEnglish.IsReady)
        {
            ShowToast("中译英离线模型未安装，暂不能启用英文输入预览");
        }
        else
        {
            _voiceTranslationPreviewActive = true;
        }

        // 翻译键用于锁定这一段口述：先停止采集并进行最终识别，不能继续让环境声改写原句。
        await FinishVoiceInputAsync(waitForConfirmation: true);
    }

    private async Task RefreshVoiceTranslationPreviewAsync(string sourceText)
    {
        if (!_voiceTranslationPreviewActive || string.IsNullOrWhiteSpace(sourceText))
        {
            return;
        }

        var previous = _voiceTranslationCancellation;
        previous?.Cancel();
        var cancellation = new CancellationTokenSource();
        _voiceTranslationCancellation = cancellation;
        try
        {
            var result = await TranslationEngineProvider.ChineseToEnglish.TranslateAsync(
                new TranslationRequest(sourceText)
                {
                    SourceLanguage = "zh-Hans",
                    TargetLanguage = "en"
                },
                cancellation.Token);
            if (cancellation.IsCancellationRequested || !_voiceTranslationPreviewActive ||
                !string.Equals(sourceText, _voiceInputCommittedText, StringComparison.Ordinal))
            {
                return;
            }

            _voiceInputTranslatedText = result.TranslatedText.Trim();
            if (string.IsNullOrWhiteSpace(_voiceInputTranslatedText))
            {
                ShowToast("未能生成英文翻译，请重新开始语音输入后重试");
                return;
            }

            _voiceInputOverlay?.SetTranslationPreview(sourceText, _voiceInputTranslatedText);
        }
        catch (OperationCanceledException)
        {
            // 新的识别片段会替换旧翻译；取消时无需提示。
        }
        catch (Exception exception)
        {
            if (_voiceTranslationPreviewActive)
            {
                ShowToast($"中译英失败：{exception.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(_voiceTranslationCancellation, cancellation))
            {
                _voiceTranslationCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelVoiceTranslation()
    {
        _voiceTranslationCancellation?.Cancel();
        _voiceTranslationCancellation = null;
    }

    private async Task FinishVoiceInputAsync(bool waitForConfirmation = false)
    {
        if (!_voiceInputService.IsRecording)
        {
            return;
        }

        var cancellation = _voiceInputCancellation;
        if (cancellation is null)
        {
            return;
        }

        try
        {
            ShowVoiceInputOverlay("正在识别…", "所有音频仅在本机内存中处理");
            var text = await _voiceInputService.StopAndRecognizeAsync(cancellation.Token);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = _voiceInputCommittedText;
                if (string.IsNullOrWhiteSpace(text))
                {
                    ShowToast($"未识别到文字（{_voiceInputService.CaptureDiagnostics}）。请检查系统麦克风权限和输入设备。");
                    return;
                }
            }

            var outputText = text.Trim();
            if (_voiceTranslationPreviewActive)
            {
                _voiceInputCommittedText = outputText;
                await RefreshVoiceTranslationPreviewAsync(outputText);
                if (string.IsNullOrWhiteSpace(_voiceInputTranslatedText))
                {
                    ShowToast("英文翻译尚未完成，未执行粘贴");
                    return;
                }

                outputText = _voiceInputTranslatedText;
                _voiceInputOverlay?.SetTranslationPreview(text, outputText);
            }
            else
            {
                _voiceInputOverlay?.SetRecognizedText(text);
            }

            if (waitForConfirmation && _voiceTranslationPreviewActive)
            {
                _voiceInputCommittedText = outputText;
                _voiceInputAwaitingConfirmation = true;
                ShowToast("翻译完成，再次按右 Alt 确认输入；按 Esc 取消");
                return;
            }

            if (!_preferences.VoiceInputPasteAutomatically || _voiceInputPasteTarget == IntPtr.Zero)
            {
                ShowToast(await SetVoiceInputClipboardTextAsync(outputText)
                    ? "识别结果已复制到系统剪贴板"
                    : "识别结果已生成，但暂时无法写入剪贴板");
                return;
            }

            _voiceInputCommittedText = outputText;
            if (await PasteVoiceInputTextAsync(outputText, showSuccess: true))
            {
                await SetVoiceInputClipboardTextAsync(outputText);
            }
        }
        catch (OperationCanceledException)
        {
            ShowToast("语音输入已取消");
        }
        catch (Exception exception)
        {
            ShowToast($"语音识别失败：{exception.Message}");
        }
        finally
        {
            CancelVoiceTranslation();
            _voiceTranslationPreviewActive = false;
            if (!_voiceInputAwaitingConfirmation)
            {
                CloseVoiceInputOverlay();
            }
            cancellation.Dispose();
            if (ReferenceEquals(_voiceInputCancellation, cancellation))
            {
                _voiceInputCancellation = null;
            }
        }
    }

    private async Task ConfirmVoiceInputAsync()
    {
        var outputText = _voiceInputCommittedText;
        _voiceInputAwaitingConfirmation = false;
        _voiceTranslationPreviewActive = false;
        CloseVoiceInputOverlay();

        if (string.IsNullOrWhiteSpace(outputText))
        {
            ShowToast("没有可确认的翻译结果");
            return;
        }

        if (!_preferences.VoiceInputPasteAutomatically || _voiceInputPasteTarget == IntPtr.Zero)
        {
            ShowToast(await SetVoiceInputClipboardTextAsync(outputText)
                ? "翻译结果已确认并复制到系统剪贴板"
                : "翻译结果已确认，但暂时无法写入剪贴板");
            return;
        }

        if (await PasteVoiceInputTextAsync(outputText, showSuccess: true))
        {
            await SetVoiceInputClipboardTextAsync(outputText);
        }
    }

    private async Task<bool> PasteVoiceInputTextAsync(string text, bool showSuccess)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!await SetVoiceInputClipboardTextAsync(text))
        {
            if (showSuccess)
            {
                ShowToast("无法写入识别结果到剪贴板，请重试");
            }

            return false;
        }
        // 语音浮窗从创建起就不激活目标应用。若用户录音期间主动切换了窗口，
        // 不强行恢复浏览器等应用：它们往往会把内部焦点默认切到地址栏，导致误粘贴。
        if (!NativeMethods.IsWindowForeground(_voiceInputPasteTarget))
        {
            if (showSuccess)
            {
                ShowToast("原输入窗口焦点已变化，识别结果已复制，请手动粘贴");
            }

            return false;
        }

        // 右 Alt 的原始按键会被拦截以保护浏览器等应用的输入焦点。
        // 留出一个短暂窗口让目标程序处理抬键和剪贴板更新，再检查一次前台状态，避免 Ctrl+V 偶发丢失。
        await Task.Delay(75);
        if (!NativeMethods.IsWindowForeground(_voiceInputPasteTarget))
        {
            if (showSuccess)
            {
                ShowToast("原输入窗口焦点已变化，识别结果已复制，请手动粘贴");
            }

            return false;
        }

        // 部分剪贴板管理器会短暂改写内容；发送 Ctrl+V 前再次确认仍是本次识别结果。
        if (!ClipboardMatchesVoiceInputText(text) && !await SetVoiceInputClipboardTextAsync(text))
        {
            if (showSuccess)
            {
                ShowToast("识别结果已生成，但剪贴板内容被占用，请手动重试");
            }

            return false;
        }

        // 与剪贴板快速粘贴保持一致：只发送一次，避免少数应用收到重复输入。
        if (!NativeMethods.SendPasteShortcut())
        {
            if (showSuccess)
            {
                ShowToast("识别结果已复制，请手动粘贴");
            }

            return false;
        }

        if (showSuccess)
        {
            ShowToast($"已识别 {text.Length} 个字符并粘贴");
        }

        return true;
    }

    private static async Task<bool> EnsurePasteTargetReadyAsync(IntPtr target)
    {
        var activated = NativeMethods.IsWindowForeground(target);
        var focusRestored = false;
        for (var attempt = 0; attempt < 3 && !activated; attempt++)
        {
            activated = NativeMethods.RestoreAndActivateWindow(target);
            focusRestored |= activated;
            if (!activated)
            {
                await Task.Delay(40);
            }
        }

        if (focusRestored)
        {
            // 仅在刚恢复外部窗口时留出一帧时间让其内部控件重新接收输入。
            await Task.Delay(35);
        }

        return activated;
    }

    private async Task<bool> SetVoiceInputClipboardTextAsync(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                ClipboardService.SetText(text);
                _suppressClipboardCapture = true;
                await Task.Delay(35);
                if (ClipboardMatchesVoiceInputText(text))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 剪贴板短暂被系统或其他程序占用时继续重试。
            }

            await Task.Delay(45);
        }

        return false;
    }

    private static bool ClipboardMatchesVoiceInputText(string expected)
    {
        try
        {
            return Clipboard.ContainsText() && string.Equals(Clipboard.GetText(), expected, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ShowVoiceInputOverlay(string title, string detail)
    {
        if (_voiceInputOverlay is null)
        {
            _voiceInputOverlay = new VoiceInputOverlayWindow();
            _voiceInputOverlay.Closed += (_, _) => _voiceInputOverlay = null;
            _voiceInputOverlay.SizeChanged += (_, _) => PositionVoiceInputOverlay();
            _voiceInputOverlay.Show();
            Dispatcher.BeginInvoke(PositionVoiceInputOverlay, DispatcherPriority.Loaded);
        }

        _voiceInputOverlay.EnsureTopmostWithoutActivation();
        _voiceInputOverlay.UpdateStatus(title, detail);
    }

    private void PositionVoiceInputOverlay()
    {
        if (_voiceInputOverlay is null || !_voiceInputOverlay.IsVisible)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        _voiceInputOverlay.Left = workArea.Left + (workArea.Width - _voiceInputOverlay.ActualWidth) / 2;
        _voiceInputOverlay.Top = workArea.Bottom - _voiceInputOverlay.ActualHeight - 28;
    }

    private void CloseVoiceInputOverlay()
    {
        _voiceInputOverlay?.Close();
        _voiceInputOverlay = null;
    }

    private void VoiceInputEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _preferences.VoiceInputEnabled = VoiceInputEnabledCheckBox.IsChecked == true;
        _preferences.Save();
        if (!_preferences.VoiceInputEnabled)
        {
            _voiceInputService.Cancel();
            CloseVoiceInputOverlay();
            _voiceInputHotKeyMonitor?.Dispose();
            _voiceInputHotKeyMonitor = null;
            if (_windowSource is not null && _voiceInputHotKeyRegistered && !_voiceInputShortcut.IsRightAlt)
            {
                NativeMethods.UnregisterHotKey(_windowSource.Handle, NativeMethods.VoiceHotKeyId);
            }
            _voiceInputHotKeyRegistered = false;
            return;
        }

        if (_windowSource is not null)
        {
            RegisterVoiceInputHotKey(_windowSource.Handle);
        }
        ShowToast(_voiceInputHotKeyRegistered ? "本地语音输入已开启" : "快捷键被其他程序占用");
    }

    private void VoiceInputPasteAutomaticallyCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _preferences.VoiceInputPasteAutomatically = VoiceInputPasteAutomaticallyCheckBox.IsChecked == true;
        _preferences.Save();
    }

    private void NavigateToWorkbench_Click(object sender, RoutedEventArgs e)
    {
        ScreenWorkbenchNav.IsChecked = true;
        NavigateToPage("ScreenWorkbench");
    }

    private void NavigateToConverterWorkbench_Click(object sender, RoutedEventArgs e)
    {
        ConverterWorkbenchNav.IsChecked = true;
        NavigateToPage("ConverterWorkbench");
    }

    private void HomeNavigate_Click(object sender, RoutedEventArgs e)
    {
        var page = (sender as FrameworkElement)?.Tag?.ToString();
        switch (page)
        {
            case "FileWorkbench":
                FileWorkbenchNav.IsChecked = true;
                break;
            case "NetworkWorkbench":
                NetworkWorkbenchNav.IsChecked = true;
                break;
            case "ResourceManagement":
                ResourceManagementNav.IsChecked = true;
                break;
            case "SystemTools":
                SystemToolsNav.IsChecked = true;
                break;
            case "DeveloperTools":
                DeveloperToolsNav.IsChecked = true;
                break;
            case "Shortcuts":
                OpenShortcutSettings();
                return;
            case "Settings":
                SettingsNav.IsChecked = true;
                break;
            default:
                return;
        }

        NavigateToPage(page);
    }

    private void OpenImageConverter_Click(object sender, RoutedEventArgs e)
    {
        ConverterWorkbenchNav.IsChecked = true;
        NavigateToPage("ImageConverter");
    }

    private void ConverterToolRail_ToolRequested(object? sender, ConverterToolRequestedEventArgs e)
    {
        ConverterWorkbenchNav.IsChecked = true;
        NavigateToPage(e.Tool switch
        {
            "Audio" => "AudioConverter",
              "Video" => "VideoConverter",
              "Pdf" => "PdfConverter",
              "Encoding" => "EncodingConverter",
              "QrCode" => "QrCodeConverter",
              _ => "ImageConverter"
        });
    }

    private void BackToConverterWorkbench_Click(object sender, RoutedEventArgs e)
    {
        NavigateToPage("ConverterWorkbench");
    }

    private void SelectImageFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择需要处理的图片",
            Multiselect = true,
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|PNG|*.png|JPEG|*.jpg;*.jpeg|BMP|*.bmp|TIFF|*.tif;*.tiff"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _imageConversionFiles.Clear();
        foreach (var fileName in dialog.FileNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _imageConversionFiles.Add(ImageConversionQueueItem.Create(fileName));
        }

        UpdateImageFileSummary();
        UpdateImagePreviews();
    }

    private void ClearImageFileList_Click(object sender, RoutedEventArgs e)
    {
        _imageConversionFiles.Clear();
        UpdateImageFileSummary();
        UpdateImagePreviews();
        ImageConversionStatusText.Text = string.Empty;
    }

    private void ResetImageConversionSettings_Click(object sender, RoutedEventArgs e)
    {
        ImageFormatComboBox.SelectedIndex = 0;
        ImageQualitySlider.Value = 88;
        ImageScaleSlider.Value = 100;
        ImageConversionStatusText.Text = string.Empty;
        UpdateImageConversionControls();
    }

    private void SelectConverterOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择转换结果的保存位置",
            UseDescriptionForTitle = true,
            SelectedPath = _imageOutputDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        _imageOutputDirectory = Path.GetFullPath(dialog.SelectedPath);
        ConverterOutputFolderText.Text = _imageOutputDirectory;
    }

    private void ImageFormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateImageConversionControls();
    }

    private void ImageQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateImageConversionControls();
    }

    private void ImageScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateImageConversionControls();
    }

    private void UpdateImageFileSummary()
    {
        ImageFilesSummaryText.Text = _imageConversionFiles.Count == 0
            ? "支持 PNG、JPEG、BMP、TIFF，可一次选择多张"
            : $"已添加 {_imageConversionFiles.Count} 张图片，处理结果会生成到新文件中";
    }

    private void UpdateImagePreviews()
    {
        var firstFile = _imageConversionFiles.FirstOrDefault();
        if (firstFile is null)
        {
            ImagePreviewBefore.Source = null;
            ImagePreviewAfter.Source = null;
            ImagePreviewBeforeInfoText.Text = "等待添加图片";
            ImagePreviewAfterInfoText.Text = "调整参数后实时更新";
            return;
        }

        ImagePreviewBefore.Source = ImageConversionService.CreatePreview(firstFile.FilePath, ImageOutputFormat.Png, 100, 100);
        ImagePreviewAfter.Source = ImageConversionService.CreatePreview(
            firstFile.FilePath,
            GetSelectedImageFormat(),
            (int)Math.Round(ImageScaleSlider.Value),
            (int)Math.Round(ImageQualitySlider.Value));
        var previewScale = Math.Clamp(ImageScaleSlider.Value, 10, 100) / 100d;
        ImagePreviewAfter.RenderTransformOrigin = new Point(0.5, 0.5);
        ImagePreviewAfter.RenderTransform = new ScaleTransform(previewScale, previewScale);

        var scalePercent = (int)Math.Round(ImageScaleSlider.Value);
        ImagePreviewBeforeInfoText.Text = $"原图 {firstFile.DimensionDisplay} · {firstFile.FileSize}";
        var outputDimensions = firstFile.PixelWidth > 0 && firstFile.PixelHeight > 0
            ? $"{Math.Max(1, (int)Math.Round(firstFile.PixelWidth * scalePercent / 100d))} × {Math.Max(1, (int)Math.Round(firstFile.PixelHeight * scalePercent / 100d))}"
            : "尺寸将在转换时确定";
        ImagePreviewAfterInfoText.Text = $"输出 {outputDimensions} · {GetImagePreviewSettingText()}";
    }

    private string GetImagePreviewSettingText()
    {
        return GetSelectedImageFormat() switch
        {
            ImageOutputFormat.Jpeg => $"JPEG {Math.Round(ImageQualitySlider.Value)}%",
            ImageOutputFormat.Png => "PNG 无损",
            ImageOutputFormat.Bmp => "BMP 位图",
            ImageOutputFormat.Tiff => "TIFF 高保真",
            _ => string.Empty
        };
    }

    private void UpdateImageConversionControls()
    {
        if (ImageQualitySlider is null || ImageScaleSlider is null || ImageFormatComboBox is null)
        {
            return;
        }

        var isJpeg = GetSelectedImageFormat() == ImageOutputFormat.Jpeg;
        ImageQualitySlider.IsEnabled = isJpeg;
        ImageQualityValueText.Text = isJpeg ? $"{Math.Round(ImageQualitySlider.Value)}%" : "仅 JPEG";
        ImageScaleValueText.Text = $"{Math.Round(ImageScaleSlider.Value)}%";
        UpdateImagePreviews();
    }

    private ImageOutputFormat GetSelectedImageFormat()
    {
        return (ImageFormatComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Jpeg" => ImageOutputFormat.Jpeg,
            "Bmp" => ImageOutputFormat.Bmp,
            "Tiff" => ImageOutputFormat.Tiff,
            _ => ImageOutputFormat.Png
        };
    }

    private async void StartImageConversion_Click(object sender, RoutedEventArgs e)
    {
        if (_imageConversionFiles.Count == 0)
        {
            ShowToast("请先添加至少一张图片");
            return;
        }

        if (string.IsNullOrWhiteSpace(_imageOutputDirectory))
        {
            ShowToast("请为本次图片任务选择输出位置");
            return;
        }

        StartImageConversionButton.IsEnabled = false;
        ImageConversionStatusText.Text = "正在准备本地转换…";
        try
        {
            var progress = new Progress<string>(message => ImageConversionStatusText.Text = message);
            var result = await ImageConversionService.ConvertAsync(
                _imageConversionFiles.Select(item => item.FilePath).ToArray(),
                GetSelectedImageFormat(),
                (int)Math.Round(ImageScaleSlider.Value),
                (int)Math.Round(ImageQualitySlider.Value),
                _imageOutputDirectory,
                progress);
            ImageConversionStatusText.Text = result.Failed == 0
                ? string.Empty
                : $"已完成 {result.Succeeded} 张，失败 {result.Failed} 张。{result.Errors.FirstOrDefault()}";
            ShowToast(result.Failed == 0 ? "图片处理完成" : "部分图片处理失败");
        }
        catch (Exception exception)
        {
            ImageConversionStatusText.Text = $"处理失败：{exception.Message}";
            ShowToast("图片处理失败");
        }
        finally
        {
            StartImageConversionButton.IsEnabled = true;
        }
    }

    private void FutureTools_Click(object sender, RoutedEventArgs e)
    {
        ShowToast("更多 X-Tool 子工具正在准备中");
    }

    private void OpenShortcutSettings()
    {
        var window = new ShortcutSettingsWindow(
            _screenshotShortcut,
            _fullScreenShortcut,
            _clipboardShortcut,
            _voiceInputShortcut)
        {
            Owner = this
        };
        window.ApplyShortcutRequested = ApplyShortcutRequested;
        window.ShowDialog();
    }

    private void OpenShortcutSettings_Click(object sender, MouseButtonEventArgs e) => OpenShortcutSettings();

    /// <summary>快捷键弹框回调：完成合法性检查、冲突探测、热键重注册与偏好保存。</summary>
    private string? ApplyShortcutRequested(string target, GlobalShortcut candidate)
    {
        if (!candidate.IsRightAlt && !candidate.IsRightCtrl && !candidate.IsSupportedGlobalCombination)
        {
            return "请使用 Ctrl、Shift 或 Alt 加一个非修饰键；Windows 徽标键组合不允许设置。";
        }

        if (candidate.IsKnownWindowsReserved)
        {
            return "这是 Windows 保留快捷键，不能设置为 X-Tool 全局快捷键。";
        }

        if (candidate.IsRightAlt && target != "Voice")
        {
            return "右 Alt 仅可作为本地语音输入快捷键。";
        }

        if (candidate.IsRightCtrl && target != "FullScreen")
        {
            return "右 Ctrl 单键仅可作为全屏截图快捷键。";
        }

        if (!TryValidateShortcut(target, candidate, out var reason))
        {
            return reason;
        }

        if (!ApplyShortcut(target, candidate))
        {
            return "新快捷键注册失败，已恢复原快捷键。";
        }
        HomeScreenshotShortcutText.Text = _screenshotShortcut.DisplayText;
        UpdateSettingsShortcutSummary();
        return null;
    }

    private bool TryValidateShortcut(string target, GlobalShortcut candidate, out string reason)
    {
        reason = string.Empty;
        var currentShortcut = target switch
        {
            "Screenshot" => _screenshotShortcut,
            "FullScreen" => _fullScreenShortcut,
            "Clipboard" => _clipboardShortcut,
            _ => _voiceInputShortcut
        };
        if (candidate == currentShortcut)
        {
            return true;
        }

        var otherShortcuts = target switch
        {
            "Screenshot" => new[] { _fullScreenShortcut, _clipboardShortcut, _voiceInputShortcut },
            "FullScreen" => new[] { _screenshotShortcut, _clipboardShortcut, _voiceInputShortcut },
            "Clipboard" => new[] { _screenshotShortcut, _fullScreenShortcut, _voiceInputShortcut },
            _ => new[] { _screenshotShortcut, _fullScreenShortcut, _clipboardShortcut }
        };
        if (otherShortcuts.Contains(candidate))
        {
            reason = "该快捷键已被 X-Tool 的其他功能使用。";
            return false;
        }

        if (candidate.IsRightAlt)
        {
            reason = "右 Alt 已设为语音快捷键。该键通过键盘监听实现，Windows 无法枚举其他软件的低级键盘钩子。";
            return true;
        }
        if (candidate.IsRightCtrl)
        {
            reason = "右 Ctrl 使用键盘监听实现；与其他按键组合时不会触发全屏截图。";
            return true;
        }

        if (_windowSource is null)
        {
            return true;
        }

        if (!NativeMethods.RegisterHotKey(
                _windowSource.Handle,
                NativeMethods.ShortcutProbeHotKeyId,
                candidate.NativeModifiers,
                candidate.VirtualKey))
        {
            reason = "该组合已被系统、Windows 保留快捷键或其他程序注册，无法使用。";
            return false;
        }

        NativeMethods.UnregisterHotKey(_windowSource.Handle, NativeMethods.ShortcutProbeHotKeyId);
        return true;
    }

    private bool ApplyShortcut(string target, GlobalShortcut candidate)
    {
        var previous = target switch
        {
            "Screenshot" => _screenshotShortcut,
            "FullScreen" => _fullScreenShortcut,
            "Clipboard" => _clipboardShortcut,
            _ => _voiceInputShortcut
        };
        UnregisterShortcut(target);
        SetShortcut(target, candidate);
        var registered = RegisterShortcut(target);
        if (!registered && _windowSource is not null)
        {
            SetShortcut(target, previous);
            _ = RegisterShortcut(target);
            ShowToast("新快捷键注册失败，已恢复原快捷键");
            return false;
        }

        _preferences.ScreenshotShortcut = _screenshotShortcut.ToPreferenceValue();
        _preferences.FullScreenShortcut = _fullScreenShortcut.ToPreferenceValue();
        _preferences.ClipboardShortcut = _clipboardShortcut.ToPreferenceValue();
        _preferences.VoiceInputShortcut = _voiceInputShortcut.ToPreferenceValue();
        _preferences.Save();
        return true;
    }

    private void SetShortcut(string target, GlobalShortcut shortcut)
    {
        if (target == "Screenshot") _screenshotShortcut = shortcut;
        else if (target == "FullScreen") _fullScreenShortcut = shortcut;
        else if (target == "Clipboard") _clipboardShortcut = shortcut;
        else _voiceInputShortcut = shortcut;
    }

    private void UnregisterShortcut(string target)
    {
        if (_windowSource is null)
        {
            return;
        }

        if (target == "Screenshot")
        {
            _screenshotHotKeySuppressor?.Dispose();
            _screenshotHotKeySuppressor = null;
            if (_hotKeyRegistered)
            {
                NativeMethods.UnregisterHotKey(_windowSource.Handle, NativeMethods.HotKeyId);
                _hotKeyRegistered = false;
            }
        }
        else if (target == "FullScreen")
        {
            UnregisterFullScreenHotKey();
        }
        else if (target == "Clipboard" && _clipboardHotKeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_windowSource.Handle, NativeMethods.ClipboardHotKeyId);
            _clipboardHotKeyRegistered = false;
        }
        else if (target == "Voice" && _voiceInputHotKeyRegistered)
        {
            _voiceInputHotKeyMonitor?.Dispose();
            _voiceInputHotKeyMonitor = null;
            if (!_voiceInputShortcut.IsRightAlt)
            {
                NativeMethods.UnregisterHotKey(_windowSource.Handle, NativeMethods.VoiceHotKeyId);
            }

            _voiceInputHotKeyRegistered = false;
        }
    }

    private bool RegisterShortcut(string target)
    {
        if (_windowSource is null)
        {
            return true;
        }

        if (target == "Screenshot")
        {
            _hotKeyRegistered = RegisterStandardShortcut(_windowSource.Handle, NativeMethods.HotKeyId, _screenshotShortcut);
            InstallScreenshotHotKeySuppressor();
            // RegisterHotKey 可能被 Windows Shell 或其他程序占用；低级钩子仍可独占拦截截图键，
            // 这样 Apps 等单键快捷键不会把菜单命令继续传给浏览器或播放器。
            return _hotKeyRegistered || _screenshotHotKeySuppressor?.IsInstalled == true;
        }
        if (target == "Clipboard")
        {
            return _clipboardHotKeyRegistered = RegisterStandardShortcut(_windowSource.Handle, NativeMethods.ClipboardHotKeyId, _clipboardShortcut);
        }
        if (target == "FullScreen")
        {
            RegisterFullScreenHotKey(_windowSource.Handle);
            return _fullScreenHotKeyRegistered;
        }

        RegisterVoiceInputHotKey(_windowSource.Handle);
        return _voiceInputHotKeyRegistered;
    }

    private void UpdateSettingsShortcutSummary()
    {
        SettingsShortcutSummaryText.Text =
            $"截图 {_screenshotShortcut.DisplayText} · 全屏 {_fullScreenShortcut.DisplayText} · 剪贴板 {_clipboardShortcut.DisplayText} · 语音 {_voiceInputShortcut.DisplayText}";
    }

    private void ShowToast(string message)
    {
        _toastAnimationGeneration++;
        ToastText.Text = message;
        ToastBorder.Visibility = Visibility.Visible;
        AppMotion.AnimateToastIn(ToastBorder);

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        if (ToastBorder.Visibility != Visibility.Visible)
        {
            return;
        }

        var generation = ++_toastAnimationGeneration;
        AppMotion.AnimateToastOut(ToastBorder, () =>
        {
            if (generation != _toastAnimationGeneration)
            {
                return;
            }

            ToastBorder.Visibility = Visibility.Collapsed;
            ToastBorder.BeginAnimation(OpacityProperty, null);
            ToastBorder.Opacity = 1;
            ToastBorder.RenderTransform = new TranslateTransform();
        });
    }

#if DEBUG
    private async void CapturePreviewWhenRequested(object sender, RoutedEventArgs e)
    {
        const string argumentPrefix = "--capture-preview=";
        var captureArgument = Environment.GetCommandLineArgs()
            .FirstOrDefault(argument => argument.StartsWith(argumentPrefix, StringComparison.OrdinalIgnoreCase));

        if (captureArgument is null)
        {
            return;
        }

        var outputPath = captureArgument[argumentPrefix.Length..].Trim('"');
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Round(RootVisual.ActualWidth)),
            Math.Max(1, (int)Math.Round(RootVisual.ActualHeight)),
            96,
            96,
            PixelFormats.Pbgra32);

        bitmap.Render(RootVisual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        await using var stream = File.Create(outputPath);
        encoder.Save(stream);
        if (Application.Current is App app)
        {
            app.ExitApplication();
        }
        else
        {
            Close();
        }
    }
#endif
}

/// <summary>图片处理队列的展示项，缩略图只在用户主动选择文件后生成。</summary>
internal sealed class ImageConversionQueueItem
{
    private ImageConversionQueueItem(string filePath, BitmapImage? thumbnail, string fileSize, int pixelWidth, int pixelHeight)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Extension = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        FileSize = fileSize;
        Thumbnail = thumbnail;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    public string FilePath { get; }
    public string FileName { get; }
    public string Extension { get; }
    public string FileSize { get; }
    public BitmapImage? Thumbnail { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public string DimensionDisplay => PixelWidth > 0 && PixelHeight > 0 ? $"{PixelWidth} × {PixelHeight}" : "尺寸未知";

    public static ImageConversionQueueItem Create(string filePath)
    {
        BitmapImage? thumbnail = null;
        var pixelWidth = 0;
        var pixelHeight = 0;
        try
        {
            using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            pixelWidth = frame.PixelWidth;
            pixelHeight = frame.PixelHeight;
        }
        catch
        {
            // 尺寸读取失败时仍允许在后续转换阶段由系统解码器给出真实错误。
        }

        try
        {
            thumbnail = new BitmapImage();
            thumbnail.BeginInit();
            thumbnail.CacheOption = BitmapCacheOption.OnLoad;
            thumbnail.DecodePixelWidth = 96;
            thumbnail.UriSource = new Uri(filePath, UriKind.Absolute);
            thumbnail.EndInit();
            thumbnail.Freeze();
        }
        catch
        {
            // 单个文件缩略图解码失败不影响转换任务本身。
        }

        var length = new FileInfo(filePath).Length;
        return new ImageConversionQueueItem(filePath, thumbnail, FormatFileSize(length), pixelWidth, pixelHeight);
    }

    private static string FormatFileSize(long length) => length switch
    {
        < 1024 => $"{length} B",
        < 1024 * 1024 => $"{length / 1024d:0.0} KB",
        _ => $"{length / 1024d / 1024d:0.00} MB"
    };
}
