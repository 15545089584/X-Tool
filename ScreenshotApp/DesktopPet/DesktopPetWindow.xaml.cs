using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using ScreenshotApp.Collaboration;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.DesktopPet;

public partial class DesktopPetWindow : Window
{
    private const double EdgeMargin = 12;
    private readonly DesktopPetAnimationCatalog _catalog;
    private readonly DesktopPetAnimationPlayer _player;
    private readonly CollaborationService _collaborationService = CollaborationService.Instance;
    private readonly HashSet<string> _petTransferIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CollaborationTransferProgress> _activeTransferProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransferSpeedSample> _transferSpeedSamples = new(StringComparer.Ordinal);
    private CancellationTokenSource? _resizeDecodeCancellation;
    private CancellationTokenSource? _completionResetCancellation;
    private PetActionWheelWindow? _actionWheel;
    private PetTransferBubbleWindow? _transferBubble;
    private CollaborationTransferProgress? _lastCompletedProgress;
    private CollaborationTransferProgress? _lastFailedProgress;
    private int _scalePercent;
    private int _activeQueueOperations;
    private bool _petTransferFailed;
    private bool _showingCompletionState;
    private bool _initialized;
    private bool _closed;

    internal DesktopPetWindow(int initialScalePercent = 100)
    {
        InitializeComponent();
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
        _transferBubble?.Close();
        _transferBubble = null;
        _resizeDecodeCancellation?.Cancel();
        _resizeDecodeCancellation?.Dispose();
        _resizeDecodeCancellation = null;
        CancelCompletionReset();
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        _player.StateChanged -= Player_StateChanged;
        _collaborationService.TransferProgressChanged -= CollaborationService_TransferProgressChanged;
        _player.Dispose();
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
            _player.Pause();
            _transferBubble?.Hide();
        }

        PetVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void PetSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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
            SnapToNearestEdge();
            return;
        }

        if (HasAnyActiveTransfer || _showingCompletionState || _actionWheel is not null)
        {
            return;
        }

        var currentIndex = _catalog.States
            .Select((state, index) => (state, index))
            .FirstOrDefault(item => item.state.Id.Equals(_player.CurrentStateId, StringComparison.OrdinalIgnoreCase))
            .index;
        var nextState = _catalog.States[(currentIndex + 1) % _catalog.States.Count].Id;
        await SwitchStateSafelyAsync(nextState);
    }

    private async void AnimationStateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (HasAnyActiveTransfer || _showingCompletionState || _actionWheel is not null)
        {
            return;
        }

        if (sender is MenuItem { Tag: string stateId })
        {
            await SwitchStateSafelyAsync(stateId);
        }
    }

    private void SnapToEdgeMenuItem_Click(object sender, RoutedEventArgs e) => SnapToNearestEdge();

    private void HidePetMenuItem_Click(object sender, RoutedEventArgs e) => Hide();

    private void PetSurface_DragEnter(object sender, DragEventArgs e)
    {
        if (!TryGetDraggedFiles(e.Data, out var files))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
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
        var wheel = new PetActionWheelWindow(petBounds, GetCurrentWorkingArea());
        wheel.FilesDropped += ActionWheel_FilesDropped;
        wheel.Cancelled += ActionWheel_Cancelled;
        _actionWheel = wheel;
        wheel.Show();
        _ = SwitchStateSafelyAsync("state-02");
    }

    private void PetSurface_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDraggedFiles(e.Data, out _)
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

    private void ActionWheel_Cancelled()
    {
        _actionWheel = null;
        _ = RestoreStateAfterWheelAsync();
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
        _activeQueueOperations > 0 || _petTransferIds.Count > 0 || _activeTransferProgress.Count > 0;

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
        if (_transferBubble is not { IsVisible: true } bubble || !IsVisible || !IsLoaded)
        {
            return;
        }

        bubble.UpdateLayout();
        var workingArea = GetCurrentWorkingArea();
        var petWidth = ActualWidth > 0 ? ActualWidth : Width;
        var petHeight = ActualHeight > 0 ? ActualHeight : Height;
        var bubbleWidth = bubble.ActualWidth > 0 ? bubble.ActualWidth : bubble.Width;
        var bubbleHeight = bubble.ActualHeight > 0 ? bubble.ActualHeight : 126;
        var desiredLeft = Left + petWidth / 2 - bubbleWidth / 2;
        var desiredTop = Top - bubbleHeight + 14;

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
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        files = existingFiles;
        return existingFiles.Length > 0;
    }

    private async Task SwitchStateSafelyAsync(string stateId)
    {
        try
        {
            await _player.SwitchStateAsync(stateId);
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
        ToolTip = $"{e.State.DisplayName} · 可拖入文件传到手机 · 左键单击切换，拖动后自动贴边，右键查看更多";
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
        Dispatcher.BeginInvoke(SnapToNearestEdge);
    }
}
