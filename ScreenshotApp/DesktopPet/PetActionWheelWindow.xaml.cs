using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using ShapePath = System.Windows.Shapes.Path;

namespace ScreenshotApp.DesktopPet;

/// <summary>文件拖入桌面宠物时显示的自适应毛玻璃操作轮盘。</summary>
public partial class PetActionWheelWindow : Window
{
    private const int SectorCount = 4;
    private const int ArchiveSectorIndex = 0;
    private const int SendToPhoneSectorIndex = 1;
    private const int StoreInShelfSectorIndex = 2;
    private readonly List<WheelSectorVisual> _sectors = [];
    private readonly PetArchiveDropAction _archiveAction;
    private readonly bool _fileOnlySelection;
    private Point _wheelCenter;
    private double _innerRadius;
    private double _outerRadius;
    private double _fanStartAngle;
    private double _sectorSweepAngle;
    private int _selectedSectorIndex = -1;
    private bool _actionCommitted;
    private bool _cancelRaised;

    internal PetActionWheelWindow(Rect petBounds, Rect workingArea, IReadOnlyList<string> draggedPaths)
    {
        _archiveAction = PetArchiveDropPlanner.ResolveAction(draggedPaths);
        _fileOnlySelection = draggedPaths.Count > 0 && draggedPaths.All(File.Exists);
        InitializeComponent();
        ConfigureLayout(petBounds, workingArea);
        BuildWheel();
        Loaded += (_, _) => PlayOpenAnimation();
    }

    internal event Action<IReadOnlyList<string>>? FilesDropped;

    internal event Action<IReadOnlyList<string>>? FilesStored;

    internal event Action<IReadOnlyList<string>, PetArchiveDropAction>? ArchiveRequested;

    internal event Action? Cancelled;

    protected override void OnClosed(EventArgs e)
    {
        RaiseCancelledIfNeeded();
        base.OnClosed(e);
    }

    private void ConfigureLayout(Rect petBounds, Rect workingArea)
    {
        var petCenter = new Point(petBounds.Left + petBounds.Width / 2, petBounds.Top + petBounds.Height / 2);
        _innerRadius = Math.Clamp(petBounds.Width * 0.42, 92, 150);
        var layout = ResolveFanLayout(petBounds, workingArea);
        var standardOuterRadius = _innerRadius + Math.Clamp(petBounds.Width * 0.34, 88, 104);
        _outerRadius = layout.IsCorner
            ? Math.Sqrt(
                _innerRadius * _innerRadius +
                (standardOuterRadius * standardOuterRadius - _innerRadius * _innerRadius) * 168 / layout.SweepAngle)
            : standardOuterRadius;
        _fanStartAngle = layout.CenterAngle - layout.SweepAngle / 2;
        _sectorSweepAngle = layout.SweepAngle / SectorCount;

        var layoutRadius = _outerRadius + 92;
        var desiredBounds = new Rect(
            petCenter.X - layoutRadius,
            petCenter.Y - layoutRadius,
            layoutRadius * 2,
            layoutRadius * 2);
        var visibleBounds = Rect.Intersect(desiredBounds, workingArea);
        Left = visibleBounds.Left;
        Top = visibleBounds.Top;
        Width = Math.Max(1, visibleBounds.Width);
        Height = Math.Max(1, visibleBounds.Height);
        _wheelCenter = new Point(petCenter.X - Left, petCenter.Y - Top);
    }

