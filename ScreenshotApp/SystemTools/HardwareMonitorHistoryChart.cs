using System.Windows;
using System.Windows.Media;

namespace ScreenshotApp.SystemTools;

/// <summary>轻量内存曲线，仅绘制当前页面需要的最近传感器数据。</summary>
public sealed class HardwareMonitorHistoryChart : FrameworkElement
{
    private IReadOnlyList<HardwareHistoryPoint> _points = Array.Empty<HardwareHistoryPoint>();

    public void SetPoints(IReadOnlyList<HardwareHistoryPoint> points)
    {
        _points = points;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = Math.Max(0, ActualWidth);
        var height = Math.Max(0, ActualHeight);
        if (width < 20 || height < 20) return;

        var area = new Rect(34, 12, Math.Max(1, width - 46), Math.Max(1, height - 32));
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 173, 203, 225)), 1);
        for (var index = 0; index <= 4; index++)
        {
            var y = area.Top + area.Height * index / 4d;
            drawingContext.DrawLine(gridPen, new Point(area.Left, y), new Point(area.Right, y));
        }

        if (_points.Count < 2)
        {
            var text = new FormattedText("等待积累实时数据…", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12, new SolidColorBrush(Color.FromRgb(114, 139, 163)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawingContext.DrawText(text, new Point(area.Left + (area.Width - text.Width) / 2, area.Top + (area.Height - text.Height) / 2));
            return;
        }

        DrawSeries(drawingContext, area, _points.Select(item => item.CpuTemperature).ToArray(), Color.FromRgb(77, 124, 254));
        DrawSeries(drawingContext, area, _points.Select(item => item.GpuTemperature).ToArray(), Color.FromRgb(143, 101, 219));
        DrawSeries(drawingContext, area, _points.Select(item => item.CpuLoad).ToArray(), Color.FromRgb(22, 185, 155));
    }

    private static void DrawSeries(DrawingContext context, Rect area, IReadOnlyList<double?> values, Color color)
    {
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            var started = false;
            for (var index = 0; index < values.Count; index++)
            {
                if (!values[index].HasValue) { started = false; continue; }
                var x = area.Left + area.Width * index / Math.Max(1, values.Count - 1d);
                var y = area.Bottom - area.Height * Math.Clamp(values[index]!.Value, 0, 100) / 100d;
                if (!started) { stream.BeginFigure(new Point(x, y), false, false); started = true; }
                else stream.LineTo(new Point(x, y), true, false);
            }
        }
        geometry.Freeze();
        var pen = new Pen(new SolidColorBrush(color), 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }
}
