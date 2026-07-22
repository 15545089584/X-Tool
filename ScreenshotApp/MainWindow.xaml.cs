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
using ScreenshotApp.Translation;
using ScreenshotApp.VoiceInput;

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
    private bool _clipboardHotKeyRegistered;
    private bool _voiceInputHotKeyRegistered;
    private RightAltHotKeyMonitor? _voiceInputHotKeyMonitor;
    private bool _clipboardListenerRegistered;
    private bool _captureInProgress;
    private bool _historyRefreshInProgress;
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
    private string _voiceInputTranslatedText = string.Empty;
    private GlobalShortcut _screenshotShortcut;
    private GlobalShortcut _clipboardShortcut;
    private GlobalShortcut _voiceInputShortcut;
    private string? _shortcutBeingEdited;

    public MainWindow()
    {
        InitializeComponent();
        _historyStore = new ScreenshotHistoryStore(_preferences);
        _ = GlobalShortcut.TryParse(_preferences.ScreenshotShortcut, GlobalShortcut.ScreenshotDefault, out _screenshotShortcut);
        _ = GlobalShortcut.TryParse(_preferences.ClipboardShortcut, GlobalShortcut.ClipboardDefault, out _clipboardShortcut);
        _ = GlobalShortcut.TryParse(_preferences.VoiceInputShortcut, GlobalShortcut.VoiceDefault, out _voiceInputShortcut);
        StickerTopmostCheckBox.IsChecked = _preferences.StickerTopmost;
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
            ToastBorder.Visibility = Visibility.Collapsed;
        };
        UpdateShortcutButtons();

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
        HomeView.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
        ScreenWorkbenchView.Visibility = page == "ScreenWorkbench" ? Visibility.Visible : Visibility.Collapsed;
        ConverterWorkbenchView.Visibility = page == "ConverterWorkbench" ? Visibility.Visible : Visibility.Collapsed;
        ImageConverterView.Visibility = page == "ImageConverter" ? Visibility.Visible : Visibility.Collapsed;
        AudioConverterView.Visibility = page == "AudioConverter" ? Visibility.Visible : Visibility.Collapsed;
          VideoConverterView.Visibility = page == "VideoConverter" ? Visibility.Visible : Visibility.Collapsed;
          PdfConverterView.Visibility = page == "PdfConverter" ? Visibility.Visible : Visibility.Collapsed;
          EncodingConverterView.Visibility = page == "EncodingConverter" ? Visibility.Visible : Visibility.Collapsed;
        FileWorkbenchView.Visibility = page == "FileWorkbench" ? Visibility.Visible : Visibility.Collapsed;
        SystemToolsView.Visibility = page == "SystemTools" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "ImageConverter")
        {
            Dispatcher.BeginInvoke(new Action(() => NormalizeImageConverterLabels(ImageConverterView)), DispatcherPriority.Loaded);
        }
        HistoryView.Visibility = page == "History" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsView.Visibility = page == "Shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        if (page is "History" or "ScreenWorkbench")
        {
            _ = RefreshHistoryAsync();
        }
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
        _clipboardHotKeyRegistered = RegisterStandardShortcut(handle, NativeMethods.ClipboardHotKeyId, _clipboardShortcut);
        RegisterVoiceInputHotKey(handle);
        _clipboardListenerRegistered = NativeMethods.AddClipboardFormatListener(handle);

        if (!_hotKeyRegistered)
        {
            Dispatcher.BeginInvoke(() => ShowToast($"{_screenshotShortcut.DisplayText} 已被其他程序占用"), DispatcherPriority.Loaded);
        }
        if (!_clipboardHotKeyRegistered)
        {
            Dispatcher.BeginInvoke(() => ShowToast($"{_clipboardShortcut.DisplayText} 已被其他程序占用"), DispatcherPriority.Loaded);
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
            handled = true;
            _ = StartRegionCaptureAsync();
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

        try
        {
            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            NativeMethods.DwmFlush();
            await Task.Delay(90);

            var frame = await _captureBackend.CaptureCurrentMonitorAsync();
            var overlay = new SelectionOverlayWindow(frame);
            overlay.HistoryTextCreated += Overlay_HistoryTextCreated;
            var confirmed = overlay.ShowDialog() == true;

            if (confirmed &&
                overlay.IsScreenRecordingRequested &&
                overlay.SelectedScreenBounds is Int32Rect recordingRegion)
            {
                await CaptureScreenRecordingAsync(
                    recordingRegion,
                    overlay.RecordSystemAudio,
                    overlay.RecordMicrophone);
                return;
            }

            if (confirmed &&
                overlay.IsScrollCaptureRequested &&
                overlay.SelectedScreenBounds is Int32Rect scrollRegion)
            {
                await CaptureScrollRegionAsync(scrollRegion);
                return;
            }

            if (confirmed && overlay.SelectedBitmap is not null)
            {
                await SetClipboardImageWithRetryAsync(overlay.SelectedBitmap);
                var savedPath = await TrySaveCaptureAsync(overlay.SelectedBitmap, false);
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
            if (wasVisible)
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

        try
        {
            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            NativeMethods.DwmFlush();
            await Task.Delay(220);

            var frame = await _scrollCaptureBackend.CaptureCurrentMonitorAsync();
            var overlay = new SelectionOverlayWindow(frame, SelectionPurpose.ScrollCaptureRegion);
            var confirmed = overlay.ShowDialog() == true;
            if (!confirmed || overlay.SelectedScreenBounds is not Int32Rect screenRegion)
            {
                return;
            }

            await CaptureScrollRegionAsync(screenRegion);
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
            if (wasVisible)
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
        await SetClipboardImageWithRetryAsync(result.Bitmap);
        var savedPath = await TrySaveCaptureAsync(result.Bitmap, true);
        if (savedPath is not null)
        {
            ShowToast($"长截图已保存 · {result.FrameCount} 帧 · {result.StopReason}");
        }
        else
        {
            ShowToast("长截图已复制，但无法保存到 E 盘");
        }
    }

    private async Task CaptureScreenRecordingAsync(Int32Rect screenRegion, bool recordSystemAudio, bool recordMicrophone)
    {
        var regionWindow = new ScreenRecordingRegionWindow(screenRegion);
        regionWindow.Show();
        try
        {
            await ShowRecordingCountdownAsync(screenRegion);
            var controlWindow = new ScreenRecordingControlWindow(screenRegion);
            controlWindow.Show();
            try
            {
                var result = await _screenRecordingService.RecordAsync(
                    new ScreenRecordingOptions(screenRegion, recordSystemAudio, recordMicrophone),
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
                await RefreshHistoryAsync();
            }
            finally
            {
                controlWindow.Close();
            }
        }
        finally
        {
            regionWindow.Close();
        }
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
                    return;
                }

                _lastExternalClipboardSignature = signature;
                await _historyStore.SaveClipboardTextAsync(content);
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
                    return;
                }

                _lastExternalClipboardSignature = signature;
                await _historyStore.SaveClipboardImageAsync(image);
            }
            else
            {
                return;
            }

            await RefreshHistoryAsync();
        }
        catch
        {
            // 剪贴板可能被其他程序短暂占用，下一次复制时会自然重试。
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
            await RefreshHistoryAsync();
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
            ShowToast($"无法打开截图：{exception.Message}");
        }
    }

    private void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        NavigateToPage("History");
    }

    private Task ShowClipboardPickerAsync()
    {
        if (_clipboardPicker is not null)
        {
            _clipboardPicker.Close();
            return Task.CompletedTask;
        }

        _clipboardPasteTarget = NativeMethods.GetForegroundWindow();
        var choices = _allHistoryItems
            .Where(item => item.Kind != HistoryEntryKind.ScreenRecording &&
                           (item.IsTextRecord || Path.GetExtension(item.FilePath).Equals(".png", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (choices.Length == 0)
        {
            ShowToast("剪贴板还没有可粘贴的内容");
            return Task.CompletedTask;
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
        return Task.CompletedTask;
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

    private static bool RegisterStandardShortcut(IntPtr handle, int hotKeyId, GlobalShortcut shortcut) =>
        shortcut.IsSupportedGlobalCombination && NativeMethods.RegisterHotKey(
            handle,
            hotKeyId,
            shortcut.NativeModifiers,
            shortcut.VirtualKey);

    private void VoiceInputHotKeyMonitor_Pressed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => _ = ToggleVoiceInputAsync());
    }

    private void VoiceInputHotKeyMonitor_EscapePressed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(CancelVoiceInput);
    }

    private void VoiceInputHotKeyMonitor_TranslationPressed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => _ = StartVoiceTranslationPreviewAsync());
    }

    private void CancelVoiceInput()
    {
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

    private async Task ToggleVoiceInputAsync()
    {
        if (!_preferences.VoiceInputEnabled)
        {
            ShowToast("本地语音输入已在设置中关闭");
            return;
        }

        if (_voiceInputService.IsRecording)
        {
            await FinishVoiceInputAsync();
            return;
        }

        try
        {
            _voiceInputPasteTarget = NativeMethods.GetForegroundWindow();
            _voiceInputCancellation?.Cancel();
            _voiceInputCancellation?.Dispose();
            _voiceInputCancellation = new CancellationTokenSource();
            _voiceInputCommittedText = string.Empty;
            _voiceInputTranslatedText = string.Empty;
            _voiceTranslationPreviewActive = false;
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

    private async Task StartVoiceTranslationPreviewAsync()
    {
        if (!_voiceInputService.IsRecording)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_voiceInputCommittedText))
        {
            ShowToast("请先说出需要翻译的中文内容");
            return;
        }

        if (!TranslationEngineProvider.ChineseToEnglish.IsReady)
        {
            ShowToast("中译英离线模型未安装，暂不能启用英文输入预览");
            return;
        }

        _voiceTranslationPreviewActive = true;
        await RefreshVoiceTranslationPreviewAsync(_voiceInputCommittedText);
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
                !_voiceInputService.IsRecording || !string.Equals(sourceText, _voiceInputCommittedText, StringComparison.Ordinal))
            {
                return;
            }

            _voiceInputTranslatedText = result.TranslatedText.Trim();
            if (string.IsNullOrWhiteSpace(_voiceInputTranslatedText))
            {
                ShowToast("未能生成英文翻译，请继续说话后重试");
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

    private async Task FinishVoiceInputAsync()
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
                await RefreshVoiceTranslationPreviewAsync(text);
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
            CloseVoiceInputOverlay();
            cancellation.Dispose();
            if (ReferenceEquals(_voiceInputCancellation, cancellation))
            {
                _voiceInputCancellation = null;
            }
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
            case "Shortcuts":
                ShortcutsNav.IsChecked = true;
                break;
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

    private void ShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string target)
        {
            return;
        }

        _shortcutBeingEdited = target;
        button.Content = "请按下快捷键…";
        ShortcutCaptureStatusText.Text = "正在监听。按 Esc 取消；右 Alt 仅可用于本地语音输入。";
        button.Focus();
    }

    private void ShortcutButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_shortcutBeingEdited is null)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key == Key.Escape)
        {
            _shortcutBeingEdited = null;
            UpdateShortcutButtons();
            ShortcutCaptureStatusText.Text = "已取消修改。";
            return;
        }

        GlobalShortcut candidate;
        if (key == Key.RightAlt && _shortcutBeingEdited == "Voice")
        {
            candidate = GlobalShortcut.VoiceDefault;
        }
        else
        {
            candidate = GlobalShortcut.FromKey(key, Keyboard.Modifiers);
        }

        if (!candidate.IsRightAlt && !candidate.IsSupportedGlobalCombination)
        {
            ShortcutCaptureStatusText.Text = "请使用 Ctrl、Shift 或 Alt 加一个非修饰键；Windows 徽标键组合不允许设置。";
            return;
        }

        if (candidate.IsKnownWindowsReserved)
        {
            ShortcutCaptureStatusText.Text = "这是 Windows 保留快捷键，不能设置为 X-Tool 全局快捷键。";
            return;
        }

        if (candidate.IsRightAlt && _shortcutBeingEdited != "Voice")
        {
            ShortcutCaptureStatusText.Text = "右 Alt 仅可作为本地语音输入快捷键。";
            return;
        }

        if (!TryValidateShortcut(_shortcutBeingEdited, candidate, out var reason))
        {
            ShortcutCaptureStatusText.Text = reason;
            return;
        }

        ApplyShortcut(_shortcutBeingEdited, candidate);
        _shortcutBeingEdited = null;
        UpdateShortcutButtons();
        ShortcutCaptureStatusText.Text = $"已设为 {candidate.DisplayText}，未发现应用内或已注册的系统级冲突。";
    }

    private bool TryValidateShortcut(string target, GlobalShortcut candidate, out string reason)
    {
        reason = string.Empty;
        var currentShortcut = target switch
        {
            "Screenshot" => _screenshotShortcut,
            "Clipboard" => _clipboardShortcut,
            _ => _voiceInputShortcut
        };
        if (candidate == currentShortcut)
        {
            return true;
        }

        var otherShortcuts = target switch
        {
            "Screenshot" => new[] { _clipboardShortcut, _voiceInputShortcut },
            "Clipboard" => new[] { _screenshotShortcut, _voiceInputShortcut },
            _ => new[] { _screenshotShortcut, _clipboardShortcut }
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

    private void ApplyShortcut(string target, GlobalShortcut candidate)
    {
        var previous = target switch
        {
            "Screenshot" => _screenshotShortcut,
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
            return;
        }

        _preferences.ScreenshotShortcut = _screenshotShortcut.ToPreferenceValue();
        _preferences.ClipboardShortcut = _clipboardShortcut.ToPreferenceValue();
        _preferences.VoiceInputShortcut = _voiceInputShortcut.ToPreferenceValue();
        _preferences.Save();
    }

    private void SetShortcut(string target, GlobalShortcut shortcut)
    {
        if (target == "Screenshot") _screenshotShortcut = shortcut;
        else if (target == "Clipboard") _clipboardShortcut = shortcut;
        else _voiceInputShortcut = shortcut;
    }

    private void UnregisterShortcut(string target)
    {
        if (_windowSource is null)
        {
            return;
        }

        if (target == "Screenshot" && _hotKeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_windowSource.Handle, NativeMethods.HotKeyId);
            _hotKeyRegistered = false;
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
            return _hotKeyRegistered = RegisterStandardShortcut(_windowSource.Handle, NativeMethods.HotKeyId, _screenshotShortcut);
        }
        if (target == "Clipboard")
        {
            return _clipboardHotKeyRegistered = RegisterStandardShortcut(_windowSource.Handle, NativeMethods.ClipboardHotKeyId, _clipboardShortcut);
        }

        RegisterVoiceInputHotKey(_windowSource.Handle);
        return _voiceInputHotKeyRegistered;
    }

    private void UpdateShortcutButtons()
    {
        if (ScreenshotShortcutButton is null)
        {
            return;
        }

        ScreenshotShortcutButton.Content = _screenshotShortcut.DisplayText;
        ClipboardShortcutButton.Content = _clipboardShortcut.DisplayText;
        VoiceShortcutButton.Content = _voiceInputShortcut.DisplayText;
        HomeScreenshotShortcutText.Text = _screenshotShortcut.DisplayText;
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
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
    private ImageConversionQueueItem(string filePath, BitmapImage? thumbnail, string fileSize)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Extension = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        FileSize = fileSize;
        Thumbnail = thumbnail;
    }

    public string FilePath { get; }
    public string FileName { get; }
    public string Extension { get; }
    public string FileSize { get; }
    public BitmapImage? Thumbnail { get; }

    public static ImageConversionQueueItem Create(string filePath)
    {
        BitmapImage? thumbnail = null;
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
        return new ImageConversionQueueItem(filePath, thumbnail, FormatFileSize(length));
    }

    private static string FormatFileSize(long length) => length switch
    {
        < 1024 => $"{length} B",
        < 1024 * 1024 => $"{length / 1024d:0.0} KB",
        _ => $"{length / 1024d / 1024d:0.00} MB"
    };
}
