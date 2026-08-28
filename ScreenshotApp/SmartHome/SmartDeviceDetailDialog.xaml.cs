using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ScreenshotApp.SmartHome;

public partial class SmartDeviceDetailDialog : UserControl
{
    public static readonly DependencyProperty DeviceProperty = DependencyProperty.Register(
        nameof(Device),
        typeof(SmartDeviceViewModel),
        typeof(SmartDeviceDetailDialog),
        new PropertyMetadata(null, OnDeviceChanged));

    public SmartDeviceDetailDialog()
    {
        InitializeComponent();
    }

    private static void OnDeviceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SmartDeviceDetailDialog dialog)
        {
            dialog.ExitRenameMode();
            dialog.UpdateLightControlsLayout();
            // 设备切换时列表会重新求值并触发 SelectionChanged，短暂屏蔽避免误发控制命令（例如风扇进页响一声）。
            dialog._suppressSelectionChanged = true;
            dialog.Dispatcher.BeginInvoke(new Action(() => dialog._suppressSelectionChanged = false), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    public SmartDeviceViewModel? Device
    {
        get => (SmartDeviceViewModel?)GetValue(DeviceProperty);
        set => SetValue(DeviceProperty, value);
    }

    public event Action<object?, SmartHomeControlRequest>? CommandRequested;

    /// <summary>拖动过程中的实时控制命令；绕过 IsBusy 防抖，由视图直接下发 Home Assistant。</summary>
    public event Action<object?, SmartHomeControlRequest>? LiveCommandRequested;

    public event Action<object?, string>? RenameRequested;

    public event EventHandler? CloseRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Device is not { CanRename: true } device) return;
        RenameBox.Text = device.Name;
        TitleRow.Visibility = Visibility.Collapsed;
        RenameRow.Visibility = Visibility.Visible;
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    private void ConfirmRename_Click(object sender, RoutedEventArgs e)
    {
        if (Device is not { CanRename: true } device) return;
        var newName = RenameBox.Text.Trim();
        ExitRenameMode();
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, device.Name, StringComparison.Ordinal)) return;
        RenameRequested?.Invoke(this, newName);
    }

    private void CancelRename_Click(object sender, RoutedEventArgs e) => ExitRenameMode();

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ConfirmRename_Click(sender, e);
        else if (e.Key == Key.Escape) CancelRename_Click(sender, e);
    }

    private void ExitRenameMode()
    {
        RenameRow.Visibility = Visibility.Collapsed;
        TitleRow.Visibility = Visibility.Visible;
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (Device is not { CanInteract: true } device) return;
        Raise(device.IsOn ? SmartHomeControlAction.TurnOff : SmartHomeControlAction.TurnOn);
    }

    private bool _isDraggingBrightness;
    private bool _isDraggingColorTemp;
    private double _pendingColorTempPercent;
    private double _pendingIndicatorY = double.NaN;
    private DateTimeOffset _lastLiveSend = DateTimeOffset.MinValue;
    private double _lastSentBrightness = -1;
    private double _lastSentKelvin = -1;
    private bool _suppressSelectionChanged;

    private void BrightnessBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateBrightnessFill();

    private void BrightnessBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Device is not { SupportsBrightness: true, CanInteract: true }) return;
        _isDraggingBrightness = true;
        _lastSentBrightness = -1;
        _lastLiveSend = DateTimeOffset.MinValue;
        BrightnessBar.CaptureMouse();
        UpdateBrightnessFromPointer(e.GetPosition(BrightnessBar).X);
    }

    private void BrightnessBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingBrightness || BrightnessBar.ActualWidth <= 0) return;
        UpdateBrightnessFromPointer(e.GetPosition(BrightnessBar).X);
        var percent = Math.Clamp(BrightnessFill.Width / BrightnessBar.ActualWidth * 100d, 0, 100);
        TrySendLiveBrightness(percent, force: false);
    }

    private void BrightnessBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingBrightness) return;
        _isDraggingBrightness = false;
        BrightnessBar.ReleaseMouseCapture();
        if (BrightnessBar.ActualWidth <= 0) return;
        var percent = Math.Clamp(BrightnessFill.Width / BrightnessBar.ActualWidth * 100d, 0, 100);
        TrySendLiveBrightness(percent, force: true);
    }

    /// <summary>拖动期间节流下发亮度：本地乐观值即时刷新文字，实际命令经 LiveCommandRequested 直达。</summary>
    private void TrySendLiveBrightness(double percent, bool force)
    {
        if (Device is not { SupportsBrightness: true, IsAvailable: true } device) return;
        if (!force)
        {
            if (Math.Abs(percent - _lastSentBrightness) < 1.0) return;
            if (DateTimeOffset.Now - _lastLiveSend < TimeSpan.FromMilliseconds(150)) return;
        }

        _lastLiveSend = DateTimeOffset.Now;
        _lastSentBrightness = percent;
        var request = new SmartHomeControlRequest(device.EntityId, SmartHomeControlAction.SetBrightness, percent);
        device.ApplyOptimistic(request);
        LiveCommandRequested?.Invoke(this, request);
    }

    private void UpdateBrightnessFromPointer(double x)
    {
        var width = BrightnessBar.ActualWidth;
        if (width <= 0) return;
        BrightnessFill.Width = Math.Clamp(x, 0, width);
    }

    private void UpdateBrightnessFill()
    {
        if (BrightnessBar.ActualWidth <= 0 || Device is not { SupportsBrightness: true } device) return;
        BrightnessFill.Width = Math.Clamp(device.BrightnessPercent / 100d, 0, 1) * BrightnessBar.ActualWidth;
    }

    private void ColorTempPalette_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateColorTempIndicator();

    private void ColorTempPalette_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Device is not { SupportsColorTemperature: true, CanInteract: true }) return;
        _isDraggingColorTemp = true;
        _lastSentKelvin = -1;
        _lastLiveSend = DateTimeOffset.MinValue;
        ColorTempPalette.CaptureMouse();
        var position = e.GetPosition(ColorTempPalette);
        UpdateColorTempFromPointer(position.X, position.Y);
    }

    private void ColorTempPalette_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingColorTemp || ColorTempPalette.ActualWidth <= 0) return;
        if (Device is not { SupportsColorTemperature: true } device) return;
        var position = e.GetPosition(ColorTempPalette);
        UpdateColorTempFromPointer(position.X, position.Y);
        TrySendLiveColorTemperature(device, device.ToColorTemperatureKelvin(_pendingColorTempPercent), force: false);
    }

    private void ColorTempPalette_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingColorTemp) return;
        _isDraggingColorTemp = false;
        ColorTempPalette.ReleaseMouseCapture();
        if (Device is not { SupportsColorTemperature: true } device) return;
        TrySendLiveColorTemperature(device, device.ToColorTemperatureKelvin(_pendingColorTempPercent), force: true);
    }

    private void TrySendLiveColorTemperature(SmartDeviceViewModel device, double kelvin, bool force)
    {
        if (device is not { SupportsColorTemperature: true, IsAvailable: true }) return;
        if (!force)
        {
            if (Math.Abs(kelvin - _lastSentKelvin) < 40) return;
            if (DateTimeOffset.Now - _lastLiveSend < TimeSpan.FromMilliseconds(150)) return;
        }

        _lastLiveSend = DateTimeOffset.Now;
        _lastSentKelvin = kelvin;
        var request = new SmartHomeControlRequest(device.EntityId, SmartHomeControlAction.SetColorTemperature, kelvin);
        device.ApplyOptimistic(request);
        LiveCommandRequested?.Invoke(this, request);
    }

    private void UpdateColorTempFromPointer(double x, double y)
    {
        var width = ColorTempPalette.ActualWidth;
        var height = ColorTempPalette.ActualHeight;
        if (width <= 0 || height <= 0) return;
        _pendingColorTempPercent = Math.Clamp(x / width * 100d, 0, 100);
        _pendingIndicatorY = Math.Clamp(y, 13, height - 13);
        PositionColorTempIndicator(_pendingColorTempPercent, _pendingIndicatorY);
    }

    private void UpdateColorTempIndicator()
    {
        if (ColorTempPalette.ActualWidth <= 0 || Device is not { SupportsColorTemperature: true } device) return;
        _pendingColorTempPercent = device.ColorTemperaturePercent;
        PositionColorTempIndicator(_pendingColorTempPercent, null);
    }

    private void PositionColorTempIndicator(double percent, double? y)
    {
        var width = ColorTempPalette.ActualWidth;
        if (width <= 0) return;
        var x = Math.Clamp(percent / 100d, 0, 1) * width;
        var top = y ?? ColorTempPalette.ActualHeight / 2;
        ColorTempIndicator.Margin = new Thickness(x - 13, top - 13, 0, 0);
    }

    private void UpdateLightControlsLayout()
    {
        UpdateBrightnessFill();
        UpdateColorTempIndicator();
    }

    private void FanPresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        RaiseSelection(sender, SmartHomeControlAction.SetPreset, Device?.CurrentPresetMode);
    }

    private void FanLevelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (sender is not ListBox { SelectedValue: double percent } ||
            Device is not { IsAvailable: true } device) return;
        if (Math.Abs(percent - device.FanPercentage) < 0.5) return;
        var request = new SmartHomeControlRequest(device.EntityId, SmartHomeControlAction.SetFanPercentage, percent);
        device.ApplyOptimistic(request);
        LiveCommandRequested?.Invoke(this, request);
    }

    private void CoverSlider_Commit(object sender, MouseButtonEventArgs e) =>
        Raise(SmartHomeControlAction.SetCoverPosition, CoverSlider.Value);

    private void CoverSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            Raise(SmartHomeControlAction.SetCoverPosition, CoverSlider.Value);
        }
    }

    private void DecreaseTemperature_Click(object sender, RoutedEventArgs e)
    {
        if (Device is not { } device) return;
        Raise(SmartHomeControlAction.SetTargetTemperature,
            Math.Max(device.MinimumTemperature, device.TargetTemperature - device.TemperatureStep));
    }

    private void IncreaseTemperature_Click(object sender, RoutedEventArgs e)
    {
        if (Device is not { } device) return;
        Raise(SmartHomeControlAction.SetTargetTemperature,
            Math.Min(device.MaximumTemperature, device.TargetTemperature + device.TemperatureStep));
    }

    private void CoverAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) return;
        Raise(action switch
        {
            "Open" => SmartHomeControlAction.OpenCover,
            "Stop" => SmartHomeControlAction.StopCover,
            _ => SmartHomeControlAction.CloseCover
        });
    }

    private void HvacModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RaiseSelection(sender, SmartHomeControlAction.SetHvacMode, Device?.CurrentHvacMode);

    private void FanModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RaiseSelection(sender, SmartHomeControlAction.SetFanMode, Device?.CurrentFanMode);

    private void SwingModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RaiseSelection(sender, SmartHomeControlAction.SetSwingMode, Device?.CurrentSwingMode);

    private void FanSwingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        RaiseSelection(sender, SmartHomeControlAction.SetSwingMode, Device?.CurrentSwingMode);
    }

    private void AuxiliarySelectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox
            {
                DataContext: SmartSelectControl control,
                SelectedValue: string value
            } || Device is not { CanInteract: true } || string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, control.SelectedValue, StringComparison.Ordinal))
        {
            return;
        }

        RaiseForEntity(control.EntityId, SmartHomeControlAction.SelectOption, textValue: value);
    }

    private void FeatureToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SmartFeatureOption feature } ||
            Device is not { CanInteract: true }) return;
        RaiseForEntity(
            feature.EntityId,
            feature.IsOn ? SmartHomeControlAction.TurnOff : SmartHomeControlAction.TurnOn);
    }

    private void RaiseSelection(object sender, SmartHomeControlAction action, string? currentValue)
    {
        if (!IsLoaded || sender is not Selector { SelectedValue: string value } ||
            Device is not { CanInteract: true } || string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, currentValue, StringComparison.Ordinal))
        {
            return;
        }

        Raise(action, textValue: value);
    }

    private void Raise(SmartHomeControlAction action, double? value = null, string? textValue = null)
    {
        if (Device is not { CanInteract: true } device || string.IsNullOrWhiteSpace(device.EntityId)) return;
        var effectiveValue = action == SmartHomeControlAction.SetTargetTemperature && value is not null
            ? device.ToSourceTemperature(value.Value)
            : value;
        RaiseForEntity(device.EntityId, action, effectiveValue, textValue);
    }

    private void RaiseForEntity(
        string entityId,
        SmartHomeControlAction action,
        double? value = null,
        string? textValue = null)
    {
        if (Device is not { CanInteract: true } || string.IsNullOrWhiteSpace(entityId)) return;
        CommandRequested?.Invoke(this, new SmartHomeControlRequest(entityId, action, value, textValue));
    }
}
