using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using ScreenshotApp.Capture;

namespace ScreenshotApp.DesktopPet;

/// <summary>提醒时覆盖当前屏幕四边的鼠标穿透彩光，不阻挡用户操作。</summary>
internal sealed class PetAlarmEdgeGlowWindow : Window
{
    private readonly Grid _root;
    private readonly Canvas _mistCanvas;
    private readonly List<GlowMist> _mistBlobs = [];
    private readonly Border _continuousHalo;
    private readonly RotateTransform _haloRotation;
    private readonly Border _edgeTrace;
    private readonly AlarmTopmostPulse _topmostPulse;

    internal PetAlarmEdgeGlowWindow(Rect screenBounds)
    {
        Left = screenBounds.Left;
        Top = screenBounds.Top;
        Width = screenBounds.Width;
        Height = screenBounds.Height;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        _root = new Grid { IsHitTestVisible = false, Opacity = 0, ClipToBounds = true };

        var haloBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            SpreadMethod = GradientSpreadMethod.Reflect
        };
        haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(180, 42, 150, 255), 0));
        haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(190, 61, 216, 255), 0.24));
        haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(176, 91, 125, 255), 0.5));
        haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(190, 53, 202, 255), 0.76));
        haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(180, 42, 150, 255), 1));
        _haloRotation = new RotateTransform(-7, 0.5, 0.5);
        haloBrush.RelativeTransform = _haloRotation;
        _continuousHalo = new Border
        {
            Margin = new Thickness(-10),
            BorderBrush = haloBrush,
            BorderThickness = new Thickness(48),
            CornerRadius = new CornerRadius(24),
            Background = Brushes.Transparent,
            Opacity = 0.36,
            Effect = new BlurEffect { Radius = 27, KernelType = KernelType.Gaussian }
        };
        _root.Children.Add(_continuousHalo);

        _mistCanvas = new Canvas { ClipToBounds = true };
        _root.Children.Add(_mistCanvas);

        // 光斑的大部分位于屏幕外，只让羽化后的蓝青光纹自然漫入画面。
        AddMist(Width * 0.04, -108, 620, 270, Color.FromRgb(48, 158, 255), 0.88, 78, 0, 8200);
        AddMist(Width * 0.39, -94, 760, 250, Color.FromRgb(68, 211, 255), 0.82, -92, 0, 9700);
        AddMist(Width * 0.76, -112, 610, 280, Color.FromRgb(91, 118, 255), 0.8, 70, 0, 8800);
        AddMist(Width * 0.08, Height - 158, 700, 280, Color.FromRgb(57, 198, 255), 0.82, -84, 0, 9400);
        AddMist(Width * 0.48, Height - 174, 780, 300, Color.FromRgb(65, 126, 255), 0.86, 96, 0, 10300);
        AddMist(-112, Height * 0.08, 300, 660, Color.FromRgb(47, 169, 255), 0.84, 0, 78, 9100);
        AddMist(-102, Height * 0.56, 280, 590, Color.FromRgb(68, 216, 255), 0.76, 0, -66, 10600);
        AddMist(Width - 166, Height * 0.12, 300, 650, Color.FromRgb(71, 119, 255), 0.84, 0, -82, 9900);
        AddMist(Width - 174, Height * 0.62, 320, 570, Color.FromRgb(49, 199, 255), 0.78, 0, 70, 8700);

        _edgeTrace = new Border
        {
            Margin = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(148, 78, 177, 255)),
            BorderThickness = new Thickness(1.4),
            CornerRadius = new CornerRadius(16),
            Opacity = 0.62,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(54, 169, 255),
                BlurRadius = 30,
                ShadowDepth = 0,
                Opacity = 0.8
            }
        };
        _root.Children.Add(_edgeTrace);
        Content = _root;
        SourceInitialized += (_, _) => ConfigureNativeWindow();
        _topmostPulse = new AlarmTopmostPulse(ConfigureNativeWindow, TimeSpan.FromMilliseconds(700));
        Closed += (_, _) => _topmostPulse.Dispose();
    }

    internal Task ShowForAsync(TimeSpan duration) => ShowUntilAsync(Task.Delay(duration));

    internal async Task ShowUntilAsync(Task dismissalTask)
    {
        Show();
        ConfigureNativeWindow();
        _topmostPulse.Start();
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        _continuousHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 0.48, TimeSpan.FromMilliseconds(2400))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        _haloRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-7, 7, TimeSpan.FromMilliseconds(7600))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        for (var index = 0; index < _mistBlobs.Count; index++)
        {
            var mist = _mistBlobs[index];
            mist.Translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(
                -mist.DriftX,
                mist.DriftX,
                TimeSpan.FromMilliseconds(mist.DurationMilliseconds))
            {
                BeginTime = TimeSpan.FromMilliseconds(mist.DelayMilliseconds),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
            mist.Translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(
                -mist.DriftY,
                mist.DriftY,
                TimeSpan.FromMilliseconds(mist.DurationMilliseconds * 1.12))
            {
                BeginTime = TimeSpan.FromMilliseconds(mist.DelayMilliseconds + 180),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
            mist.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(
                0.92,
                1.08,
                TimeSpan.FromMilliseconds(mist.DurationMilliseconds * 0.72))
            {
                BeginTime = TimeSpan.FromMilliseconds(mist.DelayMilliseconds),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
            mist.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(
                0.9,
                1.1,
                TimeSpan.FromMilliseconds(mist.DurationMilliseconds * 0.81))
            {
                BeginTime = TimeSpan.FromMilliseconds(mist.DelayMilliseconds + 120),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
            mist.Shape.BeginAnimation(OpacityProperty, new DoubleAnimation(
                mist.BaseOpacity * 0.62,
                mist.BaseOpacity,
                TimeSpan.FromMilliseconds(mist.DurationMilliseconds * 0.48))
            {
                BeginTime = TimeSpan.FromMilliseconds(mist.DelayMilliseconds + 240),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }
        _edgeTrace.BeginAnimation(OpacityProperty, new DoubleAnimation(0.44, 0.7, TimeSpan.FromMilliseconds(1800))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });

        await dismissalTask;
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        await Task.Delay(280);
        _topmostPulse.Stop();
        Close();
    }

    private void AddMist(
        double left,
        double top,
        double width,
        double height,
        Color color,
        double opacity,
        double driftX,
        double driftY,
        double durationMilliseconds)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, color.R, color.G, color.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(218, color.R, color.G, color.B), 0.3));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(112, color.R, color.G, color.B), 0.64));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(34, color.R, color.G, color.B), 0.84));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        var shape = new System.Windows.Shapes.Ellipse
        {
            Width = width,
            Height = height,
            Fill = brush,
            Opacity = opacity,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { scale, translate } },
            Effect = new BlurEffect { Radius = 21, KernelType = KernelType.Gaussian }
        };
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        _mistCanvas.Children.Add(shape);
        _mistBlobs.Add(new GlowMist(
            shape,
            scale,
            translate,
            opacity,
            driftX,
            driftY,
            durationMilliseconds,
            _mistBlobs.Count * 130));
    }

    private void ConfigureNativeWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeWindowNonActivating(handle);
        NativeMethods.MakeWindowMouseTransparent(handle);
        NativeMethods.KeepWindowTopmostWithoutActivating(handle);
    }

    private sealed record GlowMist(
        System.Windows.Shapes.Ellipse Shape,
        ScaleTransform Scale,
        TranslateTransform Translate,
        double BaseOpacity,
        double DriftX,
        double DriftY,
        double DurationMilliseconds,
        double DelayMilliseconds);
}

