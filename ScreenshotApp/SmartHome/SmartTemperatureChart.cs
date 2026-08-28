using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenshotApp.SmartHome;

/// <summary>
/// 绘制最近二十四小时室温趋势，样式对齐系统中心存储页的用量历史曲线：
/// 渐变面积、采样节点圆点、右侧温度刻度与底部时间刻度，鼠标悬停时以
/// 虚线十字对齐最近采样点并弹出该位置的温度读数。
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

    /// <summary>FrameworkElement 无背景时透明区域不可命中；重写命中测试让整个图面都能接收鼠标移动。</summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) =>
        new PointHitTestResult(this, parameters.HitPoint);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 80 || bounds.Height < 80) return;

        const double gutterBottom = 26;
        const double gutterRight = 52;
        var plot = new Rect(12, 12, bounds.Width - 12 - gutterRight, bounds.Height - 12 - gutterBottom);

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

        var axisPen = FrozenPen(Color.FromArgb(40, 119, 151, 177), 1);
        for (var index = 0; index <= 4; index++)
        {
            var value = min + (max - min) * index / 4d;
            var y = plot.Bottom - plot.Height * index / 4d;
            drawingContext.DrawLine(axisPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = $"{value:0.#}°";
            var formatted = FormatText(label, 9.5, Color.FromRgb(139, 158, 174));
            drawingContext.DrawText(formatted, new Point(plot.Right + 8, y - formatted.Height / 2));
        }

        for (var index = 0; index <= 4; index++)
        {
            var seconds = duration * index / 4d;
            var x = X(seconds);
            var label = start.AddSeconds(seconds).LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture);
            var formatted = FormatText(label, 9.5, Color.FromRgb(139, 158, 174));
            drawingContext.DrawText(formatted, new Point(x - formatted.Width / 2, plot.Bottom + 7));
        }

        // 折线与其下方的柔和渐变面积。
        var lineGeometry = new StreamGeometry();
        using (var context = lineGeometry.Open())
        {
            for (var index = 0; index < points.Length; index++)
            {
                var x = X((points[index].Timestamp - start).TotalSeconds);
                var y = Y(points[index].Value);
                if (index == 0) context.BeginFigure(new Point(x, y), false, false);
                else context.LineTo(new Point(x, y), true, false);
            }
        }
        lineGeometry.Freeze();
        drawingContext.DrawGeometry(null, FrozenPen(Color.FromRgb(61, 146, 214), 2.2), lineGeometry);

        var areaGeometry = new StreamGeometry();
        using (var context = areaGeometry.Open())
        {
            context.BeginFigure(new Point(plot.Left, plot.Bottom), true, true);
            for (var index = 0; index < points.Length; index++)
            {
                context.LineTo(new Point(X((points[index].Timestamp - start).TotalSeconds), Y(points[index].Value)), true, false);
            }

            context.LineTo(new Point(X((points[^1].Timestamp - start).TotalSeconds), plot.Bottom), true, false);
        }
        areaGeometry.Freeze();
        var areaBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(90, 61, 146, 214), 0),
                new GradientStop(Color.FromArgb(12, 61, 146, 214), 1)
            }
        };
        areaBrush.Freeze();
        drawingContext.DrawGeometry(areaBrush, null, areaGeometry);

        // 采样节点圆点。
        var nodeFill = FrozenBrush(Colors.White);
        var nodePen = FrozenPen(Color.FromRgb(61, 146, 214), 1.8);
        foreach (var point in points)
        {
            drawingContext.DrawEllipse(nodeFill, nodePen,
                new Point(X((point.Timestamp - start).TotalSeconds), Y(point.Value)), 3.2, 3.2);
        }

        // 悬停：竖直虚线跟随鼠标，水平虚线与圆点对齐折线在该位置的插值读数。
        if (_mousePosition is { } mouse && mouse.X >= plot.Left && mouse.X <= plot.Right)
        {
            var ratio = Math.Clamp((mouse.X - plot.Left) / plot.Width, 0, 1);
            var targetSeconds = ratio * duration;
            SmartHistoryPoint? before = null;
            SmartHistoryPoint? after = null;
            for (var index = 0; index < points.Length - 1; index++)
            {
                var leftSeconds = (points[index].Timestamp - start).TotalSeconds;
                var rightSeconds = (points[index + 1].Timestamp - start).TotalSeconds;
                if (targetSeconds >= leftSeconds && targetSeconds <= rightSeconds)
                {
                    before = points[index];
                    after = points[index + 1];
                    break;
                }
            }

            double valueAtX;
            DateTimeOffset timeAtX;
            if (before is null || after is null)
            {
                var only = before ?? after!;
                valueAtX = only.Value;
                timeAtX = only.Timestamp;
            }
            else
            {
                var leftSeconds = (before.Timestamp - start).TotalSeconds;
                var span = (after.Timestamp - start).TotalSeconds - leftSeconds;
                var t = span <= 0 ? 0 : (targetSeconds - leftSeconds) / span;
                valueAtX = before.Value + (after.Value - before.Value) * t;
                timeAtX = before.Timestamp + TimeSpan.FromSeconds((after.Timestamp - before.Timestamp).TotalSeconds * t);
            }

            var snapX = mouse.X;
            var snapY = Y(valueAtX);

            var dashPen = new Pen(FrozenBrush(Color.FromRgb(77, 124, 254)), 1.2)
            {
                DashStyle = new DashStyle(new double[] { 4, 3 }, 0)
            };
            dashPen.Freeze();
            drawingContext.DrawLine(dashPen, new Point(snapX, plot.Top), new Point(snapX, plot.Bottom));
            drawingContext.DrawLine(dashPen, new Point(plot.Left, snapY), new Point(plot.Right, snapY));
            drawingContext.DrawEllipse(FrozenBrush(Color.FromRgb(77, 124, 254)), null, new Point(snapX, snapY), 4, 4);

            var lineOne = FormatText(timeAtX.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture), 10, Color.FromRgb(113, 132, 154));
            var lineTwo = FormatText($"{valueAtX:0.##}°C", 12.5, Color.FromRgb(32, 53, 74));
            var boxWidth = Math.Max(lineOne.Width, lineTwo.Width) + 18;
            var boxHeight = lineOne.Height + lineTwo.Height + 12;
            var boxX = Math.Clamp(snapX + 12, plot.Left, plot.Right - boxWidth);
            var boxY = Math.Clamp(mouse.Y - boxHeight / 2, plot.Top, plot.Bottom - boxHeight);
            var box = new Rect(new Point(boxX, boxY), new Size(boxWidth, boxHeight));
            drawingContext.DrawRoundedRectangle(FrozenBrush(Color.FromRgb(0xFA, 0xFF, 0xFF)),
                FrozenPen(Color.FromRgb(77, 124, 254), 1), box, 8, 8);
            drawingContext.DrawText(lineOne, new Point(boxX + 9, boxY + 4));
            drawingContext.DrawText(lineTwo, new Point(boxX + 9, boxY + 4 + lineOne.Height + 2));
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