    private void BuildWheel()
    {
        WheelCanvas.Children.Clear();
        _sectors.Clear();
        for (var index = 0; index < SectorCount; index++)
        {
            var startAngle = _fanStartAngle + index * _sectorSweepAngle + 1.2;
            var endAngle = _fanStartAngle + (index + 1) * _sectorSweepAngle - 1.2;
            var action = index switch
            {
                ArchiveSectorIndex => _archiveAction == PetArchiveDropAction.Extract
                    ? DropAction.Extract
                    : DropAction.Compress,
                SendToPhoneSectorIndex when _fileOnlySelection => DropAction.SendToPhone,
                StoreInShelfSectorIndex when _fileOnlySelection => DropAction.StoreInShelf,
                _ => DropAction.None
            };
            var enabled = action != DropAction.None;
            var path = new ShapePath
            {
                Data = CreateSectorGeometry(_wheelCenter, _innerRadius, _outerRadius, startAngle, endAngle),
                Fill = CreateSectorBrush(action, selected: false),
                Stroke = new SolidColorBrush(action switch
                {
                    DropAction.SendToPhone => Color.FromArgb(205, 137, 184, 245),
                    DropAction.StoreInShelf => Color.FromArgb(210, 151, 139, 236),
                    DropAction.Compress or DropAction.Extract => Color.FromArgb(210, 244, 181, 102),
                    _ => Color.FromArgb(150, 190, 214, 232)
                }),
                StrokeThickness = enabled ? 1.6 : 1.1,
                Effect = CreateGlassShadow(enabled, selected: false),
                Opacity = enabled ? 1 : 0.68,
                IsHitTestVisible = false
            };
            WheelCanvas.Children.Add(path);

            var middleAngle = (startAngle + endAngle) / 2;
            var labelCenter = PointOnCircle(_wheelCenter, (_innerRadius + _outerRadius) / 2, middleAngle);
            var content = CreateSectorContent(action);
            Canvas.SetLeft(content, labelCenter.X - content.Width / 2);
            Canvas.SetTop(content, labelCenter.Y - content.Height / 2);
            WheelCanvas.Children.Add(content);
            _sectors.Add(new WheelSectorVisual(path, content, action));
        }
    }

