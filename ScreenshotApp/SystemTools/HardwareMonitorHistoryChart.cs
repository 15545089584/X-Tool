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

        var area = new Rect(46, 18, Math.Max(1, width - 62), Math.Max(1, height - 46));
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 173, 203, 225)), 1);
        for (var index = 0; index <= 4; index++)
        {
            var y = area.Top + area.Height * index / 4d;
            drawingContext.DrawLine(gridPen, new Point(area.Left, y), new Point(area.Right, y));
            DrawLabel(drawingContext, $"{100 - index * 20}°", new Point(8, y - 7), 10, Color.FromRgb(126, 151, 174));
        }

        DrawLabel(drawingContext, "-30 分钟", new Point(area.Left, area.Bottom + 8), 10, Color.FromRgb(126, 151, 174));
        var nowText = new FormattedText("现在", System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 10,
            new SolidColorBrush(Color.FromRgb(126, 151, 174)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(nowText, new Point(area.Right - nowText.Width, area.Bottom + 8));

        if (_points.Count < 2)
        {
            var text = new FormattedText("等待积累实时数据…", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12, new SolidColorBrush(Color.FromRgb(114, 139, 163)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawingContext.DrawText(text, new Point(area.Left + (area.Width - text.Width) / 2, area.Top + (area.Height - text.Height) / 2));
            return;
        }

        DateTimeOffset timelineEnd = _points[^1].CapturedAt;
        DrawSeries(drawingContext, area, _points.Select(item => (item.CapturedAt, item.CpuTemperature)).ToArray(), timelineEnd, Color.FromRgb(77, 124, 254));
        DrawSeries(drawingContext, area, _points.Select(item => (item.CapturedAt, item.GpuTemperature)).ToArray(), timelineEnd, Color.FromRgb(143, 101, 219));
        DrawSeries(drawingContext, area, _points.Select(item => (item.CapturedAt, item.MemoryTemperature)).ToArray(), timelineEnd, Color.FromRgb(22, 185, 155));
        DrawSeries(drawingContext, area, _points.Select(item => (item.CapturedAt, item.MainboardTemperature)).ToArray(), timelineEnd, Color.FromRgb(240, 147, 69));
        DrawSeries(drawingContext, area, _points.Select(item => (item.CapturedAt, item.StorageTemperature)).ToArray(), timelineEnd, Color.FromRgb(56, 168, 216));
    }

    private static void DrawSeries(
        DrawingContext context,
        Rect area,
        IReadOnlyList<(DateTimeOffset CapturedAt, double? Value)> values,
        DateTimeOffset timelineEnd,
        Color color)
    {
        DateTimeOffset timelineStart = timelineEnd - TimeSpan.FromMinutes(30);
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            var started = false;
            for (var index = 0; index < values.Count; index++)
            {
                if (!values[index].Value.HasValue) { started = false; continue; }
                double elapsed = (values[index].CapturedAt - timelineStart).TotalSeconds;
                var x = area.Left + area.Width * Math.Clamp(elapsed / TimeSpan.FromMinutes(30).TotalSeconds, 0, 1);
                var y = area.Bottom - area.Height * (Math.Clamp(values[index].Value!.Value, 20, 100) - 20) / 80d;
                if (!started) { stream.BeginFigure(new Point(x, y), false, false); started = true; }
                else stream.LineTo(new Point(x, y), true, false);
            }
        }
        geometry.Freeze();
        var pen = new Pen(new SolidColorBrush(color), 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawLabel(DrawingContext context, string text, Point origin, double size, Color color)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size,
            new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(formatted, origin);
    }
}
