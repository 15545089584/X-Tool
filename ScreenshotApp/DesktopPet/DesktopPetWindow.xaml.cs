using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.DesktopPet;

public partial class DesktopPetWindow : Window
{
    private const double EdgeMargin = 12;
    private readonly DesktopPetAnimationCatalog _catalog;
    private readonly DesktopPetAnimationPlayer _player;
    private CancellationTokenSource? _resizeDecodeCancellation;
    private int _scalePercent;
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
        Loaded += DesktopPetWindow_Loaded;
        IsVisibleChanged += DesktopPetWindow_IsVisibleChanged;
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
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_closed)
        {
            base.OnClosed(e);
            return;
        }

        _closed = true;
        _resizeDecodeCancellation?.Cancel();
        _resizeDecodeCancellation?.Dispose();
        _resizeDecodeCancellation = null;
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        _player.StateChanged -= Player_StateChanged;
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
            Dispatcher.BeginInvoke(SnapToNearestEdge);
        }
        else
        {
            _player.Pause();
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

        var currentIndex = _catalog.States
            .Select((state, index) => (state, index))
            .FirstOrDefault(item => item.state.Id.Equals(_player.CurrentStateId, StringComparison.OrdinalIgnoreCase))
            .index;
        var nextState = _catalog.States[(currentIndex + 1) % _catalog.States.Count].Id;
        await SwitchStateSafelyAsync(nextState);
    }

    private async void AnimationStateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string stateId })
        {
            await SwitchStateSafelyAsync(stateId);
        }
    }

    private void SnapToEdgeMenuItem_Click(object sender, RoutedEventArgs e) => SnapToNearestEdge();

    private void HidePetMenuItem_Click(object sender, RoutedEventArgs e) => Hide();

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
        ToolTip = $"{e.State.DisplayName} · 左键单击切换，拖动后自动贴边，右键查看更多";
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

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(SnapToNearestEdge);
}
