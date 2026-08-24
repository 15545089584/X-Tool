using System.Windows;
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
    private bool _initialized;
    private bool _closed;

    internal DesktopPetWindow()
    {
        InitializeComponent();
        _catalog = DesktopPetAnimationCatalog.LoadDefault();
        Width = _catalog.DisplaySize;
        Height = _catalog.DisplaySize;
        PetImage.Width = _catalog.DisplaySize;
        PetImage.Height = _catalog.DisplaySize;
        _player = new DesktopPetAnimationPlayer(PetImage, _catalog);
        _player.StateChanged += Player_StateChanged;
        Loaded += DesktopPetWindow_Loaded;
        IsVisibleChanged += DesktopPetWindow_IsVisibleChanged;
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
    }

    internal event EventHandler? PetVisibilityChanged;

    internal string? CurrentStateId => _player.CurrentStateId;

    internal int CurrentFrameIndex => _player.CurrentFrameIndex;

    internal long CachedDecodedBytes => _player.CachedDecodedBytes;

    internal Task SwitchStateForValidationAsync(string stateId, CancellationToken cancellationToken) =>
        _player.SwitchStateAsync(stateId, cancellationToken);

    internal Rect GetCurrentWorkingAreaForValidation() => GetCurrentWorkingArea();

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

        var nextState = _player.CurrentStateId?.Equals("state-01", StringComparison.OrdinalIgnoreCase) == true
            ? "state-06"
            : "state-01";
        await SwitchStateSafelyAsync(nextState);
    }

    private async void State01MenuItem_Click(object sender, RoutedEventArgs e) =>
        await SwitchStateSafelyAsync("state-01");

    private async void State06MenuItem_Click(object sender, RoutedEventArgs e) =>
        await SwitchStateSafelyAsync("state-06");

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
        State01MenuItem.IsChecked = e.State.Id.Equals("state-01", StringComparison.OrdinalIgnoreCase);
        State06MenuItem.IsChecked = e.State.Id.Equals("state-06", StringComparison.OrdinalIgnoreCase);
        ToolTip = $"{e.State.DisplayName} · 左键单击切换，拖动后自动贴边，右键查看更多";
    }

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
