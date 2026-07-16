using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// 视口中不会随正文滚动的边缘区域。顶部和底部只在最终长图中保留一次，
/// 右侧窄带用于排除浏览器滚动条。
/// </summary>
internal readonly record struct FixedViewportInsets(
    int Top,
    int Bottom,
    int Left = 0,
    int Right = 0)
{
    internal static FixedViewportInsets Empty => new(0, 0, 0, 0);
}

/// <summary>
/// 通过“同一屏幕坐标”和“滚动后的内容坐标”两种比较，识别固定顶栏与底栏。
/// </summary>
internal sealed class FixedViewportRegionEstimator
{
    private const int MaximumObservations = 16;
    private readonly List<FixedViewportInsets> _observations = new();

    internal FixedViewportInsets Current { get; private set; } = FixedViewportInsets.Empty;

    internal FixedViewportInsets Observe(
        BitmapSource previous,
        BitmapSource current,
        int scrollDelta)
    {
        if (scrollDelta == 0 ||
            previous.PixelWidth != current.PixelWidth ||
            previous.PixelHeight != current.PixelHeight)
        {
            return Current;
        }

        // 检测器统一按向下滚动处理。向上滚动时交换帧的先后次序。
        var upper = scrollDelta > 0 ? previous : current;
        var lower = scrollDelta > 0 ? current : previous;
        var detected = Detect(upper, lower, Math.Abs(scrollDelta));
        _observations.Add(detected);
        if (_observations.Count > MaximumObservations)
        {
            _observations.RemoveAt(0);
        }

        var width = previous.PixelWidth;
        Current = new FixedViewportInsets(
            Median(_observations.Select(value => value.Top)),
            Median(_observations.Select(value => value.Bottom)),
            0,
            Math.Clamp(width / 100, 3, 12));
        return Current;
    }

    internal static FixedViewportInsets Detect(
        BitmapSource upper,
        BitmapSource lower,
        int positiveScrollDelta)
    {
        if (upper.PixelWidth != lower.PixelWidth ||
            upper.PixelHeight != lower.PixelHeight)
        {
            throw new ArgumentException("固定区域检测帧尺寸不一致。");
        }

        var width = upper.PixelWidth;
        var height = upper.PixelHeight;
        var delta = Math.Clamp(positiveScrollDelta, 1, height - 1);
        var upperPixels = CopyBgraPixels(upper);
        var lowerPixels = CopyBgraPixels(lower);
        var topEvidence = new bool[Math.Min(height * 2 / 5, height - delta)];
        var bottomEvidence = new bool[Math.Min(height / 4, height - delta)];

        for (var y = 0; y < topEvidence.Length; y++)
        {
            var sameDifference = RowDifference(upperPixels, lowerPixels, width, y, y);
            var contentDifference = RowDifference(upperPixels, lowerPixels, width, y + delta, y);
            topEvidence[y] = IsFixedEvidence(sameDifference, contentDifference);
        }

        for (var index = 0; index < bottomEvidence.Length; index++)
        {
            var y = height - 1 - index;
            var sameDifference = RowDifference(upperPixels, lowerPixels, width, y, y);
            var contentDifference = RowDifference(upperPixels, lowerPixels, width, y, y - delta);
            bottomEvidence[index] = IsFixedEvidence(sameDifference, contentDifference);
        }

        var top = FindContiguousEdgeBand(topEvidence);
        var bottom = FindContiguousEdgeBand(bottomEvidence);
        if (top + bottom > height * 3 / 5)
        {
            return new FixedViewportInsets(0, 0, 0, Math.Clamp(width / 100, 3, 12));
        }

        return new FixedViewportInsets(
            top,
            bottom,
            0,
            Math.Clamp(width / 100, 3, 12));
    }

    private static bool IsFixedEvidence(double sameDifference, double contentDifference)
    {
        return sameDifference <= 28 &&
               contentDifference - sameDifference >= 4.5 &&
               sameDifference <= contentDifference * 0.74;
    }

    private static int FindContiguousEdgeBand(IReadOnlyList<bool> evidence)
    {
        if (evidence.Count == 0)
        {
            return 0;
        }

        var firstEvidence = -1;
        var lastEvidence = -1;
        var gap = 0;
        for (var index = 0; index < evidence.Count; index++)
        {
            if (evidence[index])
            {
                firstEvidence = firstEvidence < 0 ? index : firstEvidence;
                lastEvidence = index;
                gap = 0;
            }
            else if (firstEvidence >= 0)
            {
                gap++;
                if (gap > 8)
                {
                    break;
                }
            }

            // 固定区域必须从视口边缘附近开始，避免把正文中的巧合匹配当成顶栏。
            if (firstEvidence < 0 && index >= 12)
            {
                return 0;
            }
        }

        if (firstEvidence < 0 || lastEvidence - firstEvidence < 3)
        {
            return 0;
        }

        return lastEvidence + 1;
    }

    private static double RowDifference(
        byte[] first,
        byte[] second,
        int width,
        int firstY,
        int secondY)
    {
        var stride = width * 4;
        var xStart = Math.Max(2, width / 40);
        var xEnd = Math.Min(width - 2, width - width / 32);
        var xStep = Math.Max(1, width / 320);
        long difference = 0;
        var samples = 0;
        for (var x = xStart; x < xEnd; x += xStep)
        {
            var firstIndex = firstY * stride + x * 4;
            var secondIndex = secondY * stride + x * 4;
            difference += Math.Abs(first[firstIndex] - second[secondIndex]);
            difference += Math.Abs(first[firstIndex + 1] - second[secondIndex + 1]);
            difference += Math.Abs(first[firstIndex + 2] - second[secondIndex + 2]);
            samples += 3;
        }

        return samples == 0 ? double.MaxValue : (double)difference / samples;
    }

    private static byte[] CopyBgraPixels(BitmapSource bitmap)
    {
        BitmapSource source = bitmap;
        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            source = converted;
        }

        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static int Median(IEnumerable<int> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        return ordered[ordered.Length / 2];
    }
}
