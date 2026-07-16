using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

internal static class CaptureFrameAnalyzer
{
    /// <summary>
    /// 某些显卡驱动初始化 Desktop Duplication 时会返回全零帧。
    /// 这里只识别接近纯黑的帧，避免把正常的深色界面误判为空帧。
    /// </summary>
    internal static bool IsLikelyBlank(BitmapSource source)
    {
        BitmapSource bitmap = source;
        if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Bgr32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            bitmap = converted;
        }

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return true;
        }

        const int bytesPerPixel = 4;
        var stride = checked(width * bytesPerPixel);
        var row = new byte[stride];
        var stepX = Math.Max(1, width / 96);
        var stepY = Math.Max(1, height / 96);
        var samples = 0;
        var nearlyBlack = 0;

        for (var y = 0; y < height; y += stepY)
        {
            bitmap.CopyPixels(new Int32Rect(0, y, width, 1), row, stride, 0);
            for (var x = 0; x < width; x += stepX)
            {
                var offset = x * bytesPerPixel;
                if (row[offset] <= 6 && row[offset + 1] <= 6 && row[offset + 2] <= 6)
                {
                    nearlyBlack++;
                }

                samples++;
            }
        }

        return samples > 0 && nearlyBlack >= samples * 0.997;
    }
}