    private FrameworkElement CreateSectorContent(DropAction action)
    {
        var enabled = action != DropAction.None;
        var isShelf = action == DropAction.StoreInShelf;
        var isArchive = action is DropAction.Compress or DropAction.Extract;
        var icon = new TextBlock
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = enabled ? 23 : 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(isShelf
                ? Color.FromRgb(104, 87, 216)
                : isArchive
                    ? Color.FromRgb(214, 132, 31)
                    : enabled ? Color.FromRgb(77, 124, 254) : Color.FromRgb(126, 148, 168)),
            Text = action switch
            {
                DropAction.SendToPhone => "\uE724",
                DropAction.StoreInShelf => "\uE8B7",
                DropAction.Compress => "\uE7B8",
                DropAction.Extract => "\uE7C5",
                _ => "\uE710"
            },
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = Colors.White,
                BlurRadius = 5,
                ShadowDepth = 0,
                Opacity = 0.82
            }
        };
        var text = new TextBlock
        {
            Margin = new Thickness(0, 5, 0, 0),
            FontSize = enabled ? 12 : 9.5,
            FontWeight = enabled ? FontWeights.SemiBold : FontWeights.Medium,
            Foreground = new SolidColorBrush(isShelf
                ? Color.FromRgb(71, 65, 139)
                : isArchive
                    ? Color.FromRgb(143, 86, 25)
                    : enabled ? Color.FromRgb(54, 87, 122) : Color.FromRgb(117, 139, 158)),
            Text = action switch
            {
                DropAction.SendToPhone => "传到手机",
                DropAction.StoreInShelf => "存入暂存区",
                DropAction.Compress => "压缩文件",
                DropAction.Extract => "解压到此处",
                _ => "待开放"
            },
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = Colors.White,
                BlurRadius = 4,
                ShadowDepth = 0,
                Opacity = 0.9
            }
        };
        var content = new Grid
        {
            Width = 82,
            Height = 56,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        content.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { icon, text }
        });
        ToolTipService.SetToolTip(content, action switch
        {
            DropAction.SendToPhone => "松开发送所选文件到已连接手机",
            DropAction.StoreInShelf => "松开后把所选文件保留到本次运行的暂存区",
            DropAction.Compress => "松开后在来源所在位置创建 ZIP 压缩包",
            DropAction.Extract => "松开后把压缩包解压到其所在位置",
            _ => "该位置暂未开放"
        });
        return content;
    }

    private void Window_DragEnter(object sender, DragEventArgs e) => UpdateDragSelection(e);

    private void Window_DragOver(object sender, DragEventArgs e) => UpdateDragSelection(e);

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        SetSelectedSector(-1);
        Close();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var selectedIndex = ResolveSectorIndex(e.GetPosition(WheelCanvas));
        if (TryGetPaths(e.Data, out var files) &&
            selectedIndex >= 0 && selectedIndex < _sectors.Count &&
            _sectors[selectedIndex].Action is not DropAction.None)
        {
            var action = _sectors[selectedIndex].Action;
            _actionCommitted = true;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            if (action == DropAction.SendToPhone)
            {
                FilesDropped?.Invoke(files);
            }
            else if (action == DropAction.StoreInShelf)
            {
                FilesStored?.Invoke(files);
            }
            else
            {
                ArchiveRequested?.Invoke(
                    files,
                    action == DropAction.Extract
                        ? PetArchiveDropAction.Extract
                        : PetArchiveDropAction.Compress);
            }
            Close();
            return;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
        Close();
    }

    private void UpdateDragSelection(DragEventArgs e)
    {
        if (!TryGetPaths(e.Data, out _))
        {
            SetSelectedSector(-1);
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var selectedIndex = ResolveSectorIndex(e.GetPosition(WheelCanvas));
        SetSelectedSector(selectedIndex);
        e.Effects = selectedIndex >= 0 && selectedIndex < _sectors.Count &&
                    _sectors[selectedIndex].Action is not DropAction.None
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
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
            var enabled = sector.Action != DropAction.None;
            sector.Path.Fill = CreateSectorBrush(sector.Action, selected);
            sector.Path.Effect = CreateGlassShadow(enabled, selected);
            sector.Path.Opacity = selected ? 1 : enabled ? 0.94 : 0.62;
            sector.Content.RenderTransform = new ScaleTransform(selected ? 1.14 : 1, selected ? 1.14 : 1);
            sector.Content.Opacity = selected ? 1 : enabled ? 0.98 : 0.64;
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

    private void RaiseCancelledIfNeeded()
    {
        if (_actionCommitted || _cancelRaised)
        {
            return;
        }

        _cancelRaised = true;
        Cancelled?.Invoke();
    }

    private static bool TryGetPaths(IDataObject dataObject, out IReadOnlyList<string> files)
    {
        files = Array.Empty<string>();
        if (!dataObject.GetDataPresent(DataFormats.FileDrop) ||
            dataObject.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return false;
        }

        var existingFiles = paths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        files = existingFiles;
        return existingFiles.Length > 0;
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

    private static Brush CreateSectorBrush(DropAction action, bool selected)
    {
        var enabled = action != DropAction.None;
        var isShelf = action == DropAction.StoreInShelf;
        var isArchive = action is DropAction.Compress or DropAction.Extract;
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.36, 0.3),
            GradientOrigin = new Point(0.28, 0.22),
            RadiusX = 0.88,
            RadiusY = 0.88
        };
        if (isArchive && selected)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(244, 255, 250, 235), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(230, 255, 219, 150), 0.58));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(220, 244, 171, 78), 1));
        }
        else if (isArchive)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(226, 255, 253, 245), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(208, 255, 235, 192), 0.62));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(196, 246, 197, 112), 1));
        }
        else if (isShelf && selected)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(242, 246, 242, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(228, 196, 188, 255), 0.58));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(220, 143, 126, 244), 1));
        }
        else if (isShelf)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(224, 251, 249, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(204, 226, 218, 252), 0.62));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(194, 191, 174, 244), 1));
        }
        else if (enabled && selected)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(242, 238, 248, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(225, 170, 207, 255), 0.58));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(218, 105, 157, 246), 1));
        }
        else if (enabled)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(222, 248, 253, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(202, 210, 232, 250), 0.62));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(192, 164, 205, 245), 1));
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
        Color = selected ? Color.FromRgb(77, 124, 254) : enabled ? Color.FromRgb(86, 130, 174) : Color.FromRgb(91, 120, 147),
        BlurRadius = selected ? 25 : 17,
        ShadowDepth = selected ? 2 : 4,
        Direction = 270,
        Opacity = selected ? 0.48 : enabled ? 0.24 : 0.14
    };

    private static double NormalizeAngle(double angle)
    {
        var normalized = angle % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private enum DropAction
    {
        None,
        SendToPhone,
        StoreInShelf,
        Compress,
        Extract
    }

    private sealed record WheelSectorVisual(ShapePath Path, FrameworkElement Content, DropAction Action);

    private readonly record struct FanLayout(double CenterAngle, double SweepAngle, bool IsCorner);
}