/// <summary>桌宠隐藏时使用的静默系统风格通知，不调用任何声音 API。</summary>
internal sealed class PetAlarmSystemNotificationWindow : Window
{
    private readonly Border _card;
    private bool _isMouseTransparent = true;
    private TaskCompletionSource<bool>? _dismissed;
    private readonly AlarmTopmostPulse _topmostPulse;

    internal PetAlarmSystemNotificationWindow(PetAlarmTrigger trigger, Rect workingArea)
    {
        Width = 358;
        Height = 126;
        Left = Math.Max(workingArea.Left + 12, workingArea.Right - Width - 18);
        Top = Math.Max(workingArea.Top + 12, workingArea.Bottom - Height - 18);
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Opacity = 0;

        var icon = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromRgb(230, 238, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(183, 203, 247)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "\uE823",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(77, 124, 254)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = trigger.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(41, 70, 95)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = trigger.Detail,
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(99, 124, 147)),
            TextWrapping = TextWrapping.Wrap
        });
        if (trigger.IsDue)
        {
            text.Children.Add(new TextBlock
            {
                Text = "单击此消息结束提醒",
                Margin = new Thickness(0, 6, 0, 0),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(77, 124, 254))
            });
        }
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(icon);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        _card = new Border
        {
            Margin = new Thickness(10),
            Padding = new Thickness(16),
            Background = new SolidColorBrush(Color.FromArgb(248, 248, 251, 255)),
            BorderBrush = CreateRainbowBrush(),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(20),
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(74, 98, 126),
                BlurRadius = 28,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.35
            },
            Child = content
        };
        _card.MouseLeftButtonUp += (_, e) =>
        {
            if (_dismissed is null)
            {
                return;
            }
            e.Handled = true;
            _dismissed.TrySetResult(true);
        };
        Content = _card;
        SourceInitialized += (_, _) => ConfigureNativeWindow();
        _topmostPulse = new AlarmTopmostPulse(ConfigureNativeWindow, TimeSpan.FromMilliseconds(350));
        Closed += (_, _) => _topmostPulse.Dispose();
    }

    internal async Task ShowForAsync(TimeSpan duration)
    {
        ShowNotification();
        await Task.Delay(duration - TimeSpan.FromMilliseconds(260));
        await CloseWithFadeAsync();
    }

    internal async Task ShowUntilClickedAsync()
    {
        _isMouseTransparent = false;
        _dismissed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _card.Cursor = System.Windows.Input.Cursors.Hand;
        _card.ToolTip = "单击结束本次闹钟提醒";
        ShowNotification();
        await _dismissed.Task;
        await CloseWithFadeAsync();
    }

    private void ShowNotification()
    {
        Show();
        ConfigureNativeWindow();
        _topmostPulse.Start();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private async Task CloseWithFadeAsync()
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        await Task.Delay(260);
        _topmostPulse.Stop();
        Close();
    }

    private void ConfigureNativeWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeWindowNonActivating(handle);
        NativeMethods.SetWindowMouseTransparent(handle, _isMouseTransparent);
        NativeMethods.KeepWindowTopmostWithoutActivating(handle);
    }

    private static Brush CreateRainbowBrush()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.25), EndPoint = new Point(1, 0.75) };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(97, 122, 255), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(80, 202, 255), 0.28));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(112, 237, 163), 0.52));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 142, 130), 0.78));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(190, 112, 255), 1));
        return brush;
    }
}

/// <summary>
/// 全屏应用可能在提醒显示后再次占据顶层顺序；定期重申 HWND_TOPMOST，
/// 但始终携带 SWP_NOACTIVATE，避免抢走游戏或视频的键盘焦点。
/// </summary>
internal sealed class AlarmTopmostPulse : IDisposable
{
    private readonly Action _pulse;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private bool _disposed;

    internal AlarmTopmostPulse(Action pulse, TimeSpan interval)
    {
        _pulse = pulse;
        _timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Send)
        {
            Interval = interval
        };
        _timer.Tick += Timer_Tick;
    }

    internal void Start()
    {
        if (_disposed) return;
        PulseNow();
        _timer.Start();
    }

    internal void Stop()
    {
        _timer.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
    }

    private void Timer_Tick(object? sender, EventArgs e) => PulseNow();

    private void PulseNow()
    {
        try
        {
            _pulse();
        }
        catch
        {
            // 提醒置顶失败不应中断闹钟生命周期，下一个周期继续尝试。
        }
    }
}
