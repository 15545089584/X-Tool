using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

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
            if (dialog._subscribedDevice is { } previous)
            {
                previous.PropertyChanged -= dialog.Device_PropertyChanged;
                dialog._subscribedDevice = null;
            }

            dialog.ExitRenameMode();
            dialog.UpdateLightControlsLayout();
            // 设备切换时列表会重新求值并触发 SelectionChanged，短暂屏蔽避免误发控制命令（例如风扇进页响一声）。
            dialog._suppressSelectionChanged = true;
            dialog.Dispatcher.BeginInvoke(new Action(() => dialog._suppressSelectionChanged = false), System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.NewValue is SmartDeviceViewModel newDevice)
            {
                dialog._subscribedDevice = newDevice;
                newDevice.PropertyChanged += dialog.Device_PropertyChanged;
            }

            dialog.UpdateFanSpinState();
        }
    }

    private SmartDeviceViewModel? _subscribedDevice;
    private bool _fanSpinActive;
    private bool _isDraggingAirflowDirection;

    private void Device_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SmartDeviceViewModel.IsOn) or nameof(SmartDeviceViewModel.IsAvailable))
        {
            Dispatcher.BeginInvoke(UpdateFanSpinState);
        }

        if (e.PropertyName is nameof(SmartDeviceViewModel.CurrentAirflowDirectionText) or
            nameof(SmartDeviceViewModel.AirflowDirectionLevels))
        {
            Dispatcher.BeginInvoke(UpdateAirflowDirectionBar);
        }
    }

    /// <summary>风扇叶片旋转随开关状态启停，护网与外环保持静止。</summary>
    private void UpdateFanSpinState()
    {
        var spinning = Device is { IsFan: true, IsOn: true };
        if (spinning == _fanSpinActive) return;
        _fanSpinActive = spinning;
        if (spinning)
        {
            FanBladeRotate.BeginAnimation(RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                });
        }
        else
        {
            FanBladeRotate.BeginAnimation(RotateTransform.AngleProperty, null);
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

    /// <summary>用电统计月份/年份导航（monthOffset：0=本月……；yearOffset：0=今年……）。</summary>
    public event Action<object?, int, int>? InsightsMonthNavigate;

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
    private int _energyMonthOffset;
    private int _energyYearOffset;
    private bool _energyMonthTabIsMonth;
    private bool _isDraggingFanMode;

    private void EnergyDayTab_Click(object sender, RoutedEventArgs e)
    {
        EnergyDayTab.Background = new SolidColorBrush(Colors.White);
        EnergyDayTab.Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x39, 0x4E));
        EnergyMonthTab.Background = Brushes.Transparent;
        EnergyMonthTab.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x75, 0x8C));
        EnergyDailyPanel.Visibility = Visibility.Visible;
        EnergyMonthlyPanel.Visibility = Visibility.Collapsed;
        _energyMonthTabIsMonth = false;
        UpdateEnergyNavLabels();
    }

    private void EnergyMonthTab_Click(object sender, RoutedEventArgs e)
    {
        EnergyMonthTab.Background = new SolidColorBrush(Colors.White);
        EnergyMonthTab.Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x39, 0x4E));
        EnergyDayTab.Background = Brushes.Transparent;
        EnergyDayTab.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x75, 0x8C));
        EnergyDailyPanel.Visibility = Visibility.Collapsed;
        EnergyMonthlyPanel.Visibility = Visibility.Visible;
        _energyMonthTabIsMonth = true;
        UpdateEnergyNavLabels();
        if (_energyYearOffset != 0)
        {
            _energyYearOffset = 0;
            RequestInsights(_energyMonthOffset, 0);
        }
    }

    private void PreviousEnergyPeriod_Click(object sender, RoutedEventArgs e)
    {
        if (_energyMonthTabIsMonth) RequestInsights(_energyMonthOffset, _energyYearOffset + 1);
        else RequestInsights(_energyMonthOffset + 1, _energyYearOffset);
    }

    private void NextEnergyPeriod_Click(object sender, RoutedEventArgs e)
    {
        if (_energyMonthTabIsMonth)
        {
            if (_energyYearOffset > 0) RequestInsights(_energyMonthOffset, _energyYearOffset - 1);
        }
        else if (_energyMonthOffset > 0)
        {
            RequestInsights(_energyMonthOffset - 1, _energyYearOffset);
        }
    }

    private void UpdateEnergyNavLabels()
    {
        EnergyMonthNavLabel.Visibility = _energyMonthTabIsMonth ? Visibility.Collapsed : Visibility.Visible;
        EnergyYearNavLabel.Visibility = _energyMonthTabIsMonth ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RequestInsights(int monthOffset, int yearOffset)
    {
        _energyMonthOffset = monthOffset;
        _energyYearOffset = yearOffset;
        InsightsMonthNavigate?.Invoke(this, monthOffset, yearOffset);
    }

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
        UpdateFanModeBar();
        UpdateAirflowDirectionBar();
    }

    private void AirflowDirectionBar_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateAirflowDirectionBar();

    private void AirflowDirectionBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Device is not { CanInteract: true, HasAirflowDirectionLevels: true } device ||
            device.AirflowDirectionControl is not { IsAvailable: true }) return;
        _isDraggingAirflowDirection = true;
        AirflowDirectionBar.CaptureMouse();
        PreviewAirflowDirectionFromPointer(e.GetPosition(AirflowDirectionBar).Y);
    }

    private void AirflowDirectionBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingAirflowDirection) return;
        PreviewAirflowDirectionFromPointer(e.GetPosition(AirflowDirectionBar).Y);
    }

    private void AirflowDirectionBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingAirflowDirection) return;
        _isDraggingAirflowDirection = false;
        AirflowDirectionBar.ReleaseMouseCapture();
        CommitAirflowDirectionFromBar();
    }

    private SmartAirflowDirectionLevel? AirflowDirectionLevelFromPointer(double y)
    {
        var height = AirflowDirectionBar.ActualHeight;
        if (height <= 0 || Device is not { } device) return null;
        var levels = device.AirflowDirectionLevels;
        if (levels.Count == 0) return null;
        var ratio = Math.Clamp(y / height, 0, 0.999999);
        var index = Math.Clamp((int)Math.Floor(ratio * levels.Count), 0, levels.Count - 1);
        return levels[index];
    }

    private void PreviewAirflowDirectionFromPointer(double y)
    {
        var level = AirflowDirectionLevelFromPointer(y);
        if (level is null) return;
        AirflowDirectionFill.Height = level.Percent / 100d * AirflowDirectionBar.ActualHeight;
        AirflowDirectionFillLabel.SetCurrentValue(TextBlock.TextProperty, level.Name);
    }

    private void CommitAirflowDirectionFromBar()
    {
        var height = AirflowDirectionBar.ActualHeight;
        if (height <= 0 || Device is not { CanInteract: true } device ||
            device.AirflowDirectionControl is not { IsAvailable: true } control) return;
        var levels = device.AirflowDirectionLevels;
        if (levels.Count == 0) return;
        var percent = Math.Clamp(AirflowDirectionFill.Height / height * 100d, 0, 100);
        var level = levels.OrderBy(candidate => Math.Abs(candidate.Percent - percent)).First();
        if (string.Equals(level.Value, control.SelectedValue, StringComparison.Ordinal)) return;
        RaiseForEntity(control.EntityId, SmartHomeControlAction.SelectOption, textValue: level.Value);
    }

    private void UpdateAirflowDirectionBar()
    {
        if (AirflowDirectionBar.ActualHeight <= 0 || Device is not { } device) return;
        var current = device.AirflowDirectionLevels.FirstOrDefault(level => level.IsCurrent);
        if (current is null)
        {
            AirflowDirectionFill.Height = 0;
            AirflowDirectionFillLabel.SetCurrentValue(TextBlock.TextProperty, string.Empty);
            return;
        }

        AirflowDirectionFill.Height = current.Percent / 100d * AirflowDirectionBar.ActualHeight;
        AirflowDirectionFillLabel.SetCurrentValue(TextBlock.TextProperty, current.Name);
    }

    private void FanModeBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFanModeBar();

    private void FanModeBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Device is not { SupportsFanMode: true, CanInteract: true }) return;
        _isDraggingFanMode = true;
        FanModeBar.CaptureMouse();
        PreviewFanModeFromPointer(e.GetPosition(FanModeBar).X);
    }

    private void FanModeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingFanMode) return;
        PreviewFanModeFromPointer(e.GetPosition(FanModeBar).X);
    }

    private void FanModeBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingFanMode) return;
        _isDraggingFanMode = false;
        FanModeBar.ReleaseMouseCapture();
        CommitFanModeFromBar();
    }

    private SmartFanModeLevel? LevelFromPointer(double x)
    {
        var width = FanModeBar.ActualWidth;
        if (width <= 0 || Device is not { SupportsFanMode: true } device) return null;
        var levels = device.FanModeLevels;
        if (levels.Count == 0) return null;
        var ratio = Math.Clamp(x / width, 0, 1);
        var index = Math.Clamp((int)Math.Round(ratio * (levels.Count - 1)), 0, levels.Count - 1);
        return levels[index];
    }

    private void PreviewFanModeFromPointer(double x)
    {
        var level = LevelFromPointer(x);
        if (level is null) return;
        FanModeFill.Width = level.Percent / 100d * FanModeBar.ActualWidth;
        FanModeFillLabel.SetCurrentValue(TextBlock.TextProperty, level.Name);
    }

    private void CommitFanModeFromBar()
    {
        var width = FanModeBar.ActualWidth;
        if (width <= 0 || Device is not { SupportsFanMode: true, IsAvailable: true } device) return;
        var levels = device.FanModeLevels;
        if (levels.Count == 0) return;
        var ratio = Math.Clamp(FanModeFill.Width / width, 0, 1);
        var level = levels[Math.Clamp((int)Math.Round(ratio * (levels.Count - 1)), 0, levels.Count - 1)];
        if (string.Equals(level.Value, device.CurrentFanMode, StringComparison.Ordinal)) return;
        var request = new SmartHomeControlRequest(device.EntityId, SmartHomeControlAction.SetFanMode, null, level.Value);
        device.ApplyOptimistic(request);
        LiveCommandRequested?.Invoke(this, request);
    }

    private void FanAutoMode_Click(object sender, RoutedEventArgs e)
    {
        if (Device is not { SupportsFanMode: true, IsAvailable: true } device || device.FanAutoModeValue is not { } auto) return;
        if (string.Equals(device.CurrentFanMode, auto, StringComparison.Ordinal)) return;
        var request = new SmartHomeControlRequest(device.EntityId, SmartHomeControlAction.SetFanMode, null, auto);
        device.ApplyOptimistic(request);
        LiveCommandRequested?.Invoke(this, request);
    }

    /// <summary>按设备当前 fan_mode 刷新风速条填充与标签；“自动”等非手动档显示为空条。</summary>
    private void UpdateFanModeBar()
    {
        if (FanModeBar.ActualWidth <= 0 || Device is not { SupportsFanMode: true } device) return;
        var current = device.FanModeLevels.FirstOrDefault(level => level.IsCurrent);
        if (current is null)
        {
            FanModeFill.Width = 0;
            FanModeFillLabel.SetCurrentValue(TextBlock.TextProperty, device.CurrentFanModeText);
            return;
        }

        FanModeFill.Width = current.Percent / 100d * FanModeBar.ActualWidth;
        FanModeFillLabel.SetCurrentValue(TextBlock.TextProperty, current.Name);
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

    private void SwingModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RaiseSelection(sender, SmartHomeControlAction.SetSwingMode, Device?.CurrentSwingMode);

    private void FanSwingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        RaiseSelection(sender, SmartHomeControlAction.SetSwingMode, Device?.CurrentSwingMode);
    }

    private void AuxiliarySelectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not Selector
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
