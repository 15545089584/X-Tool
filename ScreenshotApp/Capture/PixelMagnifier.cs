using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// 圆形像素放大镜。使用最近邻缩放保留色块边界，并以中心框标记实际取色像素。
/// </summary>
public sealed class PixelMagnifier : FrameworkElement
{
    private const int DefaultPixelCount = 11;
    private BitmapSource? _pixels;

    public PixelMagnifier()
    {
        Width = 132;
        Height = 132;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    internal void SetPixels(BitmapSource pixels)
    {
        _pixels = pixels;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var center = new Point(width / 2, height / 2);
        var radius = Math.Max(1, Math.Min(width, height) / 2 - 1.5);
        var circle = new EllipseGeometry(center, radius, radius);
        drawingContext.PushClip(circle);
        drawingContext.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(246, 248, 251)),
            null,
            new Rect(0, 0, width, height));

        var pixelCount = _pixels?.PixelWidth ?? DefaultPixelCount;
        if (_pixels is not null)
        {
            drawingContext.DrawImage(_pixels, new Rect(0, 0, width, height));
        }

        var cellWidth = width / pixelCount;
        var cellHeight = height / pixelCount;
        var gridPen = new Pen(
            new SolidColorBrush(Color.FromArgb(145, 66, 75, 88)),
            0.75);
        gridPen.Freeze();
        for (var index = 1; index < pixelCount; index++)
        {
            var x = index * cellWidth;
            var y = index * cellHeight;
            drawingContext.DrawLine(gridPen, new Point(x, 0), new Point(x, height));
            drawingContext.DrawLine(gridPen, new Point(0, y), new Point(width, y));
        }

        var centerIndex = pixelCount / 2;
        var centerCell = new Rect(
            centerIndex * cellWidth,
            centerIndex * cellHeight,
            cellWidth,
            cellHeight);
        var whitePen = new Pen(Brushes.White, 3.5);
        whitePen.Freeze();
        var darkPen = new Pen(new SolidColorBrush(Color.FromRgb(20, 24, 30)), 1.5);
        darkPen.Freeze();
        drawingContext.DrawRectangle(null, whitePen, centerCell);
        drawingContext.DrawRectangle(null, darkPen, centerCell);
        drawingContext.Pop();

        var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(54, 63, 75)), 2.5);
        borderPen.Freeze();
        drawingContext.DrawEllipse(null, borderPen, center, radius, radius);
    }
}
