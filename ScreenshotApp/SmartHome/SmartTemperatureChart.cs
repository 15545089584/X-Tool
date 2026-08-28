using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ScreenshotApp.SmartHome;

/// <summary>绘制最近二十四小时室温趋势，避免为一个轻量图表引入大型图表依赖。</summary>
public sealed class SmartTemperatureChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(IReadOnlyList<SmartHistoryPoint>),
        typeof(SmartTemperatureChart),
        new FrameworkPropertyMetadata(Array.Empty<SmartHistoryPoint>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<SmartHistoryPoint> Points
    {
        get => (IReadOnlyList<SmartHistoryPoint>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 40 || bounds.Height < 40) return;

        var plot = new Rect(12, 12, bounds.Width - 24, bounds.Height - 34);
        var gridPen = FrozenPen(Color.FromArgb(48, 119, 151, 177), 1);
        for (var index = 0; index < 3; index++)
        {
            var y = plot.Top + plot.Height * index / 2d;
            drawingContext.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        var points = Points?.Where(point => !double.IsNaN(point.Value) && !double.IsInfinity(point.Value))
            .OrderBy(point => point.Timestamp)
            .ToArray() ?? Array.Empty<SmartHistoryPoint>();
        if (points.Length < 2)
        {
            DrawText(drawingContext, "暂无室温历史", 11, Color.FromRgb(132, 153, 170),
                new Point(plot.Left, plot.Top + plot.Height / 2 - 8));
            return;
        }

        var min = points.Min(point => point.Value);
        var max = points.Max(point => point.Value);
        if (max - min < 1) { min -= 0.5; max += 0.5; }
        else { min -= 0.4; max += 0.4; }
        var start = points[0].Timestamp;
        var end = points[^1].Timestamp;
        var duration = Math.Max(1, (end - start).TotalSeconds);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var index = 0; index < points.Length; index++)
            {
                var x = plot.Left + (points[index].Timestamp - start).TotalSeconds / duration * plot.Width;
                var y = plot.Bottom - (points[index].Value - min) / (max - min) * plot.Height;
                if (index == 0) context.BeginFigure(new Point(x, y), false, false);
                else context.LineTo(new Point(x, y), true, false);
            }
        }
        geometry.Freeze();

        drawingContext.DrawGeometry(null, FrozenPen(Color.FromRgb(61, 146, 214), 2.2), geometry);
        var last = points[^1];
        var lastX = plot.Left + (last.Timestamp - start).TotalSeconds / duration * plot.Width;
        var lastY = plot.Bottom - (last.Value - min) / (max - min) * plot.Height;
        drawingContext.DrawEllipse(FrozenBrush(Color.FromRgb(61, 146, 214)), null, new Point(lastX, lastY), 4, 4);

        DrawText(drawingContext, $"{min:0.#}°", 9, Color.FromRgb(139, 158, 174), new Point(plot.Left, plot.Bottom + 5));
        var maxText = $"{max:0.#}°";
        DrawText(drawingContext, maxText, 9, Color.FromRgb(139, 158, 174),
            new Point(plot.Right - Math.Max(24, maxText.Length * 6), plot.Bottom + 5));
    }

    private static void DrawText(DrawingContext context, string text, double size, Color color, Point point)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            FrozenBrush(color),
            1);
        context.DrawText(formatted, point);
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(FrozenBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
