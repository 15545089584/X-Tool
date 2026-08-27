using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenshotApp.SmartHome;

public partial class SmartDeviceCard : UserControl
{
    public static readonly DependencyProperty DeviceProperty = DependencyProperty.Register(
        nameof(Device),
        typeof(SmartDeviceViewModel),
        typeof(SmartDeviceCard),
        new PropertyMetadata(null));

    public SmartDeviceCard()
    {
        InitializeComponent();
    }

    public SmartDeviceViewModel? Device
    {
        get => (SmartDeviceViewModel?)GetValue(DeviceProperty);
        set => SetValue(DeviceProperty, value);
    }

    public event Action<object?, SmartHomeControlRequest>? CommandRequested;

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (Device is not { IsAvailable: true } device) return;
        Raise(device.IsOn ? SmartHomeControlAction.TurnOff : SmartHomeControlAction.TurnOn);
    }

    private void BrightnessSlider_Commit(object sender, MouseButtonEventArgs e) =>
        Raise(SmartHomeControlAction.SetBrightness, BrightnessSlider.Value);

    private void BrightnessSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            Raise(SmartHomeControlAction.SetBrightness, BrightnessSlider.Value);
        }
    }

    private void FanSlider_Commit(object sender, MouseButtonEventArgs e) =>
        Raise(SmartHomeControlAction.SetFanPercentage, FanSlider.Value);

    private void FanSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            Raise(SmartHomeControlAction.SetFanPercentage, FanSlider.Value);
        }
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

    private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RaiseSelection(sender, SmartHomeControlAction.SetPreset, Device?.CurrentPresetMode);

    private void RaiseSelection(object sender, SmartHomeControlAction action, string? currentValue)
    {
        if (!IsLoaded || sender is not ComboBox { SelectedValue: string value } ||
            Device is not { IsAvailable: true, IsBusy: false } ||
            string.IsNullOrWhiteSpace(value) || string.Equals(value, currentValue, StringComparison.Ordinal))
        {
            return;
        }

        Raise(action, textValue: value);
    }

    private void Raise(SmartHomeControlAction action, double? value = null, string? textValue = null)
    {
        if (Device is not { IsAvailable: true } device || string.IsNullOrWhiteSpace(device.EntityId)) return;
        CommandRequested?.Invoke(this, new SmartHomeControlRequest(device.EntityId, action, value, textValue));
    }
}
