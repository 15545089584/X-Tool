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
        // 电源键脉冲反馈：按下后轻微收缩回弹，作为开关切换的过渡动效。
        if (sender is Button button)
        {
            var transform = new System.Windows.Media.ScaleTransform(1, 1);
            button.RenderTransformOrigin = new Point(0.5, 0.5);
            button.RenderTransform = transform;
            var frames = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
            frames.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.Zero));
            frames.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.86, TimeSpan.FromMilliseconds(90)));
            frames.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(200)));
            transform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, frames);
            transform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, frames);
        }

        CommandRequested?.Invoke(this, new SmartHomeControlRequest(
            device.EntityId,
            device.IsOn ? SmartHomeControlAction.TurnOff : SmartHomeControlAction.TurnOn));
    }

    private void OpenDetails_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenDetail();
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
