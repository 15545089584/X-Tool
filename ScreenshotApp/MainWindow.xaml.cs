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
    private IReadOnlyList<ScreenshotHistoryItem> _allHistoryItems = Array.Empty<ScreenshotHistoryItem>();
    private HistoryEntryKind? _historyFilter;
    private HwndSource? _windowSource;
    private bool _hotKeyRegistered;
    private bool _clipboardHotKeyRegistered;
    private bool _clipboardListenerRegistered;
    private bool _captureInProgress;
    private bool _historyRefreshInProgress;
    private bool _suppressClipboardCapture;
    private string? _lastExternalClipboardSignature;
    private ClipboardPickerWindow? _clipboardPicker;
    private IntPtr _clipboardPasteTarget;

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
        ImageFileList.ItemsSource = _imageConversionFiles;
        ConverterOutputFolderText.Text = _preferences.ConverterDirectory;
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

                var columns = new Grid { Margin = new Thickness(60, 0, 0, 0) };
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
                columns.Children.Add(new TextBlock { Text = "文件名", VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(120, 144, 166)) });
                var format = new TextBlock { Text = "格式", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(120, 144, 166)) };
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

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(Key.A);
        _hotKeyRegistered = NativeMethods.RegisterHotKey(
            handle,
            NativeMethods.HotKeyId,
            NativeMethods.ModControl | NativeMethods.ModShift,
            virtualKey);
        _clipboardHotKeyRegistered = NativeMethods.RegisterHotKey(
            handle,
            NativeMethods.ClipboardHotKeyId,
            NativeMethods.ModControl | NativeMethods.ModShift,
            (uint)KeyInterop.VirtualKeyFromKey(Key.V));
        _clipboardListenerRegistered = NativeMethods.AddClipboardFormatListener(handle);

        if (!_hotKeyRegistered)
        {
            Dispatcher.BeginInvoke(() => ShowToast("Ctrl + Shift + A 已被其他程序占用"), DispatcherPriority.Loaded);
        }
        if (!_clipboardHotKeyRegistered)
        {
            Dispatcher.BeginInvoke(() => ShowToast("Ctrl + Shift + V 已被其他程序占用"), DispatcherPriority.Loaded);
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
        if (_clipboardListenerRegistered)
        {
            NativeMethods.RemoveClipboardFormatListener(_windowSource.Handle);
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
        "转换器" => _preferences.ConverterDirectory,
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
            case "转换器":
                _preferences.ConverterDirectory = fullPath;
                ConverterOutputFolderText.Text = fullPath;
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
        ConverterStoragePathText.Text = _preferences.ConverterDirectory;
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
                // 剪贴板浮窗为非激活窗口时，外部输入框会持续保持焦点，直接发送粘贴即可。
                var activated = NativeMethods.IsWindowForeground(_clipboardPasteTarget);
                var focusRestored = false;
                for (var attempt = 0; attempt < 3 && !activated; attempt++)
                {
                    activated = NativeMethods.RestoreAndActivateWindow(_clipboardPasteTarget);
                    focusRestored |= activated;
                    if (!activated)
                    {
                        await Task.Delay(40);
                    }
                }

                if (!activated)
                {
                    ShowToast("无法恢复原输入窗口，内容已复制到系统剪贴板");
                    return;
                }

                if (focusRestored)
                {
                    // 仅在刚恢复外部窗口时留出一帧时间让其内部控件重新接收输入。
                    await Task.Delay(35);
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

    private void OpenImageConverter_Click(object sender, RoutedEventArgs e)
    {
        ConverterWorkbenchNav.IsChecked = true;
        NavigateToPage("ImageConverter");
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
            SelectedPath = _preferences.ConverterDirectory
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        _preferences.ConverterDirectory = Path.GetFullPath(dialog.SelectedPath);
        _preferences.Save();
        ConverterOutputFolderText.Text = _preferences.ConverterDirectory;
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
                _preferences.ConverterDirectory,
                progress);
            ImageConversionStatusText.Text = result.Failed == 0
                ? $"已完成 {result.Succeeded} 张图片，可在输出目录中查看。"
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
