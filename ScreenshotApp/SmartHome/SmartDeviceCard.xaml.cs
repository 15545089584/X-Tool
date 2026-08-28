using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

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

    public event Action<object?, SmartDeviceViewModel>? DetailRequested;

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Device is not { CanInteract: true } device || string.IsNullOrWhiteSpace(device.EntityId)) return;
        CommandRequested?.Invoke(this, new SmartHomeControlRequest(
            device.EntityId,
            device.IsOn ? SmartHomeControlAction.TurnOff : SmartHomeControlAction.TurnOn));
    }

    private void OpenDetails_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenDetail();
    }

    private void CardBrightnessSlider_Commit(object sender, MouseButtonEventArgs e)
    {
        CommitBrightness();
    }

    private void CardBrightnessSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        e.Handled = true;
        CommitBrightness();
    }

    private void CommitBrightness()
    {
        if (Device is not { CanInteract: true, SupportsBrightness: true } device) return;
        CommandRequested?.Invoke(this, new SmartHomeControlRequest(
            device.EntityId,
            SmartHomeControlAction.SetBrightness,
            CardBrightnessSlider.Value));
    }

    private void ColorTemperatureSlider_Commit(object sender, MouseButtonEventArgs e)
    {
        CommitColorTemperature();
    }

    private void ColorTemperatureSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        e.Handled = true;
        CommitColorTemperature();
    }

    private void CommitColorTemperature()
    {
        if (Device is not { CanInteract: true, SupportsColorTemperature: true } device) return;
        CommandRequested?.Invoke(this, new SmartHomeControlRequest(
            device.EntityId,
            SmartHomeControlAction.SetColorTemperature,
            device.ToColorTemperatureKelvin(ColorTemperatureSlider.Value)));
    }

    private void CardSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindInteractiveAncestor(e.OriginalSource as DependencyObject)) return;
        OpenDetail();
    }

    private void CardSurface_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        OpenDetail();
    }

    private void OpenDetail()
    {
        if (Device is { } device) DetailRequested?.Invoke(this, device);
    }

    private static bool FindInteractiveAncestor(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or Slider or ComboBox) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
