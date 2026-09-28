using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using ScreenshotApp.Archives;
using ScreenshotApp.Collaboration;
using ScreenshotApp.Capture;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.DesktopPet;

public partial class DesktopPetWindow : Window
{
    private const double EdgeMargin = 12;
    private readonly DesktopPetAnimationCatalog _catalog;
    private readonly DesktopPetAnimationPlayer _player;
    private readonly CollaborationService _collaborationService = CollaborationService.Instance;
    private readonly PetSessionShelfService _shelfService;
    private readonly PetAlarmService _alarmService;
    private readonly bool _ownsShelfService;
    private readonly bool _ownsAlarmService;
    private readonly HashSet<string> _petTransferIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CollaborationTransferProgress> _activeTransferProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransferSpeedSample> _transferSpeedSamples = new(StringComparer.Ordinal);
    private CancellationTokenSource? _resizeDecodeCancellation;
    private CancellationTokenSource? _completionResetCancellation;
    private CancellationTokenSource? _shelfBubbleResetCancellation;
    private CancellationTokenSource? _alarmBubbleResetCancellation;
    private CancellationTokenSource? _archiveOperationCancellation;
    private PetActionWheelWindow? _actionWheel;
    private PetCommandWheelWindow? _commandWheel;
    private PetShelfPanelWindow? _shelfPanel;
    private PetAlarmPanelWindow? _alarmPanel;
    private PetTransferBubbleWindow? _transferBubble;
    private PetTransferBubbleWindow? _phoneBubble;
    private readonly System.Windows.Threading.DispatcherTimer _phoneBubbleTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    internal void ShowPhoneNotification(string title, string detail, Action onClick, string? avatar = null, ScreenshotApp.Collaboration.PhoneVerificationCode? verificationCode = null)
    {
        if (_closed || !IsVisible) return;
        if (_phoneBubble is null)
        {
            _phoneBubble = new PetTransferBubbleWindow();
            _phoneBubbleTimer.Tick += (_, _) => DismissPhoneNotification();
        }
        _phoneBubble.ShowPhoneMessage(title, detail, () => { DismissPhoneNotification(); onClick(); }, avatar, verificationCode: verificationCode);
        _phoneBubbleTimer.Interval = TimeSpan.FromSeconds(verificationCode is null ? 10 : 20);
        UpdateTransferBubblePosition();
        _phoneBubbleTimer.Stop(); _phoneBubbleTimer.Start();
    }
    internal void ShowMailNotification(string title, string detail, Action open, Action? markRead, string? logo = null)
    {
        if (_closed || !IsVisible) return;
        if (_phoneBubble is null)
        {
            _phoneBubble = new PetTransferBubbleWindow();
            _phoneBubbleTimer.Tick += (_, _) => DismissPhoneNotification();
        }
        _phoneBubble.ShowMailMessage(title, detail,
            () => { DismissPhoneNotification(); open(); },
            markRead is null ? null : () => { DismissPhoneNotification(); markRead(); }, logo);
        _phoneBubbleTimer.Interval = TimeSpan.FromSeconds(10);
        UpdateTransferBubblePosition();
        _phoneBubbleTimer.Stop(); _phoneBubbleTimer.Start();
    }
    internal void DismissPhoneNotification() { _phoneBubbleTimer.Stop(); _phoneBubble?.ResetVerificationCode(); _phoneBubble?.Hide(); }

    private CollaborationTransferProgress? _lastCompletedProgress;
    private CollaborationTransferProgress? _lastFailedProgress;
    private int _scalePercent;
    private int _activeQueueOperations;
    private bool _petTransferFailed;
    private bool _showingCompletionState;
    private bool _activeDueAlarm;
    private bool _archiveOperationActive;
    private bool _shelfTransitionInProgress;
    private bool _initialized;
    private bool _closed;
    private int _phoneImageReminderCount;

    internal IDisposable BeginPhoneImageReminder()
    {
        Dispatcher.VerifyAccess();
        _phoneImageReminderCount++;
        if (!_closed) _ = SwitchStateSafelyAsync("state-05");
        return new PhoneImageReminderLease(this);
    }

    private sealed class PhoneImageReminderLease(DesktopPetWindow owner) : IDisposable
    {
        private DesktopPetWindow? _owner = owner;
        public void Dispose()
        {
            var pet = Interlocked.Exchange(ref _owner, null);
            if (pet is null) return;
            pet._phoneImageReminderCount = Math.Max(0, pet._phoneImageReminderCount - 1);
            if (!pet._closed && pet._phoneImageReminderCount == 0)
                _ = pet.SwitchStateSafelyAsync("state-01");
        }
    }

    internal DesktopPetWindow(int initialScalePercent = 100)
        : this(
            new PetSessionShelfService(cleanupStaleSessions: false),
            new PetAlarmService(),
            initialScalePercent,
            ownsShelfService: true,
            ownsAlarmService: true)
    {
    }

    internal DesktopPetWindow(
        PetSessionShelfService shelfService,
        PetAlarmService alarmService,
        int initialScalePercent = 100)
        : this(shelfService, alarmService, initialScalePercent, ownsShelfService: false, ownsAlarmService: false)
    {
    }

    private DesktopPetWindow(
        PetSessionShelfService shelfService,
        PetAlarmService alarmService,
        int initialScalePercent,
        bool ownsShelfService,
        bool ownsAlarmService)
    {
        InitializeComponent();
        _shelfService = shelfService;
        _alarmService = alarmService;
        _ownsShelfService = ownsShelfService;
        _ownsAlarmService = ownsAlarmService;
        _catalog = DesktopPetAnimationCatalog.LoadDefault();
        _scalePercent = Math.Clamp(initialScalePercent, 60, 160);
        var initialSize = CalculateDisplaySize(_scalePercent);
        Width = initialSize;
        Height = initialSize;
        PetImage.Width = initialSize;
        PetImage.Height = initialSize;
        _player = new DesktopPetAnimationPlayer(
            PetImage,
            _catalog,
            Math.Min(_catalog.CanvasWidth, (int)Math.Ceiling(initialSize)));
        BuildAnimationStateMenu();
        _player.StateChanged += Player_StateChanged;
        _collaborationService.TransferProgressChanged += CollaborationService_TransferProgressChanged;
        _shelfService.ItemsChanged += ShelfService_ItemsChanged;
        _alarmService.ItemsChanged += AlarmService_ItemsChanged;
        Loaded += DesktopPetWindow_Loaded;
        IsVisibleChanged += DesktopPetWindow_IsVisibleChanged;
        LocationChanged += (_, _) => UpdateTransferBubblePosition();
        SizeChanged += (_, _) => UpdateTransferBubblePosition();
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
    }

