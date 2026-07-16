using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// 低成本的视口内容签名，用于识别用户是否回到了某个已采集位置。
/// 签名忽略上下固定区域，不参与精确位移计算。
/// </summary>
internal sealed class CaptureFrameSignature
{
    private const int Columns = 48;
    private const int Rows = 24;
    private readonly byte[] _samples;

    private CaptureFrameSignature(byte[] samples)
    {
        _samples = samples;
    }

    internal static CaptureFrameSignature Create(BitmapSource bitmap)
    {
        BitmapSource source = bitmap;
        if (bitmap.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            source = converted;
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var xStart = Math.Max(2, width / 100);
        var xEnd = Math.Max(xStart + 1, width - width / 12);
        var yStart = Math.Min(height - 1, Math.Max(2, height / 7));
        var yEnd = Math.Max(yStart + 1, height - height / 8);
        var samples = new byte[Columns * Rows];
        for (var row = 0; row < Rows; row++)
        {
            var y = yStart + (int)Math.Round((yEnd - yStart - 1) * row / (double)Math.Max(1, Rows - 1));
            for (var column = 0; column < Columns; column++)
            {
                var x = xStart + (int)Math.Round((xEnd - xStart - 1) * column / (double)Math.Max(1, Columns - 1));
                var offset = y * stride + x * 4;
                samples[row * Columns + column] = (byte)(
                    (pixels[offset] * 29 + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8);
            }
        }

        return new CaptureFrameSignature(samples);
    }

    internal double DifferenceFrom(CaptureFrameSignature other)
    {
        if (_samples.Length != other._samples.Length)
        {
            return double.MaxValue;
        }

        long difference = 0;
        for (var index = 0; index < _samples.Length; index++)
        {
            difference += Math.Abs(_samples[index] - other._samples[index]);
        }

        return (double)difference / _samples.Length;
    }
}
