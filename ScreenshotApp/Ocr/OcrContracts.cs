using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Ocr;

public enum OcrExecutionProvider
{
    Cpu,
    DirectML,
    WindowsML
}

public sealed class OcrOptions
{
    public bool UseOrientationClassification { get; init; }

    public string Language { get; init; } = "zh-en";

    public OcrExecutionProvider ExecutionProvider { get; init; } = OcrExecutionProvider.Cpu;

    public float ConfidenceThreshold { get; init; } = 0.5f;
}

public sealed record OcrTextBlock(
    string Text,
    float Confidence,
    int X,
    int Y,
    int Width,
    int Height);

public sealed record OcrResult(
    string Text,
    IReadOnlyList<OcrTextBlock> Blocks,
    TimeSpan Elapsed)
{
    public float AverageConfidence => Blocks.Count == 0
        ? 0
        : Blocks.Average(block => block.Confidence);
}

public interface IOcrEngine
{
    Task<OcrResult> RecognizeAsync(
        OcrImage image,
        OcrOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// 可安全传入后台线程的 BGRA32 OCR 输入，不再依赖 WPF DispatcherObject。
/// </summary>
public sealed class OcrImage
{
    public OcrImage(int pixelWidth, int pixelHeight, byte[] bgraPixels)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "OCR 图片尺寸必须大于零。");
        }

        if (bgraPixels.Length != pixelWidth * pixelHeight * 4)
        {
            throw new ArgumentException("BGRA32 像素长度与图片尺寸不匹配。", nameof(bgraPixels));
        }

        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        BgraPixels = bgraPixels;
    }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public byte[] BgraPixels { get; }

    public static OcrImage FromBitmapSource(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bitmap = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return new OcrImage(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }
}

internal static class OcrTextLayout
{
    internal static string Compose(IReadOnlyList<OcrTextBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        var ordered = blocks
            .OrderBy(block => block.Y + block.Height / 2d)
            .ThenBy(block => block.X)
            .ToArray();
        var lines = new List<List<OcrTextBlock>>();
        foreach (var block in ordered)
        {
            var currentLine = lines.LastOrDefault();
            if (currentLine is null)
            {
                lines.Add(new List<OcrTextBlock> { block });
                continue;
            }

            var centerY = block.Y + block.Height / 2d;
            var lineCenter = currentLine.Average(item => item.Y + item.Height / 2d);
            var tolerance = Math.Max(4, currentLine.Average(item => item.Height) * 0.5);
            if (Math.Abs(centerY - lineCenter) <= tolerance)
            {
                currentLine.Add(block);
            }
            else
            {
                lines.Add(new List<OcrTextBlock> { block });
            }
        }

        return string.Join(
            Environment.NewLine,
            lines.Select(line => string.Join(
                " ",
                line.OrderBy(item => item.X)
                    .Select(item => item.Text.Trim())
                    .Where(text => text.Length > 0))));
    }
}