    internal event EventHandler? PetVisibilityChanged;

    internal string? CurrentStateId => _player.CurrentStateId;

    internal int CurrentFrameIndex => _player.CurrentFrameIndex;

    internal long CachedDecodedBytes => _player.CachedDecodedBytes;

    internal IReadOnlyList<string> StateIds => _catalog.States.Select(state => state.Id).ToArray();

    internal int ScalePercent => _scalePercent;

    internal Task SwitchStateForValidationAsync(string stateId, CancellationToken cancellationToken) =>
        _player.SwitchStateAsync(stateId, cancellationToken);

    internal Rect GetCurrentWorkingAreaForValidation() => GetCurrentWorkingArea();

    internal Rect GetCurrentScreenBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var bounds = Forms.Screen.FromHandle(handle).Bounds;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }

        var transform = source.CompositionTarget.TransformFromDevice;
        return new Rect(
            transform.Transform(new Point(bounds.Left, bounds.Top)),
            transform.Transform(new Point(bounds.Right, bounds.Bottom)));
    }

    internal async Task SetScalePercentForValidationAsync(
        int scalePercent,
        CancellationToken cancellationToken)
    {
        ApplyScalePercent(scalePercent);
        await _player.SetDecodePixelWidthAsync(
            Math.Min(_catalog.CanvasWidth, (int)Math.Ceiling(CalculateDisplaySize(_scalePercent))),
            cancellationToken);
    }

    internal void SetScalePercent(int scalePercent)
    {
        CloseCommandExperience();
        if (!ApplyScalePercent(scalePercent))
        {
            return;
        }

        _resizeDecodeCancellation?.Cancel();
        _resizeDecodeCancellation?.Dispose();
        _resizeDecodeCancellation = new CancellationTokenSource();
        _ = RefreshDecodedFramesAfterResizeAsync(_resizeDecodeCancellation.Token);
    }

    internal void SnapToNearestEdge()
    {
        if (!IsLoaded)
        {
            return;
        }

        var workingArea = GetCurrentWorkingArea();
        var target = DesktopPetPlacement.SnapToNearestEdge(
            new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height),
            workingArea,
            EdgeMargin);
        Left = target.X;
        Top = target.Y;
        UpdateTransferBubblePosition();
    }

    /// <summary>主窗口切换页面或重新激活后，重申桌宠的非激活置顶层级。</summary>
    internal void EnsureTopmostWithoutActivation()
    {
        if (_closed || !IsVisible)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.KeepWindowTopmostWithoutActivating(handle);
        _transferBubble?.RefreshTopmost();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_closed)
        {
            base.OnClosed(e);
            return;
        }

        _closed = true;
        _actionWheel?.Close();
        _actionWheel = null;
        CloseCommandExperience();
        _transferBubble?.Close();
        _transferBubble = null;
        _phoneBubbleTimer.Stop(); _phoneBubble?.Close(); _phoneBubble = null;
        _resizeDecodeCancellation?.Cancel();
        _resizeDecodeCancellation?.Dispose();
        _resizeDecodeCancellation = null;
        _archiveOperationCancellation?.Cancel();
        _archiveOperationCancellation?.Dispose();
        _archiveOperationCancellation = null;
        _archiveOperationActive = false;
        CancelCompletionReset();
        CancelShelfBubbleReset();
        CancelAlarmBubbleReset();
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        _player.StateChanged -= Player_StateChanged;
        _collaborationService.TransferProgressChanged -= CollaborationService_TransferProgressChanged;
        _shelfService.ItemsChanged -= ShelfService_ItemsChanged;
        _alarmService.ItemsChanged -= AlarmService_ItemsChanged;
        _player.Dispose();
        if (_ownsShelfService)
        {
            _shelfService.Dispose();
        }
        if (_ownsAlarmService)
        {
            _alarmService.Dispose();
        }
        base.OnClosed(e);
    }

    private async void DesktopPetWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            _initialized = true;
            PlaceAtBottomRight();
            try
            {
                await _player.SwitchStateAsync(_catalog.States[0].Id);
            }
            catch (OperationCanceledException)
            {
                // 启动过程中收到图片等新状态请求时，让最新状态继续加载，不隐藏宠物。
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"桌面宠物初始状态加载失败：{exception}");
                Hide();
                return;
            }
        }

        _player.Play();
    }

    private void DesktopPetWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _player.Play();
            Dispatcher.BeginInvoke(() =>
            {
                SnapToNearestEdge();
                RestoreActiveTransferBubble();
            });
        }
        else
        {
            DismissPhoneNotification();
            _player.Pause();
            if (!_activeDueAlarm)
            {
                _transferBubble?.Hide();
            }
            CloseCommandExperience();
        }

        PetVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PetSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var startLeft = Left;
        var startTop = Top;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var moved = Math.Abs(Left - startLeft) > 3 || Math.Abs(Top - startTop) > 3;
        if (moved)
        {
            CloseCommandExperience();
            SnapToNearestEdge();
            return;
        }

        if (_actionWheel is not null)
        {
            return;
        }

        ToggleCommandExperience();
    }

    private async void AnimationStateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (HasAnyActiveTransfer || _showingCompletionState || _actionWheel is not null || _commandWheel is not null)
        {
            return;
        }

        if (sender is MenuItem { Tag: string stateId })
        {
            await SwitchStateSafelyAsync(stateId);
        }
    }

    private void SnapToEdgeMenuItem_Click(object sender, RoutedEventArgs e) => SnapToNearestEdge();

    private void HidePetMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // 右键菜单属于用户主动关闭，需要保存；普通 Hide 仅改变运行状态。
        if (Application.Current is App app) app.SetDesktopPetVisible(false);
        else Hide();
    }

    private void PetSurface_DragEnter(object sender, DragEventArgs e)
    {
        if (HasAnyActiveTransfer || !TryGetDraggedFiles(e.Data, out var files))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        CloseCommandExperience();
        if (_actionWheel is not null)
        {
            return;
        }

        CancelCompletionReset();

        var petBounds = new Rect(
            Left,
            Top,
            ActualWidth > 0 ? ActualWidth : Width,
            ActualHeight > 0 ? ActualHeight : Height);
        var wheel = new PetActionWheelWindow(petBounds, GetCurrentWorkingArea(), files);
        wheel.FilesDropped += ActionWheel_FilesDropped;
        wheel.FilesStored += ActionWheel_FilesStored;
        wheel.ArchiveRequested += ActionWheel_ArchiveRequested;
        wheel.Cancelled += ActionWheel_Cancelled;
        _actionWheel = wheel;
        wheel.Show();
        _ = SwitchStateSafelyAsync("state-02");
    }

    private void PetSurface_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !HasAnyActiveTransfer && TryGetDraggedFiles(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void PetSurface_DragLeave(object sender, DragEventArgs e)
    {
        // 轮盘窗口显示后会接管后续拖拽；它离开自身边界时负责关闭并恢复宠物状态。
    }

    private async void PetSurface_Drop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (_actionWheel is not null)
        {
            _actionWheel.Close();
            _actionWheel = null;
        }
        await RestoreStateAfterWheelAsync();
    }

    private void ActionWheel_FilesDropped(IReadOnlyList<string> files)
    {
        _actionWheel = null;
        _ = QueuePetFilesAsync(files);
    }

    private void ActionWheel_FilesStored(IReadOnlyList<string> files)
    {
        _actionWheel = null;
        _ = StorePetFilesAsync(files);
    }

    private void ActionWheel_ArchiveRequested(
        IReadOnlyList<string> paths,
        PetArchiveDropAction action)
    {
        _actionWheel = null;
        _ = RunPetArchiveOperationAsync(paths, action);
    }

    private void ActionWheel_Cancelled()
    {
        _actionWheel = null;
        _ = RestoreStateAfterWheelAsync();
    }

    private async Task StorePetFilesAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            await RestoreStateAfterWheelAsync();
            return;
        }

        PetShelfAddResult result;
        try
        {
            result = await _shelfService.AddFilesAsync(files);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"桌宠文件暂存失败：{exception.GetBaseException().Message}");
            await RestoreStateAfterWheelAsync();
            return;
        }

        await RestoreStateAfterWheelAsync();
        if (result.AddedCount > 0)
        {
            _ = ShowShelfCompletionBubbleAsync(result);
        }
        else if (!string.IsNullOrWhiteSpace(result.LimitMessage))
        {
            _ = ShowShelfMessageBubbleAsync("暂存区空间不足", result.LimitMessage, "单击查看并清理");
        }
        else if (result.DuplicateCount > 0)
        {
            _ = ShowShelfMessageBubbleAsync("文件已在暂存区", "没有重复存入相同文件", "单击查看");
        }
        else
        {
            _ = ShowShelfMessageBubbleAsync("文件暂存失败", "文件可能已移动或正在被占用", "单击查看");
        }
    }

    private void ToggleCommandExperience()
    {
        if (_commandWheel is not null || _shelfPanel is not null || _alarmPanel is not null)
        {
            CloseCommandExperience();
            return;
        }

        OpenCommandExperience(showShelf: false);
    }

    private void OpenCommandExperience(bool showShelf)
    {
        if (!IsVisible || !IsLoaded)
        {
            return;
        }

        if (_commandWheel is null)
        {
            var wheel = new PetCommandWheelWindow(
                GetPetBounds(),
                GetCurrentWorkingArea(),
                _shelfService.Items.Count,
                _alarmService.Items.Count);
            wheel.ShelfRequested += ShowShelfPanel;
            wheel.AlarmRequested += ShowAlarmPanel;
            wheel.CalendarRequested += () => { CloseCommandExperience(); ScreenshotApp.PhoneCalendar.PhoneCalendarWindow.Open(); };
            wheel.MailRequested += () => { CloseCommandExperience(); ScreenshotApp.Mail.MailWindow.Open(); };
            wheel.DismissRequested += CloseCommandExperience;
            wheel.Closed += (_, _) =>
            {
                if (ReferenceEquals(_commandWheel, wheel))
                {
                    _commandWheel = null;
                    _shelfPanel?.Close();
                    _shelfPanel = null;
                    _alarmPanel?.Close();
                    _alarmPanel = null;
                }
            };
            _commandWheel = wheel;
            wheel.Show();
        }

        if (showShelf)
        {
            Dispatcher.BeginInvoke(ShowShelfPanel);
        }
    }

    private async void ShowShelfPanel()
    {
        var wheel = _commandWheel;
        if (wheel is null || _shelfTransitionInProgress)
        {
            return;
        }

        if (_shelfPanel is not null)
        {
            _shelfPanel.Activate();
            return;
        }

        _shelfTransitionInProgress = true;
        wheel.KeepOpenForCompanion = true;
        try
        {
            var panel = new PetShelfPanelWindow(_shelfService, () => false);
            var panelBounds = wheel.CalculatePanelBounds(panel.Width, panel.Height);
            var transitionAnchor = wheel.CalculateTransitionAnchor(panelBounds);
            await wheel.PlaySelectionTransitionAsync(PetCommandWheelWindow.ShelfCommandIndex, panelBounds);
            if (!ReferenceEquals(_commandWheel, wheel))
            {
                panel.Close();
                return;
            }

            panel.DismissRequested += CloseCommandExperience;
            panel.Closed += (_, _) =>
            {
                if (ReferenceEquals(_shelfPanel, panel))
                {
                    _shelfPanel = null;
                }
            };
            panel.UpdatePlacement(panelBounds);
            panel.PrepareEntrance(transitionAnchor, panelBounds);
            _shelfPanel = panel;
            panel.Show();
            panel.Activate();
            _commandWheel = null;
            wheel.Close();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"桌宠暂存区转场失败：{exception.GetBaseException().Message}");
            CloseCommandExperience();
        }
        finally
        {
            _shelfTransitionInProgress = false;
        }
    }

    private async void ShowAlarmPanel()
    {
        var wheel = _commandWheel;
        if (wheel is null || _shelfTransitionInProgress)
        {
            return;
        }

        if (_alarmPanel is not null)
        {
            _alarmPanel.Activate();
            return;
        }

        _shelfTransitionInProgress = true;
        wheel.KeepOpenForCompanion = true;
        try
        {
            var panel = new PetAlarmPanelWindow(_alarmService, () => false);
            var panelBounds = wheel.CalculatePanelBounds(panel.Width, panel.Height);
            var transitionAnchor = wheel.CalculateTransitionAnchor(panelBounds);
            await wheel.PlaySelectionTransitionAsync(PetCommandWheelWindow.AlarmCommandIndex, panelBounds);
            if (!ReferenceEquals(_commandWheel, wheel))
            {
                panel.Close();
                return;
            }

            panel.DismissRequested += CloseCommandExperience;
            panel.Closed += (_, _) =>
            {
                if (ReferenceEquals(_alarmPanel, panel))
                {
                    _alarmPanel = null;
                }
            };
            panel.UpdatePlacement(panelBounds);
            panel.PrepareEntrance(transitionAnchor, panelBounds);
            _alarmPanel = panel;
            panel.Show();
            panel.Activate();
            _commandWheel = null;
            wheel.Close();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"桌宠闹钟转场失败：{exception.GetBaseException().Message}");
            CloseCommandExperience();
        }
        finally
        {
            _shelfTransitionInProgress = false;
        }
    }

    private void CloseCommandExperience()
    {
        var panel = _shelfPanel;
        var alarmPanel = _alarmPanel;
        var wheel = _commandWheel;
        _shelfPanel = null;
        _alarmPanel = null;
        _commandWheel = null;
        _shelfTransitionInProgress = false;
        if (wheel is not null)
        {
            wheel.KeepOpenForCompanion = false;
        }
        panel?.Close();
        alarmPanel?.Close();
        wheel?.Close();
    }

    private void AlarmService_ItemsChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            _commandWheel?.UpdateAlarmCount(_alarmService.Items.Count);
        }
        else
        {
            Dispatcher.BeginInvoke(() => _commandWheel?.UpdateAlarmCount(_alarmService.Items.Count));
        }
    }

    private void ShelfService_ItemsChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            _commandWheel?.UpdateShelfCount(_shelfService.Items.Count);
        }
        else
        {
            Dispatcher.BeginInvoke(() => _commandWheel?.UpdateShelfCount(_shelfService.Items.Count));
        }
    }

    internal async Task ShowScreenshotShelfNoticeAsync(string title, string detail)
    {
        // 正在传输或显示到点闹钟时先等待，避免自动暂存提醒抢占已有气泡。
        while (!_closed && IsVisible && (HasAnyActiveTransfer || _showingCompletionState || _activeDueAlarm))
            await Task.Delay(500);
        if (_closed || !IsVisible) return;
        await ShowShelfMessageBubbleAsync(title, detail, "单击查看暂存区");
    }

    private Task ShowShelfCompletionBubbleAsync(PetShelfAddResult result)
    {
        var detailParts = new List<string>();
        if (result.DuplicateCount > 0)
        {
            detailParts.Add($"跳过 {result.DuplicateCount} 个重复文件");
        }
        if (result.FailedCount > 0)
        {
            detailParts.Add($"{result.FailedCount} 个失败");
        }
        if (!string.IsNullOrWhiteSpace(result.LimitMessage))
        {
            detailParts.Add(result.LimitMessage);
        }

        var fileSummary = detailParts.Count == 0
            ? "文件已安全保留到本次运行的暂存区"
            : string.Join(" · ", detailParts);
        return ShowShelfMessageBubbleAsync(
            $"暂存区现有 {_shelfService.Items.Count} 个文件",
            fileSummary,
            "单击查看暂存区");
    }

    private async Task ShowShelfMessageBubbleAsync(string title, string detail, string actionText)
    {
        if (!IsVisible || HasAnyActiveTransfer || _showingCompletionState)
        {
            return;
        }

        CancelShelfBubbleReset();
        var resetCancellation = new CancellationTokenSource();
        _shelfBubbleResetCancellation = resetCancellation;
        try
        {
            EnsureTransferBubble().ShowShelfMessage(title, detail, actionText, () => OpenCommandExperience(showShelf: true));
            UpdateTransferBubblePosition();
            await Task.Delay(TimeSpan.FromSeconds(4), resetCancellation.Token);
            if (!HasAnyActiveTransfer && !_showingCompletionState)
            {
                _transferBubble?.Hide();
            }
        }
        catch (OperationCanceledException)
        {
            // 手机传输开始后立即交还气泡显示权。
        }
        finally
        {
            if (ReferenceEquals(_shelfBubbleResetCancellation, resetCancellation))
            {
                _shelfBubbleResetCancellation = null;
            }
            resetCancellation.Dispose();
        }
    }

    private void CancelShelfBubbleReset()
    {
        var cancellation = _shelfBubbleResetCancellation;
        _shelfBubbleResetCancellation = null;
        cancellation?.Cancel();
    }

    internal async Task ShowAlarmReminderAsync(PetAlarmTrigger trigger)
    {
        if (!IsVisible)
        {
            return;
        }

        CancelAlarmBubbleReset();
        CancelShelfBubbleReset();
        var previousState = _player.CurrentStateId;
        var resetCancellation = new CancellationTokenSource();
        _alarmBubbleResetCancellation = resetCancellation;
        _activeDueAlarm = trigger.IsDue;
        using var topmostPulse = new AlarmTopmostPulse(RefreshAlarmTopmost, TimeSpan.FromMilliseconds(350));
        topmostPulse.Start();
        try
        {
            await SwitchStateSafelyAsync("state-03");
            if (trigger.IsDue)
            {
                var dismissed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                EnsureTransferBubble().ShowAlarmMessage(trigger, () => dismissed.TrySetResult(true));
                UpdateTransferBubblePosition();
                RefreshAlarmTopmost();
                await dismissed.Task.WaitAsync(resetCancellation.Token);
            }
            else
            {
                EnsureTransferBubble().ShowAlarmMessage(trigger);
                UpdateTransferBubblePosition();
                RefreshAlarmTopmost();
                await Task.Delay(TimeSpan.FromSeconds(5), resetCancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 新提醒或传输开始后，由新的状态接管。
        }
        finally
        {
            if (ReferenceEquals(_alarmBubbleResetCancellation, resetCancellation))
            {
                _alarmBubbleResetCancellation = null;
                _activeDueAlarm = false;
                if (HasAnyActiveTransfer)
                {
                    RestoreActiveTransferBubble();
                    await SwitchStateSafelyAsync("state-06");
                }
                else if (_showingCompletionState)
                {
                    await SwitchStateSafelyAsync("state-05");
                }
                else
                {
                    _transferBubble?.Hide();
                    await SwitchStateSafelyAsync(string.IsNullOrWhiteSpace(previousState) ? "state-01" : previousState);
                }
            }
            resetCancellation.Dispose();
        }
    }

    private void RefreshAlarmTopmost()
    {
        if (!IsVisible) return;
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.KeepWindowTopmostWithoutActivating(handle);
        _transferBubble?.RefreshTopmost();
    }

    private void CancelAlarmBubbleReset()
    {
        var cancellation = _alarmBubbleResetCancellation;
        _alarmBubbleResetCancellation = null;
        cancellation?.Cancel();
    }

    private async Task RunPetArchiveOperationAsync(
        IReadOnlyList<string> paths,
        PetArchiveDropAction action)
    {
        if (paths.Count == 0 || _archiveOperationActive)
        {
            await RestoreStateAfterWheelAsync();
            return;
        }

        CancelCompletionReset();
        CancelShelfBubbleReset();
        _archiveOperationCancellation?.Cancel();
        _archiveOperationCancellation?.Dispose();
        var operationCancellation = new CancellationTokenSource();
        _archiveOperationCancellation = operationCancellation;
        _archiveOperationActive = true;
        var sourceSummary = paths.Count == 1
            ? Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : $"{paths.Count} 个项目";

        try
        {
            await SwitchStateSafelyAsync("state-06");
            if (IsVisible)
            {
                EnsureTransferBubble().ShowArchiveProgress(action, sourceSummary);
                UpdateTransferBubblePosition();
            }

            var archiveService = new ArchiveService();
            string completionPath;
            string completionName;
            long processedBytes;
            if (action == PetArchiveDropAction.Compress)
            {
                var outputPath = PetArchiveDropPlanner.CreateCompressionOutputPath(paths);
                var progress = CreatePetArchiveProgress(action, sourceSummary);
                var compressionResult = await archiveService.CreateAsync(
                    paths,
                    outputPath,
                    ArchiveOutputFormat.Zip,
                    ArchiveCompressionPreset.Balanced,
                    includeTopLevelDirectory: true,
                    progress,
                    operationCancellation.Token);
                completionPath = compressionResult.OutputPath;
                completionName = Path.GetFileName(compressionResult.OutputPath);
                processedBytes = compressionResult.ProcessedBytes;
            }
            else
            {
                long totalProcessedBytes = 0;
                string? firstDestination = null;
                foreach (var (archivePath, index) in paths.Select((path, index) => (path, index)))
                {
                    operationCancellation.Token.ThrowIfCancellationRequested();
                    var archiveName = Path.GetFileName(archivePath);
                    var itemSummary = paths.Count == 1
                        ? archiveName
                        : $"{index + 1}/{paths.Count} · {archiveName}";
                    if (IsVisible)
                    {
                        EnsureTransferBubble().ShowArchiveProgress(action, itemSummary);
                        UpdateTransferBubblePosition();
                    }

                    var destination = PetArchiveDropPlanner.GetExtractionDestination(archivePath);
                    firstDestination ??= destination;
                    var itemResult = await archiveService.ExtractAsync(
                        archivePath,
                        destination,
                        password: null,
                        ArchiveConflictPolicy.Rename,
                        CreatePetArchiveProgress(action, itemSummary),
                        operationCancellation.Token);
                    totalProcessedBytes += itemResult.ProcessedBytes;
                }

                completionPath = firstDestination
                                 ?? PetArchiveDropPlanner.GetExtractionDestination(paths[0]);
                completionName = paths.Count == 1
                    ? $"{Path.GetFileName(paths[0])} · 已解压"
                    : $"{paths.Count} 个压缩包已解压";
                processedBytes = totalProcessedBytes;
            }

            _archiveOperationActive = false;
            await ShowArchiveCompletionStateAsync(
                action,
                completionName,
                completionPath,
                processedBytes);
        }
        catch (OperationCanceledException)
        {
            _archiveOperationActive = false;
            _transferBubble?.Hide();
            if (!_closed)
            {
                await RestoreStateAfterWheelAsync();
            }
        }
        catch (Exception exception)
        {
            _archiveOperationActive = false;
            System.Diagnostics.Debug.WriteLine($"桌宠压缩任务失败：{exception}");
            await ShowArchiveFailureBubbleAsync(
                action,
                sourceSummary,
                exception.GetBaseException().Message);
        }
        finally
        {
            _archiveOperationActive = false;
            if (ReferenceEquals(_archiveOperationCancellation, operationCancellation))
            {
                _archiveOperationCancellation = null;
            }
            operationCancellation.Dispose();
        }
    }

    private IProgress<ArchiveProgressInfo> CreatePetArchiveProgress(
        PetArchiveDropAction action,
        string sourceSummary)
    {
        return new Progress<ArchiveProgressInfo>(progress =>
        {
            if (_closed || !_archiveOperationActive || !IsVisible)
            {
                return;
            }

            EnsureTransferBubble().ShowArchiveProgress(action, sourceSummary, progress);
            UpdateTransferBubblePosition();
            if (!string.Equals(_player.CurrentStateId, "state-06", StringComparison.OrdinalIgnoreCase))
            {
                _ = SwitchStateSafelyAsync("state-06");
            }
        });
    }

    private async Task ShowArchiveCompletionStateAsync(
        PetArchiveDropAction action,
        string resultName,
        string resultPath,
        long processedBytes)
    {
        CancelCompletionReset();
        var resetCancellation = new CancellationTokenSource();
        _completionResetCancellation = resetCancellation;
        _showingCompletionState = true;
        try
        {
            await SwitchStateSafelyAsync("state-05");
            if (IsVisible)
            {
                EnsureTransferBubble().ShowArchiveCompletion(
                    action,
                    resultName,
                    resultPath,
                    processedBytes);
                UpdateTransferBubblePosition();
            }
            await Task.Delay(TimeSpan.FromSeconds(5), resetCancellation.Token);
            _transferBubble?.Hide();
            if (!HasAnyActiveTransfer && _actionWheel is null)
            {
                await SwitchStateSafelyAsync("state-01");
            }
        }
        catch (OperationCanceledException)
        {
            // 新任务开始后，由新的进度状态接管。
        }
        finally
        {
            if (ReferenceEquals(_completionResetCancellation, resetCancellation))
            {
                _completionResetCancellation = null;
                _showingCompletionState = false;
            }
            resetCancellation.Dispose();
        }
    }

    private async Task ShowArchiveFailureBubbleAsync(
        PetArchiveDropAction action,
        string sourceName,
        string message)
    {
        CancelCompletionReset();
        var resetCancellation = new CancellationTokenSource();
        _completionResetCancellation = resetCancellation;
        try
        {
            await SwitchStateSafelyAsync("state-01");
            if (IsVisible)
            {
                EnsureTransferBubble().ShowArchiveFailure(action, sourceName, message);
                UpdateTransferBubblePosition();
            }
            await Task.Delay(TimeSpan.FromSeconds(5), resetCancellation.Token);
            _transferBubble?.Hide();
        }
        catch (OperationCanceledException)
        {
            // 新任务开始后立即交还气泡显示权。
        }
        finally
        {
            if (ReferenceEquals(_completionResetCancellation, resetCancellation))
            {
                _completionResetCancellation = null;
            }
            resetCancellation.Dispose();
        }
    }

    private async Task QueuePetFilesAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            await RestoreStateAfterWheelAsync();
            return;
        }

        CancelCompletionReset();
        _lastCompletedProgress = null;
        _lastFailedProgress = null;
        _activeQueueOperations++;
        await SwitchStateSafelyAsync("state-06");
        try
        {
            if (!_collaborationService.IsRunning)
            {
                _collaborationService.Start();
            }

            foreach (var file in files)
            {
                var transferId = Guid.NewGuid().ToString("N");
                _petTransferIds.Add(transferId);
                try
                {
                    await _collaborationService.QueueOutgoingTransferAsync(file, transferId);
                }
                catch
                {
                    _petTransferIds.Remove(transferId);
                    _petTransferFailed = true;
                }
            }
        }
        catch
        {
            _petTransferFailed = true;
        }
        finally
        {
            _activeQueueOperations = Math.Max(0, _activeQueueOperations - 1);
            TryFinishPetTransferSequence();
        }
    }

    private void CollaborationService_TransferProgressChanged(CollaborationTransferProgress progress)
    {
        _ = Dispatcher.InvokeAsync(() => HandleTransferProgress(progress));
    }

    private void HandleTransferProgress(CollaborationTransferProgress progress)
    {
        var isTerminal = progress.State is "Completed" or "Failed";
        if (!isTerminal)
        {
            if (!_activeDueAlarm)
            {
                CancelAlarmBubbleReset();
            }
            CancelShelfBubbleReset();
            CancelCompletionReset();
            _activeTransferProgress[progress.TransferId] = progress;
            var speed = UpdateTransferSpeed(progress);
            if (IsVisible)
            {
                EnsureTransferBubble().ShowTransfer(progress, speed);
                UpdateTransferBubblePosition();
            }

            if (_actionWheel is null && !_showingCompletionState &&
                !string.Equals(_player.CurrentStateId, "state-06", StringComparison.OrdinalIgnoreCase))
            {
                _ = SwitchStateSafelyAsync("state-06");
            }
            return;
        }

        _activeTransferProgress.Remove(progress.TransferId);
        _transferSpeedSamples.Remove(progress.TransferId);
        var trackedByPet = _petTransferIds.Remove(progress.TransferId);
        if (progress.State == "Completed")
        {
            _lastCompletedProgress = progress;
        }
        else
        {
            _petTransferFailed |= trackedByPet;
            _lastFailedProgress = progress;
        }

        if (_activeTransferProgress.Count > 0)
        {
            RestoreActiveTransferBubble();
            if (_actionWheel is null)
            {
                _ = SwitchStateSafelyAsync("state-06");
            }
            return;
        }

        if (trackedByPet)
        {
            TryFinishPetTransferSequence();
            return;
        }

        if (progress.State == "Failed")
        {
            _ = ShowFailureBubbleAsync(progress);
        }
        else if (_activeQueueOperations == 0)
        {
            _ = ShowCompletionStateAsync(progress);
        }
    }

    private void TryFinishPetTransferSequence()
    {
        if (_activeQueueOperations > 0 || _petTransferIds.Count > 0 || _showingCompletionState)
        {
            return;
        }

        if (_petTransferFailed)
        {
            _petTransferFailed = false;
            if (_lastFailedProgress is { } failedProgress)
            {
                _ = ShowFailureBubbleAsync(failedProgress);
            }
            else
            {
                _transferBubble?.Hide();
                _ = SwitchStateSafelyAsync("state-01");
            }
            return;
        }

        if (_lastCompletedProgress is { } completedProgress)
        {
            _ = ShowCompletionStateAsync(completedProgress);
        }
        else
        {
            _transferBubble?.Hide();
            _ = SwitchStateSafelyAsync("state-01");
        }
    }

    private async Task ShowCompletionStateAsync(CollaborationTransferProgress progress)
    {
        CancelCompletionReset();
        var resetCancellation = new CancellationTokenSource();
        _completionResetCancellation = resetCancellation;
        _showingCompletionState = true;
        try
        {
            await SwitchStateSafelyAsync("state-05");
            if (IsVisible)
            {
                EnsureTransferBubble().ShowCompletion(progress);
                UpdateTransferBubblePosition();
            }
            await Task.Delay(TimeSpan.FromSeconds(5), resetCancellation.Token);
            _transferBubble?.Hide();
            if (!HasAnyActiveTransfer && _actionWheel is null)
            {
                await SwitchStateSafelyAsync("state-01");
            }
        }
        catch (OperationCanceledException)
        {
            // 新一轮拖拽或传输开始后，由新的交互状态接管。
        }
        finally
        {
            if (ReferenceEquals(_completionResetCancellation, resetCancellation))
            {
                _completionResetCancellation = null;
                _showingCompletionState = false;
            }
            resetCancellation.Dispose();
        }
    }

    private async Task ShowFailureBubbleAsync(CollaborationTransferProgress progress)
    {
        CancelCompletionReset();
        var resetCancellation = new CancellationTokenSource();
        _completionResetCancellation = resetCancellation;
        try
        {
            await SwitchStateSafelyAsync("state-01");
            if (IsVisible)
            {
                EnsureTransferBubble().ShowFailure(progress);
                UpdateTransferBubblePosition();
            }
            await Task.Delay(TimeSpan.FromSeconds(5), resetCancellation.Token);
            _transferBubble?.Hide();
        }
        catch (OperationCanceledException)
        {
            // 新传输开始后立即切换为实时进度气泡。
        }
        finally
        {
            if (ReferenceEquals(_completionResetCancellation, resetCancellation))
            {
                _completionResetCancellation = null;
            }
            resetCancellation.Dispose();
        }
    }

    private async Task RestoreStateAfterWheelAsync()
    {
        if (HasAnyActiveTransfer)
        {
            await SwitchStateSafelyAsync("state-06");
        }
        else if (_showingCompletionState)
        {
            await SwitchStateSafelyAsync("state-05");
        }
        else
        {
            await SwitchStateSafelyAsync("state-01");
        }
    }

    private void CancelCompletionReset()
    {
        var cancellation = _completionResetCancellation;
        _completionResetCancellation = null;
        _showingCompletionState = false;
        cancellation?.Cancel();
        if (cancellation is not null)
        {
            _transferBubble?.Hide();
        }
    }

    private bool HasAnyActiveTransfer =>
        _archiveOperationActive ||
        _activeQueueOperations > 0 ||
        _petTransferIds.Count > 0 ||
        _activeTransferProgress.Count > 0;

    private PetTransferBubbleWindow EnsureTransferBubble()
    {
        if (_transferBubble is null)
        {
            _transferBubble = new PetTransferBubbleWindow();
        }
        return _transferBubble;
    }

    private double UpdateTransferSpeed(CollaborationTransferProgress progress)
    {
        var now = progress.UpdatedAt;
        if (!_transferSpeedSamples.TryGetValue(progress.TransferId, out var previous) ||
            progress.TransferredBytes < previous.Bytes)
        {
            _transferSpeedSamples[progress.TransferId] = new TransferSpeedSample(progress.TransferredBytes, now, 0);
            return 0;
        }

        var elapsedSeconds = (now - previous.UpdatedAt).TotalSeconds;
        if (elapsedSeconds <= 0 || progress.TransferredBytes == previous.Bytes)
        {
            return previous.SmoothedBytesPerSecond;
        }

        var instantSpeed = (progress.TransferredBytes - previous.Bytes) / elapsedSeconds;
        var smoothedSpeed = previous.SmoothedBytesPerSecond <= 0
            ? instantSpeed
            : previous.SmoothedBytesPerSecond * 0.65 + instantSpeed * 0.35;
        _transferSpeedSamples[progress.TransferId] = new TransferSpeedSample(
            progress.TransferredBytes,
            now,
            smoothedSpeed);
        return smoothedSpeed;
    }

    private void RestoreActiveTransferBubble()
    {
        if (!IsVisible || _activeTransferProgress.Count == 0)
        {
            return;
        }

        var progress = _activeTransferProgress.Values
            .OrderByDescending(item => item.UpdatedAt)
            .First();
        var speed = _transferSpeedSamples.TryGetValue(progress.TransferId, out var sample)
            ? sample.SmoothedBytesPerSecond
            : 0;
        EnsureTransferBubble().ShowTransfer(progress, speed);
        UpdateTransferBubblePosition();
    }

    private void UpdateTransferBubblePosition()
    {
        if (!IsVisible || !IsLoaded) return;
        if (_transferBubble is { IsVisible: true } transfer) PositionNotificationBubble(transfer, 0);
        if (_phoneBubble is { IsVisible: true } phone)
            PositionNotificationBubble(phone, _transferBubble is { IsVisible: true } ? _transferBubble.ActualHeight + 10 : 0);
    }
    private void PositionNotificationBubble(PetTransferBubbleWindow bubble, double offset)
    {
        bubble.UpdateLayout();
        var workingArea = GetCurrentWorkingArea();
        var petWidth = ActualWidth > 0 ? ActualWidth : Width;
        var petHeight = ActualHeight > 0 ? ActualHeight : Height;
        // 隐藏后的透明窗口可能短暂报告 1 像素 ActualWidth；定位时必须采用设计宽度，
        // 否则窗口会被错误夹到屏幕右缘，即使随后恢复布局也仍不可见。
        var bubbleWidth = Math.Max(
            PetTransferBubbleWindow.PreferredWidth,
            Math.Max(bubble.ActualWidth, bubble.Width));
        var bubbleHeight = bubble.ActualHeight > 0 ? bubble.ActualHeight : 126;
        var desiredLeft = Left + petWidth / 2 - bubbleWidth / 2;
        var desiredTop = Top - bubbleHeight + 14 - offset;

        bubble.Left = Math.Clamp(
            desiredLeft,
            workingArea.Left + 8,
            Math.Max(workingArea.Left + 8, workingArea.Right - bubbleWidth - 8));
        bubble.Top = desiredTop >= workingArea.Top + 8
            ? desiredTop
            : Math.Min(workingArea.Bottom - bubbleHeight - 8, Top + petHeight * 0.28);
        var petAnchorX = Left + petWidth * _catalog.Anchor.X / _catalog.CanvasWidth;
        bubble.SetTailAnchor(petAnchorX - bubble.Left);
        bubble.RefreshTopmost();
    }

    private readonly record struct TransferSpeedSample(long Bytes, DateTime UpdatedAt, double SmoothedBytesPerSecond);

    private static bool TryGetDraggedFiles(IDataObject dataObject, out IReadOnlyList<string> files)
    {
        files = Array.Empty<string>();
        if (!dataObject.GetDataPresent(DataFormats.FileDrop) ||
            dataObject.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return false;
        }

        var existingFiles = paths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        files = existingFiles;
        return existingFiles.Length > 0;
    }

    private async Task SwitchStateSafelyAsync(string stateId)
    {
        try
        {
            // 图片气泡存在时保持状态 5，避免其他完成计时器抢先复位。
            if (_closed) return;
            await _player.SwitchStateAsync(_phoneImageReminderCount > 0 ? "state-05" : stateId);
        }
        catch (OperationCanceledException)
        {
            // 用户快速切换状态时只保留最后一次请求。
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"桌面宠物状态切换失败：{exception}");
        }
    }

    private void Player_StateChanged(object? sender, DesktopPetStateChangedEventArgs e)
    {
        foreach (var menuItem in AnimationStateMenu.Items.OfType<MenuItem>())
        {
            menuItem.IsChecked = menuItem.Tag is string stateId &&
                                 stateId.Equals(e.State.Id, StringComparison.OrdinalIgnoreCase);
        }
        ToolTip = $"{e.State.DisplayName} · 左键单击打开功能轮盘 · 拖入文件可传输、暂存、压缩或解压 · 右键查看更多";
    }

    private void BuildAnimationStateMenu()
    {
        AnimationStateMenu.Items.Clear();
        foreach (var state in _catalog.States)
        {
            var item = new MenuItem
            {
                Header = state.DisplayName,
                Tag = state.Id,
                IsCheckable = true
            };
            item.Click += AnimationStateMenuItem_Click;
            AnimationStateMenu.Items.Add(item);
        }
    }

    private bool ApplyScalePercent(int scalePercent)
    {
        var normalizedScale = Math.Clamp(scalePercent, 60, 160);
        if (normalizedScale == _scalePercent && IsLoaded)
        {
            return false;
        }

        var oldWidth = ActualWidth > 0 ? ActualWidth : Width;
        var oldHeight = ActualHeight > 0 ? ActualHeight : Height;
        var anchorX = Left + oldWidth * _catalog.Anchor.X / _catalog.CanvasWidth;
        var anchorY = Top + oldHeight * _catalog.Anchor.Y / _catalog.CanvasHeight;
        _scalePercent = normalizedScale;
        var newSize = CalculateDisplaySize(normalizedScale);
        Width = newSize;
        Height = newSize;
        PetImage.Width = newSize;
        PetImage.Height = newSize;

        if (IsLoaded)
        {
            Left = anchorX - newSize * _catalog.Anchor.X / _catalog.CanvasWidth;
            Top = anchorY - newSize * _catalog.Anchor.Y / _catalog.CanvasHeight;
            Dispatcher.BeginInvoke(SnapToNearestEdge);
        }

        return true;
    }

    private async Task RefreshDecodedFramesAfterResizeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(260, cancellationToken);
            var decodeWidth = Math.Min(
                _catalog.CanvasWidth,
                (int)Math.Ceiling(CalculateDisplaySize(_scalePercent)));
            await _player.SetDecodePixelWidthAsync(decodeWidth, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 连续拖动滑块时只处理最后一次尺寸。
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"桌面宠物尺寸资源刷新失败：{exception}");
        }
    }

    private double CalculateDisplaySize(int scalePercent) =>
        _catalog.DisplaySize * scalePercent / 100d;

    private void PlaceAtBottomRight()
    {
        var workingArea = GetCurrentWorkingArea();
        Left = Math.Max(workingArea.Left + EdgeMargin, workingArea.Right - Width - EdgeMargin);
        Top = Math.Max(workingArea.Top + EdgeMargin, workingArea.Bottom - Height - EdgeMargin);
    }

    private Rect GetCurrentWorkingArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var bounds = Forms.Screen.FromHandle(handle).WorkingArea;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }

        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(new Point(bounds.Left, bounds.Top));
        var bottomRight = transform.Transform(new Point(bounds.Right, bounds.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        _actionWheel?.Close();
        _actionWheel = null;
        CloseCommandExperience();
        Dispatcher.BeginInvoke(SnapToNearestEdge);
    }

    private Rect GetPetBounds() => new(
        Left,
        Top,
        ActualWidth > 0 ? ActualWidth : Width,
        ActualHeight > 0 ? ActualHeight : Height);
}
