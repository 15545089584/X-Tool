using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

internal enum ScreenshotAnnotationTool
{
    None,
    Pen,
    Rectangle,
    ColorPicker
}

internal abstract record ScreenshotAnnotation(Color Color, double Thickness);

internal sealed record PenScreenshotAnnotation(
    IReadOnlyList<Point> Points,
    Color Color,
    double Thickness) : ScreenshotAnnotation(Color, Thickness);

internal sealed record RectangleScreenshotAnnotation(
    Rect Bounds,
    Color Color,
    double Thickness) : ScreenshotAnnotation(Color, Thickness);

/// <summary>
/// 将选区内的矢量标注按原图像素比例绘制到最终截图。
/// </summary>
internal static class ScreenshotAnnotationRenderer
{
    internal static BitmapSource Render(
        BitmapSource croppedBitmap,
        IReadOnlyList<ScreenshotAnnotation> annotations,
        Size logicalSelectionSize)
    {
        if (annotations.Count == 0)
        {
            return croppedBitmap;
        }

        if (logicalSelectionSize.Width <= 0 || logicalSelectionSize.Height <= 0)
        {
            throw new ArgumentException("截图标注区域尺寸无效。", nameof(logicalSelectionSize));
        }

        var scaleX = croppedBitmap.PixelWidth / logicalSelectionSize.Width;
        var scaleY = croppedBitmap.PixelHeight / logicalSelectionSize.Height;
        var thicknessScale = (scaleX + scaleY) / 2;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(
                croppedBitmap,
                new Rect(0, 0, croppedBitmap.PixelWidth, croppedBitmap.PixelHeight));

            foreach (var annotation in annotations)
            {
                var brush = new SolidColorBrush(annotation.Color);
                brush.Freeze();
                var pen = new Pen(brush, Math.Max(1, annotation.Thickness * thicknessScale))
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                pen.Freeze();

                switch (annotation)
                {
                    case PenScreenshotAnnotation freehand when freehand.Points.Count >= 2:
                        DrawFreehand(drawing, freehand.Points, pen, scaleX, scaleY);
                        break;
                    case RectangleScreenshotAnnotation rectangle:
                        drawing.DrawRoundedRectangle(
                            null,
                            pen,
                            new Rect(
                                rectangle.Bounds.X * scaleX,
                                rectangle.Bounds.Y * scaleY,
                                rectangle.Bounds.Width * scaleX,
                                rectangle.Bounds.Height * scaleY),
                            2 * scaleX,
                            2 * scaleY);
                        break;
                }
            }
        }

        var result = new RenderTargetBitmap(
            croppedBitmap.PixelWidth,
            croppedBitmap.PixelHeight,
            croppedBitmap.DpiX,
            croppedBitmap.DpiY,
            PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    private static void DrawFreehand(
        DrawingContext drawing,
        IReadOnlyList<Point> points,
        Pen pen,
        double scaleX,
        double scaleY)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(Scale(points[0], scaleX, scaleY), false, false);
            var scaledPoints = points
                .Skip(1)
                .Select(point => Scale(point, scaleX, scaleY))
                .ToArray();
            context.PolyLineTo(scaledPoints, true, false);
        }

        geometry.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
    }

    private static Point Scale(Point point, double scaleX, double scaleY)
    {
        return new Point(point.X * scaleX, point.Y * scaleY);
    }
}
