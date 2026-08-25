using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

internal enum ScreenshotAnnotationTool
{
    None,
    Pen,
    Shape,
    ColorPicker
}

internal enum AnnotationShape
{
    Rectangle,
    RoundedRectangle,
    Ellipse,
    Diamond,
    Triangle,
    Pentagon,
    Hexagon,
    Star,
    Arrow,
    Line
}

internal abstract record ScreenshotAnnotation(Color Color, double Thickness);

internal sealed record PenScreenshotAnnotation(
    IReadOnlyList<Point> Points,
    Color Color,
    double Thickness) : ScreenshotAnnotation(Color, Thickness);

internal sealed record ShapeScreenshotAnnotation(
    AnnotationShape Shape,
    Rect Bounds,
    Color Color,
    double Thickness) : ScreenshotAnnotation(Color, Thickness);

internal sealed record LineScreenshotAnnotation(
    AnnotationShape Shape,
    Point Start,
    Point End,
    Color Color,
    double Thickness) : ScreenshotAnnotation(Color, Thickness);

/// <summary>
/// 提供实时预览和最终截图共用的多边形几何，避免两套绘制规则出现偏差。
/// </summary>
internal static class AnnotationShapeGeometry
{
    internal static IReadOnlyList<Point> GetPolygonPoints(AnnotationShape shape, Rect bounds)
    {
        return shape switch
        {
            AnnotationShape.Diamond => new[]
            {
                new Point(bounds.Left + bounds.Width / 2, bounds.Top),
                new Point(bounds.Right, bounds.Top + bounds.Height / 2),
                new Point(bounds.Left + bounds.Width / 2, bounds.Bottom),
                new Point(bounds.Left, bounds.Top + bounds.Height / 2)
            },
            AnnotationShape.Triangle => new[]
            {
                new Point(bounds.Left + bounds.Width / 2, bounds.Top),
                new Point(bounds.Right, bounds.Bottom),
                new Point(bounds.Left, bounds.Bottom)
            },
            AnnotationShape.Pentagon => CreateRegularPolygon(bounds, 5),
            AnnotationShape.Hexagon => CreateRegularPolygon(bounds, 6),
            AnnotationShape.Star => CreateStar(bounds),
            _ => Array.Empty<Point>()
        };
    }

    private static IReadOnlyList<Point> CreateRegularPolygon(Rect bounds, int sides)
    {
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var radiusX = bounds.Width / 2;
        var radiusY = bounds.Height / 2;
        return Enumerable.Range(0, sides)
            .Select(index =>
            {
                var angle = -Math.PI / 2 + index * Math.PI * 2 / sides;
                return new Point(
                    center.X + Math.Cos(angle) * radiusX,
                    center.Y + Math.Sin(angle) * radiusY);
            })
            .ToArray();
    }

    private static IReadOnlyList<Point> CreateStar(Rect bounds)
    {
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var radiusX = bounds.Width / 2;
        var radiusY = bounds.Height / 2;
        return Enumerable.Range(0, 10)
            .Select(index =>
            {
                var angle = -Math.PI / 2 + index * Math.PI / 5;
                var radius = index % 2 == 0 ? 1 : 0.44;
                return new Point(
                    center.X + Math.Cos(angle) * radiusX * radius,
                    center.Y + Math.Sin(angle) * radiusY * radius);
            })
            .ToArray();
    }
}

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
                    case ShapeScreenshotAnnotation shape:
                        DrawShape(drawing, shape, pen, scaleX, scaleY);
                        break;
                    case LineScreenshotAnnotation line:
                        DrawLine(drawing, line, pen, scaleX, scaleY);
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

    private static void DrawShape(
        DrawingContext drawing,
        ShapeScreenshotAnnotation shape,
        Pen pen,
        double scaleX,
        double scaleY)
    {
        var bounds = new Rect(
            shape.Bounds.X * scaleX,
            shape.Bounds.Y * scaleY,
            shape.Bounds.Width * scaleX,
            shape.Bounds.Height * scaleY);
        switch (shape.Shape)
        {
            case AnnotationShape.Rectangle:
                drawing.DrawRoundedRectangle(null, pen, bounds, 2 * scaleX, 2 * scaleY);
                break;
            case AnnotationShape.RoundedRectangle:
                var radius = Math.Max(4, Math.Min(bounds.Width, bounds.Height) * 0.18);
                drawing.DrawRoundedRectangle(null, pen, bounds, radius, radius);
                break;
            case AnnotationShape.Ellipse:
                drawing.DrawEllipse(null, pen, new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), bounds.Width / 2, bounds.Height / 2);
                break;
            case AnnotationShape.Diamond:
            case AnnotationShape.Triangle:
            case AnnotationShape.Pentagon:
            case AnnotationShape.Hexagon:
            case AnnotationShape.Star:
                DrawPolygon(drawing, pen, AnnotationShapeGeometry.GetPolygonPoints(shape.Shape, bounds));
                break;
        }
    }

    private static void DrawPolygon(DrawingContext drawing, Pen pen, IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, true);
            context.PolyLineTo(points.Skip(1).ToArray(), true, true);
        }

        geometry.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
    }

    private static void DrawLine(DrawingContext drawing, LineScreenshotAnnotation line, Pen pen, double scaleX, double scaleY)
    {
        var start = Scale(line.Start, scaleX, scaleY);
        var end = Scale(line.End, scaleX, scaleY);
        drawing.DrawLine(pen, start, end);
        if (line.Shape != AnnotationShape.Arrow)
        {
            return;
        }

        var vector = start - end;
        if (vector.Length < 1)
        {
            return;
        }

        vector.Normalize();
        var headLength = Math.Max(16, pen.Thickness * 4.5);
        var left = end + Rotate(vector, 28) * headLength;
        var right = end + Rotate(vector, -28) * headLength;
        drawing.DrawLine(pen, end, left);
        drawing.DrawLine(pen, end, right);
    }

    private static Vector Rotate(Vector vector, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Vector(
            vector.X * Math.Cos(radians) - vector.Y * Math.Sin(radians),
            vector.X * Math.Sin(radians) + vector.Y * Math.Cos(radians));
    }

    private static Point Scale(Point point, double scaleX, double scaleY)
    {
        return new Point(point.X * scaleX, point.Y * scaleY);
    }
}
