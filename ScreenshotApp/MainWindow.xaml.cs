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
using Forms = System.Windows.Forms;
using ScreenshotApp.Capture;
using ScreenshotApp.History;
using ScreenshotApp.Recording;
using ScreenshotApp.Settings;

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
    private IReadOnlyList<ScreenshotHistoryItem> _allHistoryItems = Array.Empty<ScreenshotHistoryItem>();
    private HistoryEntryKind? _historyFilter;
    private HwndSource? _windowSource;
    private bool _hotKeyRegistered;
    private bool _captureInProgress;
    private bool _historyRefreshInProgress;

    public MainWindow()
    {
        InitializeComponent();
        _historyStore = new ScreenshotHistoryStore(_preferences);
        StickerTopmostCheckBox.IsChecked = _preferences.StickerTopmost;
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

        _toastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.2)
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };

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
        await RefreshHistoryAsync();
    }

    private void NavButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton radioButton)
        {
            return;
        }

        var page = radioButton.Tag?.ToString() ?? "Home";
        HomeView.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
        HistoryView.Visibility = page == "History" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsView.Visibility = page == "Shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        if (page == "History")
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

        ShowToast($"{action}将在后续阶段接入");
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _ = NativeWindowAnimation.EnableSystemTransitions(handle);
        _ = WindowBackdrop.TryApply(handle);
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(Key.A);
        _hotKeyRegistered = NativeMethods.RegisterHotKey(
            handle,
            NativeMethods.HotKeyId,
            NativeMethods.ModControl | NativeMethods.ModShift,
            virtualKey);

        if (!_hotKeyRegistered)
        {
            Dispatcher.BeginInvoke(() => ShowToast("Ctrl + Shift + A 已被其他程序占用"), DispatcherPriority.Loaded);
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

        _windowSource.RemoveHook(WindowMessageHook);
        _windowSource = null;
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

        if (message == NativeMethods.WmHotKey && wParam.ToInt32() == NativeMethods.HotKeyId)
        {
            handled = true;
            _ = StartRegionCaptureAsync();
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

    private static async Task SetClipboardImageWithRetryAsync(BitmapSource bitmap)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetImage(bitmap);
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
            ShowToast($"历史记录加载失败：{exception.Message}");
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
            (ScreenRecordingHistoryFilterButton, HistoryEntryKind.ScreenRecording)
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
        }
    }

    private void UpdateStorageLocationText()
    {
        ScreenshotStoragePathText.Text = _preferences.ScreenshotDirectory;
        LongScreenshotStoragePathText.Text = _preferences.LongScreenshotDirectory;
        TextExtractionStoragePathText.Text = _preferences.TextExtractionDirectory;
        TranslationStoragePathText.Text = _preferences.TranslationDirectory;
        RecordingStoragePathText.Text = _preferences.RecordingDirectory;
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
        var historyNav = FindHistoryNavigationButton();
        if (historyNav is not null)
        {
            historyNav.IsChecked = true;
        }
    }

    private RadioButton? FindHistoryNavigationButton()
    {
        return FindVisualChildren<RadioButton>(this)
            .FirstOrDefault(button => button.Tag?.ToString() == "History");
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void ShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        ShowToast("快捷键编辑将在全局热键模块接入后启用");
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
