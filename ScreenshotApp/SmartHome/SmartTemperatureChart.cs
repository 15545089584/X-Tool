using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenshotApp.SmartHome;

/// <summary>
/// 绘制最近二十四小时室温趋势：坐标轴刻度标注、鼠标十字虚线对齐与悬停位置温度读数。
/// 不为轻量图表引入大型图表依赖。
/// </summary>
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

    private Point? _mousePosition;

    public SmartTemperatureChart()
    {
        MouseMove += (_, arguments) =>
        {
            _mousePosition = arguments.GetPosition(this);
            InvalidateVisual();
        };
        MouseLeave += (_, _) =>
        {
            if (_mousePosition is null) return;
            _mousePosition = null;
            InvalidateVisual();
        };
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 80 || bounds.Height < 80) return;

        const double gutterLeft = 44;
        const double gutterBottom = 26;
        var plot = new Rect(gutterLeft, 14, bounds.Width - gutterLeft - 14, bounds.Height - 14 - gutterBottom);

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

        double X(double seconds) => plot.Left + seconds / duration * plot.Width;
        double Y(double value) => plot.Bottom - (value - min) / (max - min) * plot.Height;

        // 纵轴：水平网格线与温度刻度。
        var gridPen = FrozenPen(Color.FromArgb(48, 119, 151, 177), 1);
        for (var index = 0; index <= 4; index++)
        {
            var value = min + (max - min) * index / 4d;
            var y = plot.Bottom - plot.Height * index / 4d;
            drawingContext.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = $"{value:0.#}°";
            var formatted = FormatText(label, 9.5, Color.FromRgb(139, 158, 174));
            drawingContext.DrawText(formatted, new Point(plot.Left - 8 - formatted.Width, y - formatted.Height / 2));
        }

        drawingContext.DrawLine(FrozenPen(Color.FromArgb(96, 119, 151, 177), 1),
            new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));

        // 横轴：时间刻度。
        for (var index = 0; index <= 4; index++)
        {
            var seconds = duration * index / 4d;
            var x = X(seconds);
            drawingContext.DrawLine(gridPen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 4));
            var label = start.AddSeconds(seconds).LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture);
            var formatted = FormatText(label, 9.5, Color.FromRgb(139, 158, 174));
            drawingContext.DrawText(formatted, new Point(x - formatted.Width / 2, plot.Bottom + 7));
        }

        DrawText(drawingContext, "°C", 9.5, Color.FromRgb(139, 158, 174), new Point(6, 0));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var index = 0; index < points.Length; index++)
            {
                var point = points[index];
                var x = X((point.Timestamp - start).TotalSeconds);
                var y = Y(point.Value);
                if (index == 0) context.BeginFigure(new Point(x, y), false, false);
                else context.LineTo(new Point(x, y), true, false);
            }
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, FrozenPen(Color.FromRgb(61, 146, 214), 2.2), geometry);

        var last = points[^1];
        var lastPoint = new Point(X((last.Timestamp - start).TotalSeconds), Y(last.Value));
        drawingContext.DrawEllipse(FrozenBrush(Color.FromRgb(61, 146, 214)), null, lastPoint, 4, 4);

        // 鼠标十字虚线对齐最近采样点，并给出该位置的温度读数。
        if (_mousePosition is { } mouse && mouse.X >= plot.Left && mouse.X <= plot.Right &&
            mouse.Y >= plot.Top && mouse.Y <= plot.Bottom)
        {
            var targetSeconds = (mouse.X - plot.Left) / plot.Width * duration;
            var nearest = points
                .OrderBy(point => Math.Abs((point.Timestamp - start).TotalSeconds - targetSeconds))
                .First();
            var snapX = X((nearest.Timestamp - start).TotalSeconds);
            var snapY = Y(nearest.Value);

            var dashPen = new Pen(FrozenBrush(Color.FromRgb(77, 124, 254)), 1.2)
            {
                DashStyle = new DashStyle(new double[] { 4, 3 }, 0)
            };
            dashPen.Freeze();
            drawingContext.DrawLine(dashPen, new Point(snapX, plot.Top), new Point(snapX, plot.Bottom));
            drawingContext.DrawLine(dashPen, new Point(plot.Left, snapY), new Point(plot.Right, snapY));
            drawingContext.DrawEllipse(FrozenBrush(Color.FromRgb(77, 124, 254)), null, new Point(snapX, snapY), 3.5, 3.5);

            var readout = $"{nearest.Timestamp.LocalDateTime:HH:mm}  {nearest.Value:0.##}°C";
            var readoutText = FormatText(readout, 11, Color.FromRgb(32, 53, 74));
            var boxWidth = readoutText.Width + 14;
            var boxHeight = readoutText.Height + 8;
            var boxX = Math.Clamp(snapX + 10, plot.Left, plot.Right - boxWidth);
            var boxY = Math.Clamp(snapY - boxHeight - 8, plot.Top, plot.Bottom - boxHeight);
            var box = new Rect(new Point(boxX, boxY), new Size(boxWidth, boxHeight));
            drawingContext.DrawRoundedRectangle(FrozenBrush(Color.FromRgb(0xFA, 0xFF, 0xFF)),
                FrozenPen(Color.FromRgb(77, 124, 254), 1), box, 6, 6);
            drawingContext.DrawText(readoutText, new Point(boxX + 7, boxY + 4));
        }
    }

    private static FormattedText FormatText(string text, double size, Color color)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size,
            FrozenBrush(color),
            1);
    }

    private static void DrawText(DrawingContext context, string text, double size, Color color, Point point)
    {
        context.DrawText(FormatText(text, size, color), point);
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
