using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// 从截图原始像素中读取颜色。取色结果不经过遮罩和标注层，避免界面效果污染颜色。
/// </summary>
internal static class ScreenColorSampler
{
    internal readonly record struct ColorSample(byte Red, byte Green, byte Blue, byte Alpha)
    {
        internal string Hex => $"#{Red:X2}{Green:X2}{Blue:X2}";

        internal string Rgb => $"RGB {Red}, {Green}, {Blue}";

        internal Color ToMediaColor()
        {
            return Color.FromArgb(Alpha, Red, Green, Blue);
        }
    }

    internal static BitmapSource EnsureBgra32(BitmapSource bitmap)
    {
        if (bitmap.Format == PixelFormats.Bgra32)
        {
            return bitmap;
        }

        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    internal static ColorSample Sample(BitmapSource bgraBitmap, int pixelX, int pixelY)
    {
        if (bgraBitmap.PixelWidth <= 0 || bgraBitmap.PixelHeight <= 0)
        {
            throw new ArgumentException("取色位图尺寸无效。", nameof(bgraBitmap));
        }

        var x = Math.Clamp(pixelX, 0, bgraBitmap.PixelWidth - 1);
        var y = Math.Clamp(pixelY, 0, bgraBitmap.PixelHeight - 1);
        var pixel = new byte[4];
        bgraBitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);

        // BitmapSource.CopyPixels 使用 BGRA 字节顺序。
        return new ColorSample(pixel[2], pixel[1], pixel[0], pixel[3]);
    }
}

/// <summary>
/// 一次缓存整张截图的 BGRA 像素，供鼠标移动时低开销取色并生成放大镜邻域。
/// </summary>
internal sealed class ScreenColorBuffer
{
    private readonly byte[] _pixels;
    private readonly int _stride;

    internal ScreenColorBuffer(BitmapSource bitmap)
    {
        var source = ScreenColorSampler.EnsureBgra32(bitmap);
        PixelWidth = source.PixelWidth;
        PixelHeight = source.PixelHeight;
        _stride = PixelWidth * 4;
        _pixels = new byte[_stride * PixelHeight];
        source.CopyPixels(_pixels, _stride, 0);
    }

    internal int PixelWidth { get; }

    internal int PixelHeight { get; }

    internal ScreenColorSampler.ColorSample Sample(int pixelX, int pixelY)
    {
        var x = Math.Clamp(pixelX, 0, PixelWidth - 1);
        var y = Math.Clamp(pixelY, 0, PixelHeight - 1);
        var offset = y * _stride + x * 4;
        return new ScreenColorSampler.ColorSample(
            _pixels[offset + 2],
            _pixels[offset + 1],
            _pixels[offset],
            _pixels[offset + 3]);
    }

    internal BitmapSource CreateNeighborhood(int centerX, int centerY, int radius)
    {
        if (radius < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }

        var size = radius * 2 + 1;
        var outputStride = size * 4;
        var output = new byte[outputStride * size];
        for (var row = 0; row < size; row++)
        {
            var sourceY = Math.Clamp(centerY + row - radius, 0, PixelHeight - 1);
            for (var column = 0; column < size; column++)
            {
                var sourceX = Math.Clamp(centerX + column - radius, 0, PixelWidth - 1);
                var sourceOffset = sourceY * _stride + sourceX * 4;
                var targetOffset = row * outputStride + column * 4;
                Buffer.BlockCopy(_pixels, sourceOffset, output, targetOffset, 4);
            }
        }

        var neighborhood = BitmapSource.Create(
            size,
            size,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            output,
            outputStride);
        neighborhood.Freeze();
        return neighborhood;
    }
}
