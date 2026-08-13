using System.Windows;
using System.Windows.Media;

namespace ScreenshotApp.SystemTools;

/// <summary>贴合浅色毛玻璃界面的温度圆环；缺失读数只绘制中性轨道，不伪造进度。</summary>
public sealed class HardwareTemperatureGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(HardwareTemperatureGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(HardwareTemperatureGauge),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsSupportedProperty = DependencyProperty.Register(
        nameof(IsSupported), typeof(bool), typeof(HardwareTemperatureGauge),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public bool IsSupported { get => (bool)GetValue(IsSupportedProperty); set => SetValue(IsSupportedProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 24) return;

        Point center = new(ActualWidth / 2, ActualHeight / 2);
        double radius = Math.Max(1, size / 2 - 8);
        var track = new Pen(new SolidColorBrush(Color.FromArgb(112, 207, 225, 239)), 9)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        track.Freeze();
        DrawArc(drawingContext, center, radius, -225, 270, track);

        if (!IsSupported) return;
        double sweep = 270 * Math.Clamp(Value, 0, 110) / 110d;
        if (sweep <= 0.5) return;
        var valuePen = new Pen(Accent, 9)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        DrawArc(drawingContext, center, radius, -225, sweep, valuePen);
    }

    private static void DrawArc(DrawingContext context, Point center, double radius, double startAngle, double sweepAngle, Pen pen)
    {
        Point start = PointOnCircle(center, radius, startAngle);
        Point end = PointOnCircle(center, radius, startAngle + sweepAngle);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext stream = geometry.Open())
        {
            stream.BeginFigure(start, false, false);
            stream.ArcTo(end, new Size(radius, radius), 0, sweepAngle > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angle)
    {
        double radians = angle * Math.PI / 180d;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
