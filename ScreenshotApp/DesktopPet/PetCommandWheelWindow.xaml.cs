using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;

namespace ScreenshotApp.DesktopPet;

/// <summary>单击桌面宠物时显示的主动功能轮盘，与文件拖拽轮盘完全独立。</summary>
public partial class PetCommandWheelWindow : Window
{
    private const int SectorCount = 4;
    private const int ShelfSectorIndex = 1;
    private const int AlarmSectorIndex = 2;
    private const int CalendarSectorIndex = 0;
    private const int MailSectorIndex = 3;
    private readonly List<CommandSectorVisual> _sectors = [];
    private readonly Rect _workingArea;
    private readonly Rect _petBounds;
    private Point _wheelCenter;
    private Point _petCenterOnScreen;
    private double _innerRadius;
    private double _outerRadius;
    private double _fanStartAngle;
    private double _fanCenterAngle;
    private double _sectorSweepAngle;
    private int _selectedSectorIndex = -1;
    private TextBlock? _shelfCountText;
    private TextBlock? _alarmCountText;
    private bool _isTransitioning;

    internal PetCommandWheelWindow(Rect petBounds, Rect workingArea, int shelfItemCount, int alarmCount)
    {
        InitializeComponent();
        _workingArea = workingArea;
        _petBounds = petBounds;
        ConfigureLayout(petBounds, workingArea);
        BuildWheel(shelfItemCount, alarmCount);
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            PlayOpenAnimation();
        };
    }

    internal event Action? ShelfRequested;

    internal event Action? AlarmRequested;
    internal event Action? CalendarRequested;
    internal event Action? MailRequested;

    internal event Action? DismissRequested;

    internal static int ShelfCommandIndex => ShelfSectorIndex;

    internal static int AlarmCommandIndex => AlarmSectorIndex;

    internal bool KeepOpenForCompanion { get; set; }

    internal void UpdateShelfCount(int count)
    {
        if (_shelfCountText is null)
        {
            return;
        }

        _shelfCountText.Text = count > 99 ? "99+" : count.ToString();
        _shelfCountText.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void UpdateAlarmCount(int count)
    {
        if (_alarmCountText is null)
        {
            return;
        }

        _alarmCountText.Text = count > 99 ? "99+" : count.ToString();
        _alarmCountText.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal Rect CalculatePanelBounds(double panelWidth, double panelHeight)
    {
        var left = _petCenterOnScreen.X - panelWidth / 2;
        var top = _petBounds.Top - panelHeight - 12;
        left = Math.Clamp(left, _workingArea.Left + 8, Math.Max(_workingArea.Left + 8, _workingArea.Right - panelWidth - 8));
        if (top < _workingArea.Top + 8)
        {
            top = Math.Min(_workingArea.Bottom - panelHeight - 8, _petBounds.Bottom + 12);
        }
        top = Math.Clamp(top, _workingArea.Top + 8, Math.Max(_workingArea.Top + 8, _workingArea.Bottom - panelHeight - 8));
        return new Rect(left, top, panelWidth, panelHeight);
    }

    internal Point CalculateTransitionAnchor(Rect targetPanelBounds) => new(
        Math.Clamp(
            _petCenterOnScreen.X - 8,
            _workingArea.Left + 79,
            _workingArea.Right - 79),
        Math.Clamp(
            _petBounds.Top - 92,
            targetPanelBounds.Top + 36,
            targetPanelBounds.Bottom - 36));

    /// <summary>让未选扇区退场，并把实际点击的扇区沿自身圆弧移动到宠物正上方。</summary>
    internal async Task PlaySelectionTransitionAsync(int selectedSectorIndex, Rect targetPanelBounds)
    {
        if (_isTransitioning || selectedSectorIndex < 0 || _sectors.Count <= selectedSectorIndex ||
            !_sectors[selectedSectorIndex].Enabled)
        {
            return;
        }

        _isTransitioning = true;
        KeepOpenForCompanion = true;
        WheelRoot.IsHitTestVisible = false;
        var selectedSector = _sectors[selectedSectorIndex];
        var fadeDuration = TimeSpan.FromMilliseconds(180);
        var fadeEasing = new CubicEase { EasingMode = EasingMode.EaseIn };
        for (var index = 0; index < _sectors.Count; index++)
        {
            if (index == selectedSectorIndex)
            {
                continue;
            }

            var sector = _sectors[index];
            sector.Path.BeginAnimation(OpacityProperty, new DoubleAnimation(sector.Path.Opacity, 0, fadeDuration)
            {
                EasingFunction = fadeEasing
            });
            sector.Content.BeginAnimation(OpacityProperty, new DoubleAnimation(sector.Content.Opacity, 0, fadeDuration)
            {
                EasingFunction = fadeEasing
            });
        }

        await Task.Delay(fadeDuration);

        // 面板靠近屏幕边缘时会被整体向左收拢，但扇形的落点仍应对准宠物正上方，
        // 不能跟随面板中心一起偏到左侧。Y 轴落在面板右下侧，形成“先转到位，再展开”的锚点。
        var targetCenterOnScreen = CalculateTransitionAnchor(targetPanelBounds);
        var targetCenter = new Point(targetCenterOnScreen.X - Left, targetCenterOnScreen.Y - Top);
        var moveDuration = TimeSpan.FromMilliseconds(720);

        // Path 使用轮盘绝对坐标，文字则由 Canvas 定位；必须放入同一图层后再旋转，
        // 否则两者的局部中心不同，会在转动过程中彼此错位。
        WheelCanvas.Children.Remove(selectedSector.Path);
        WheelCanvas.Children.Remove(selectedSector.Content);
        var selectedLayer = new Canvas
        {
            Width = Width,
            Height = Height,
            IsHitTestVisible = false
        };
        selectedLayer.Children.Add(selectedSector.Path);
        selectedLayer.Children.Add(selectedSector.Content);
        WheelCanvas.Children.Add(selectedLayer);

        var layerScale = new ScaleTransform(1, 1, selectedSector.Center.X, selectedSector.Center.Y);
        var layerRotate = new RotateTransform(0, selectedSector.Center.X, selectedSector.Center.Y);
        var layerTranslate = new TranslateTransform();
        selectedLayer.RenderTransform = new TransformGroup
        {
            Children = { layerScale, layerRotate, layerTranslate }
        };

        var currentOffset = selectedSector.Center - _wheelCenter;
        var targetOffset = targetCenter - _wheelCenter;
        var currentAngle = Math.Atan2(currentOffset.Y, currentOffset.X) * 180 / Math.PI;
        var targetAngle = Math.Atan2(targetOffset.Y, targetOffset.X) * 180 / Math.PI;
        var rotationDelta = NormalizeSignedAngle(targetAngle - currentAngle);

        // 逐帧按同一个角度参数计算圆弧位置，避免两个关键帧在交界处各自减速造成“卡一下”。
        // 半径只在整段圆弧中连续插值，因此即使目标点比原轮盘半径更远，也会形成平滑的外扩弧线。
        await AnimateOrbitAsync(
            layerTranslate,
            layerRotate,
            selectedSector.Center,
            currentAngle,
            currentOffset.Length,
            targetOffset.Length,
            rotationDelta,
            moveDuration);

        // 扇形抵达宠物正上方后立即开始压缩，不再插入会被误认为卡顿的静止停顿。
        var morphDuration = TimeSpan.FromMilliseconds(480);
        var morphEasing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var morphCard = new Border
        {
            Width = 142,
            Height = 58,
            CornerRadius = new CornerRadius(18),
            Background = CreateSectorBrush(enabled: true, selected: true),
            BorderBrush = new SolidColorBrush(Color.FromArgb(225, 143, 128, 245)),
            BorderThickness = new Thickness(1.6),
            Effect = CreateGlassShadow(enabled: true, selected: true),
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        var morphScale = new ScaleTransform(0.58, 0.72);
        morphCard.RenderTransform = morphScale;
        Canvas.SetLeft(morphCard, targetCenter.X - morphCard.Width / 2);
        Canvas.SetTop(morphCard, targetCenter.Y - morphCard.Height / 2);
        WheelCanvas.Children.Add(morphCard);

        layerScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 1.46, morphDuration) { EasingFunction = morphEasing });
        layerScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.54, morphDuration) { EasingFunction = morphEasing });
        selectedLayer.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0.06, morphDuration) { EasingFunction = morphEasing });
        morphCard.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 0.94, morphDuration) { EasingFunction = morphEasing });
        morphScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.58, 1, morphDuration) { EasingFunction = morphEasing });
        morphScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.72, 1, morphDuration) { EasingFunction = morphEasing });

        await Task.Delay(morphDuration);
    }

    private Task AnimateOrbitAsync(
        TranslateTransform translate,
        RotateTransform rotate,
        Point startCenter,
        double startAngle,
        double startRadius,
        double targetRadius,
        double rotationDelta,
        TimeSpan duration)
    {
        var completion = new TaskCompletionSource<bool>();
        var startedAt = Stopwatch.GetTimestamp();
        EventHandler? renderingHandler = null;
        renderingHandler = (_, _) =>
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var rawProgress = Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            var progress = EaseInOutCubic(rawProgress);
            var angle = (startAngle + rotationDelta * progress) * Math.PI / 180;
            var radius = startRadius + (targetRadius - startRadius) * progress;
            var center = new Point(
                _wheelCenter.X + Math.Cos(angle) * radius,
                _wheelCenter.Y + Math.Sin(angle) * radius);

            translate.X = center.X - startCenter.X;
            translate.Y = center.Y - startCenter.Y;
            rotate.Angle = rotationDelta * progress;

            if (rawProgress < 1)
            {
                return;
            }

            CompositionTarget.Rendering -= renderingHandler;
            completion.TrySetResult(true);
        };

        CompositionTarget.Rendering += renderingHandler;
        return completion.Task;
    }

    private static double EaseInOutCubic(double progress) => progress < 0.5
        ? 4 * progress * progress * progress
        : 1 - Math.Pow(-2 * progress + 2, 3) / 2;

    private void ConfigureLayout(Rect petBounds, Rect workingArea)
    {
        _petCenterOnScreen = new Point(petBounds.Left + petBounds.Width / 2, petBounds.Top + petBounds.Height / 2);
        _innerRadius = Math.Clamp(petBounds.Width * 0.42, 92, 150);
        var layout = ResolveFanLayout(petBounds, workingArea);
        var standardOuterRadius = _innerRadius + Math.Clamp(petBounds.Width * 0.34, 88, 104);
        _outerRadius = layout.IsCorner
            ? Math.Sqrt(
                _innerRadius * _innerRadius +
                (standardOuterRadius * standardOuterRadius - _innerRadius * _innerRadius) * 168 / layout.SweepAngle)
            : standardOuterRadius;
        _fanCenterAngle = layout.CenterAngle;
        _fanStartAngle = layout.CenterAngle - layout.SweepAngle / 2;
        _sectorSweepAngle = layout.SweepAngle / SectorCount;

        var layoutRadius = _outerRadius + 92;
        var desiredBounds = new Rect(
            _petCenterOnScreen.X - layoutRadius,
            _petCenterOnScreen.Y - layoutRadius,
            layoutRadius * 2,
            layoutRadius * 2);
        var visibleBounds = Rect.Intersect(desiredBounds, workingArea);
        Left = visibleBounds.Left;
        Top = visibleBounds.Top;
        Width = Math.Max(1, visibleBounds.Width);
        Height = Math.Max(1, visibleBounds.Height);
        _wheelCenter = new Point(_petCenterOnScreen.X - Left, _petCenterOnScreen.Y - Top);
    }

    private void BuildWheel(int shelfItemCount, int alarmCount)
    {
        WheelCanvas.Children.Clear();
        _sectors.Clear();
        for (var index = 0; index < SectorCount; index++)
        {
            var startAngle = _fanStartAngle + index * _sectorSweepAngle + 1.2;
            var endAngle = _fanStartAngle + (index + 1) * _sectorSweepAngle - 1.2;
            var enabled = index is ShelfSectorIndex or AlarmSectorIndex or CalendarSectorIndex or MailSectorIndex;
            var path = new ShapePath
            {
                Data = CreateSectorGeometry(_wheelCenter, _innerRadius, _outerRadius, startAngle, endAngle),
                Fill = CreateSectorBrush(enabled, selected: false),
                Stroke = new SolidColorBrush(enabled
                    ? Color.FromArgb(215, 143, 128, 245)
                    : Color.FromArgb(150, 190, 214, 232)),
                StrokeThickness = enabled ? 1.6 : 1.1,
                Effect = CreateGlassShadow(enabled, selected: false),
                Opacity = enabled ? 1 : 0.62,
                IsHitTestVisible = false
            };
            WheelCanvas.Children.Add(path);

            var middleAngle = (startAngle + endAngle) / 2;
            var labelCenter = PointOnCircle(_wheelCenter, (_innerRadius + _outerRadius) / 2, middleAngle);
            var content = CreateSectorContent(index, shelfItemCount, alarmCount);
            Canvas.SetLeft(content, labelCenter.X - content.Width / 2);
            Canvas.SetTop(content, labelCenter.Y - content.Height / 2);
            WheelCanvas.Children.Add(content);
            _sectors.Add(new CommandSectorVisual(path, content, enabled, labelCenter));
        }
    }

    private FrameworkElement CreateSectorContent(int sectorIndex, int shelfItemCount, int alarmCount)
    {
        var isShelf = sectorIndex == ShelfSectorIndex;
        var isAlarm = sectorIndex == AlarmSectorIndex;
        var isCalendar = sectorIndex == CalendarSectorIndex;
        var enabled = isShelf || isAlarm || isCalendar || sectorIndex == MailSectorIndex;
        var iconContainer = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        iconContainer.Children.Add(new TextBlock
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = enabled ? 23 : 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(enabled ? Color.FromRgb(104, 87, 216) : Color.FromRgb(126, 148, 168)),
            Text = isShelf ? "\uE8B7" : isAlarm ? "\uE823" : isCalendar ? "\uE787" : "\uE715",
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = Colors.White,
                BlurRadius = 5,
                ShadowDepth = 0,
                Opacity = 0.82
            }
        });

        if (isShelf)
        {
            _shelfCountText = new TextBlock
            {
                MinWidth = 17,
                Height = 17,
                Padding = new Thickness(4, 1, 4, 0),
                Margin = new Thickness(22, -8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                TextAlignment = TextAlignment.Center,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(77, 124, 254)),
                Text = shelfItemCount > 99 ? "99+" : shelfItemCount.ToString(),
                Visibility = shelfItemCount > 0 ? Visibility.Visible : Visibility.Collapsed
            };
            iconContainer.Children.Add(_shelfCountText);
        }
        else if (isAlarm)
        {
            _alarmCountText = CreateCountBadge(alarmCount);
            iconContainer.Children.Add(_alarmCountText);
        }

        var text = new TextBlock
        {
            Margin = new Thickness(0, 5, 0, 0),
            FontSize = enabled ? 11.5 : 9.5,
            FontWeight = enabled ? FontWeights.SemiBold : FontWeights.Medium,
            Foreground = new SolidColorBrush(enabled ? Color.FromRgb(71, 65, 139) : Color.FromRgb(117, 139, 158)),
            Text = isShelf ? "文件暂存区" : isAlarm ? "静默闹钟" : isCalendar ? "手机日程" : "邮箱中心",
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        var content = new Grid
        {
            Width = 88,
            Height = 58,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        content.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { iconContainer, text }
        });
        ToolTipService.SetToolTip(content, isShelf
            ? "打开本次运行的文件暂存区"
            : isAlarm ? "设置无声音的桌宠消息闹钟" : isCalendar ? "查看已同步的手机日程" : "查看多个邮箱的新邮件");
        return content;
    }

    private static TextBlock CreateCountBadge(int count) => new()
    {
        MinWidth = 17,
        Height = 17,
        Padding = new Thickness(4, 1, 4, 0),
        Margin = new Thickness(22, -8, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        TextAlignment = TextAlignment.Center,
        FontSize = 9,
        FontWeight = FontWeights.Bold,
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromRgb(77, 124, 254)),
        Text = count > 99 ? "99+" : count.ToString(),
        Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed
    };

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isTransitioning)
        {
            SetSelectedSector(ResolveSectorIndex(e.GetPosition(WheelCanvas)));
        }
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isTransitioning)
        {
            e.Handled = true;
            return;
        }

        var selectedIndex = ResolveSectorIndex(e.GetPosition(WheelCanvas));
        if (selectedIndex == MailSectorIndex)
        {
            MailRequested?.Invoke();
            e.Handled = true;
            return;
        }
        if (selectedIndex == CalendarSectorIndex)
        {
            CalendarRequested?.Invoke();
            e.Handled = true;
            return;
        }
        if (selectedIndex == ShelfSectorIndex)
        {
            SetSelectedSector(selectedIndex);
            ShelfRequested?.Invoke();
            e.Handled = true;
            return;
        }

        if (selectedIndex == AlarmSectorIndex)
        {
            SetSelectedSector(selectedIndex);
            AlarmRequested?.Invoke();
            e.Handled = true;
            return;
        }

        DismissRequested?.Invoke();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DismissRequested?.Invoke();
        }
    }

    private async void Window_Deactivated(object? sender, EventArgs e)
    {
        if (KeepOpenForCompanion)
        {
            return;
        }

        // 透明顶层窗口切换焦点时，Deactivated 可能早于同一次点击的 MouseUp 到达。
        // 短暂等待后再复核，可避免选中扇区的点击被误判为外部点击。
        await Task.Delay(180);
        if (!IsActive && !KeepOpenForCompanion && !_isTransitioning)
        {
            DismissRequested?.Invoke();
        }
    }

    private int ResolveSectorIndex(Point point)
    {
        var offsetX = point.X - _wheelCenter.X;
        var offsetY = point.Y - _wheelCenter.Y;
        var distance = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
        if (distance < _innerRadius || distance > _outerRadius)
        {
            return -1;
        }

        var angle = NormalizeAngle(Math.Atan2(offsetY, offsetX) * 180 / Math.PI);
        var start = NormalizeAngle(_fanStartAngle);
        var relative = NormalizeAngle(angle - start);
        var totalSweep = _sectorSweepAngle * SectorCount;
        if (relative >= totalSweep)
        {
            return -1;
        }

        return Math.Clamp((int)(relative / _sectorSweepAngle), 0, SectorCount - 1);
    }

    private void SetSelectedSector(int selectedIndex)
    {
        if (_selectedSectorIndex == selectedIndex)
        {
            return;
        }

        _selectedSectorIndex = selectedIndex;
        for (var index = 0; index < _sectors.Count; index++)
        {
            var sector = _sectors[index];
            var selected = index == selectedIndex;
            sector.Path.Fill = CreateSectorBrush(sector.Enabled, selected);
            sector.Path.Effect = CreateGlassShadow(sector.Enabled, selected);
            sector.Path.Opacity = selected ? 1 : sector.Enabled ? 0.95 : 0.58;
            sector.Content.RenderTransform = new ScaleTransform(selected ? 1.12 : 1, selected ? 1.12 : 1);
        }
    }

    private void PlayOpenAnimation()
    {
        WheelRoot.RenderTransformOrigin = new Point(
            Width <= 0 ? 0.5 : _wheelCenter.X / Width,
            Height <= 0 ? 0.5 : _wheelCenter.Y / Height);
        var scale = new ScaleTransform(0.84, 0.84);
        WheelRoot.RenderTransform = scale;
        WheelRoot.Opacity = 0;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        WheelRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.84, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.84, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = easing });
    }

    private static FanLayout ResolveFanLayout(Rect petBounds, Rect workingArea)
    {
        const double edgeTolerance = 30;
        var touchesLeft = Math.Abs(petBounds.Left - workingArea.Left) <= edgeTolerance;
        var touchesRight = Math.Abs(petBounds.Right - workingArea.Right) <= edgeTolerance;
        var touchesTop = Math.Abs(petBounds.Top - workingArea.Top) <= edgeTolerance;
        var touchesBottom = Math.Abs(petBounds.Bottom - workingArea.Bottom) <= edgeTolerance;

        if (touchesLeft && touchesTop) return new FanLayout(45, 100, true);
        if (touchesRight && touchesTop) return new FanLayout(135, 100, true);
        if (touchesLeft && touchesBottom) return new FanLayout(-45, 100, true);
        if (touchesRight && touchesBottom) return new FanLayout(-135, 100, true);
        if (touchesLeft) return new FanLayout(0, 168, false);
        if (touchesRight) return new FanLayout(180, 168, false);
        if (touchesTop) return new FanLayout(90, 168, false);
        if (touchesBottom) return new FanLayout(-90, 168, false);

        var distances = new[]
        {
            (Angle: 0d, Distance: Math.Abs(petBounds.Left - workingArea.Left)),
            (Angle: 180d, Distance: Math.Abs(workingArea.Right - petBounds.Right)),
            (Angle: 90d, Distance: Math.Abs(petBounds.Top - workingArea.Top)),
            (Angle: -90d, Distance: Math.Abs(workingArea.Bottom - petBounds.Bottom))
        };
        return new FanLayout(distances.OrderBy(item => item.Distance).First().Angle, 168, false);
    }

    private static Geometry CreateSectorGeometry(Point center, double innerRadius, double outerRadius, double startAngle, double endAngle)
    {
        var outerStart = PointOnCircle(center, outerRadius, startAngle);
        var outerEnd = PointOnCircle(center, outerRadius, endAngle);
        var innerEnd = PointOnCircle(center, innerRadius, endAngle);
        var innerStart = PointOnCircle(center, innerRadius, startAngle);
        var sweep = Math.Abs(endAngle - startAngle);
        var figure = new PathFigure { StartPoint = outerStart, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(outerEnd, new Size(outerRadius, outerRadius), 0, sweep > 180, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(innerEnd, true));
        figure.Segments.Add(new ArcSegment(innerStart, new Size(innerRadius, innerRadius), 0, sweep > 180, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double angle)
    {
        var radians = angle * Math.PI / 180;
        return new Point(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }

    private static Brush CreateSectorBrush(bool enabled, bool selected)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.36, 0.3),
            GradientOrigin = new Point(0.28, 0.22),
            RadiusX = 0.88,
            RadiusY = 0.88
        };
        if (enabled && selected)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(242, 246, 242, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(228, 196, 188, 255), 0.58));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(220, 143, 126, 244), 1));
        }
        else if (enabled)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(224, 251, 249, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(204, 226, 218, 252), 0.62));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(194, 191, 174, 244), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(184, 251, 254, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(160, 220, 235, 246), 1));
        }
        brush.Freeze();
        return brush;
    }

    private static Effect CreateGlassShadow(bool enabled, bool selected) => new DropShadowEffect
    {
        Color = selected ? Color.FromRgb(102, 86, 220) : enabled ? Color.FromRgb(104, 96, 184) : Color.FromRgb(91, 120, 147),
        BlurRadius = selected ? 25 : 17,
        ShadowDepth = selected ? 2 : 4,
        Direction = 270,
        Opacity = selected ? 0.48 : enabled ? 0.25 : 0.14
    };

    private static double NormalizeAngle(double angle)
    {
        var normalized = angle % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static double NormalizeSignedAngle(double angle)
    {
        var normalized = NormalizeAngle(angle);
        return normalized > 180 ? normalized - 360 : normalized;
    }

    private sealed record CommandSectorVisual(ShapePath Path, FrameworkElement Content, bool Enabled, Point Center);

    private readonly record struct FanLayout(double CenterAngle, double SweepAngle, bool IsCorner);
}
