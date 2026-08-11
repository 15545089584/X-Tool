using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// 按检测到的滚动距离，仅追加每帧底部的新内容。
/// </summary>
internal static class VerticalBitmapStitcher
{
    internal const int MaximumBitmapHeight = 30_000;

    internal sealed record PositionedFrame(BitmapSource Bitmap, int ContentOffset);

    internal sealed record PositionedSlice(
        BitmapSource Bitmap,
        Int32Rect SourceRect,
        int DestinationY);

    internal sealed record PositionedLayout(
        int PixelWidth,
        int PixelHeight,
        IReadOnlyList<PositionedSlice> Slices);

    internal static BitmapSource Stitch(IReadOnlyList<BitmapSource> frames, IReadOnlyList<int> scrollDeltas)
    {
        if (frames.Count == 0 || scrollDeltas.Count != frames.Count - 1)
        {
            throw new ArgumentException("长截图帧与滚动距离数量不匹配。");
        }

        var width = frames[0].PixelWidth;
        var frameHeight = frames[0].PixelHeight;
        var requestedHeight = frameHeight + scrollDeltas.Sum();
        var outputHeight = Math.Min(MaximumBitmapHeight, requestedHeight);
        var output = new WriteableBitmap(width, outputHeight, 96, 96, PixelFormats.Bgra32, null);
        var destinationY = 0;

        WriteRegion(output, frames[0], new Int32Rect(0, 0, width, Math.Min(frameHeight, outputHeight)), destinationY);
        destinationY += Math.Min(frameHeight, outputHeight);

        for (var index = 1; index < frames.Count && destinationY < outputHeight; index++)
        {
            var delta = Math.Clamp(scrollDeltas[index - 1], 1, frameHeight);
            var appendHeight = Math.Min(delta, outputHeight - destinationY);
            var sourceY = frameHeight - delta;
            WriteRegion(output, frames[index], new Int32Rect(0, sourceY, width, appendHeight), destinationY);
            destinationY += appendHeight;
        }

        output.Freeze();
        return output;
    }

    /// <summary>
    /// 按内容坐标拼接帧。重复经过的视口只会覆盖已有坐标，不会再次增加图片高度。
    /// 相邻帧在重叠区内部交接，避免把每一帧底部的固定分隔线重复写入结果。
    /// </summary>
    internal static BitmapSource StitchPositioned(
        IReadOnlyList<PositionedFrame> frames,
        FixedViewportInsets insets = default)
    {
        var layout = BuildPositionedLayout(frames, insets);
        var output = new WriteableBitmap(
            layout.PixelWidth,
            layout.PixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null);
        foreach (var slice in layout.Slices)
        {
            WriteRegion(output, slice.Bitmap, slice.SourceRect, slice.DestinationY);
        }

        output.Freeze();
        return output;
    }

    /// <summary>
    /// 最终拼接与实时缩略图共享同一套切片布局，保证预览不会再次绘制固定栏。
    /// </summary>
    internal static PositionedLayout BuildPositionedLayout(
        IReadOnlyList<PositionedFrame> frames,
        FixedViewportInsets insets = default)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("没有可用于拼接的长截图帧。");
        }

        var sourceWidth = frames[0].Bitmap.PixelWidth;
        var frameHeight = frames[0].Bitmap.PixelHeight;
        if (frames.Any(frame =>
                frame.Bitmap.PixelWidth != sourceWidth ||
                frame.Bitmap.PixelHeight != frameHeight))
        {
            throw new ArgumentException("长截图帧尺寸不一致。");
        }

        var top = Math.Clamp(insets.Top, 0, frameHeight / 2);
        var bottom = Math.Clamp(insets.Bottom, 0, frameHeight / 3);
        if (top + bottom >= frameHeight - 8)
        {
            top = 0;
            bottom = 0;
        }

        var left = Math.Clamp(insets.Left, 0, sourceWidth / 4);
        var right = Math.Clamp(insets.Right, 0, sourceWidth / 4);
        if (left + right >= sourceWidth - 8)
        {
            left = 0;
            right = 0;
        }

        var outputWidth = sourceWidth - left - right;
        var orderedFrames = frames.OrderBy(frame => frame.ContentOffset).ToList();
        var minimumBodyOffset = orderedFrames.Min(frame => frame.ContentOffset + top);
        var maximumBodyOffset = orderedFrames.Max(frame => frame.ContentOffset + frameHeight - bottom);
        var maximumBodyHeight = Math.Max(1, MaximumBitmapHeight - top - bottom);
        maximumBodyOffset = Math.Min(maximumBodyOffset, minimumBodyOffset + maximumBodyHeight);
        var bodyHeight = Math.Max(1, maximumBodyOffset - minimumBodyOffset);
        var outputHeight = top + bodyHeight + bottom;
        var slices = new List<PositionedSlice>(orderedFrames.Count + 2);

        if (top > 0)
        {
            slices.Add(new PositionedSlice(
                orderedFrames[0].Bitmap,
                new Int32Rect(left, 0, outputWidth, top),
                0));
        }

        var seams = new int[Math.Max(0, orderedFrames.Count - 1)];
        // 相邻接缝会连续使用同一帧。只保留上一帧和当前帧的像素缓存，
        // 避免每个接缝都重复把两张完整截图复制成 BGRA 数组。
        var upperPixels = CopyBgraPixels(orderedFrames[0].Bitmap);
        for (var index = 0; index < seams.Length; index++)
        {
            var lowerPixels = CopyBgraPixels(orderedFrames[index + 1].Bitmap);
            seams[index] = FindBestSeam(
                orderedFrames[index],
                orderedFrames[index + 1],
                upperPixels,
                lowerPixels,
                top,
                bottom);
            upperPixels = lowerPixels;
        }

        for (var index = 0; index < orderedFrames.Count; index++)
        {
            var frame = orderedFrames[index];
            var frameBodyStart = frame.ContentOffset + top;
            var frameBodyEnd = frame.ContentOffset + frameHeight - bottom;
            var frameStart = index == 0 ? minimumBodyOffset : seams[index - 1];
            var frameEnd = index == orderedFrames.Count - 1 ? maximumBodyOffset : seams[index];
            frameStart = Math.Max(frameStart, frameBodyStart);
            frameEnd = Math.Min(frameEnd, Math.Min(frameBodyEnd, maximumBodyOffset));
            if (frameEnd <= frameStart)
            {
                continue;
            }

            slices.Add(new PositionedSlice(
                frame.Bitmap,
                new Int32Rect(
                    left,
                    frameStart - frame.ContentOffset,
                    outputWidth,
                    frameEnd - frameStart),
                top + frameStart - minimumBodyOffset));
        }

        if (bottom > 0 && outputHeight <= MaximumBitmapHeight)
        {
            slices.Add(new PositionedSlice(
                orderedFrames[^1].Bitmap,
                new Int32Rect(left, frameHeight - bottom, outputWidth, bottom),
                top + bodyHeight));
        }

        return new PositionedLayout(outputWidth, outputHeight, slices);
    }

    private static int FindBestSeam(
        PositionedFrame upper,
        PositionedFrame lower,
        byte[] upperPixels,
        byte[] lowerPixels,
        int fixedTop,
        int fixedBottom)
    {
        var overlapStart = Math.Max(
            upper.ContentOffset + fixedTop,
            lower.ContentOffset + fixedTop);
        var overlapEnd = Math.Min(
            upper.ContentOffset + upper.Bitmap.PixelHeight - fixedBottom,
            lower.ContentOffset + lower.Bitmap.PixelHeight - fixedBottom);
        if (overlapEnd <= overlapStart)
        {
            return lower.ContentOffset;
        }

        var overlapHeight = overlapEnd - overlapStart;
        var margin = Math.Min(Math.Max(4, overlapHeight / 5), Math.Max(0, overlapHeight / 2 - 1));
        var searchStart = overlapStart + margin;
        var searchEnd = overlapEnd - margin;
        if (searchEnd <= searchStart)
        {
            return overlapStart + overlapHeight / 2;
        }

        var width = upper.Bitmap.PixelWidth;
        var stride = width * 4;
        var xStart = Math.Max(2, width / 32);
        var xEnd = Math.Min(width - 2, width - width / 16);
        var xStep = Math.Max(1, width / 280);
        var bestSeam = searchStart;
        var bestScore = double.MaxValue;
        var overlapCenter = overlapStart + overlapHeight / 2;

        for (var contentY = searchStart; contentY < searchEnd; contentY++)
        {
            var upperY = contentY - upper.ContentOffset;
            var lowerY = contentY - lower.ContentOffset;
            long difference = 0;
            var samples = 0;
            for (var rowOffset = -1; rowOffset <= 1; rowOffset++)
            {
                var firstY = Math.Clamp(upperY + rowOffset, 0, upper.Bitmap.PixelHeight - 1);
                var secondY = Math.Clamp(lowerY + rowOffset, 0, lower.Bitmap.PixelHeight - 1);
                for (var x = xStart; x < xEnd; x += xStep)
                {
                    var first = firstY * stride + x * 4;
                    var second = secondY * stride + x * 4;
                    difference += Math.Abs(upperPixels[first] - lowerPixels[second]);
                    difference += Math.Abs(upperPixels[first + 1] - lowerPixels[second + 1]);
                    difference += Math.Abs(upperPixels[first + 2] - lowerPixels[second + 2]);
                    samples += 3;
                }
            }

            var rawScore = samples == 0 ? double.MaxValue : (double)difference / samples;
            // 内容同样匹配时优先在重叠区中部交接，同时远离两帧固定的顶栏和底栏。
            var score = rawScore + Math.Abs(contentY - overlapCenter) * 0.0005;
            if (score < bestScore)
            {
                bestScore = score;
                bestSeam = contentY;
            }
        }

        return bestSeam;
    }

    private static byte[] CopyBgraPixels(BitmapSource bitmap)
    {
        BitmapSource source = bitmap;
        if (bitmap.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            source = converted;
        }

        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void WriteRegion(WriteableBitmap destination, BitmapSource source, Int32Rect sourceRect, int destinationY)
    {
        BitmapSource bgraSource = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            bgraSource = converted;
        }

        var stride = sourceRect.Width * 4;
        var pixels = new byte[stride * sourceRect.Height];
        bgraSource.CopyPixels(sourceRect, pixels, stride, 0);
        destination.WritePixels(
            new Int32Rect(0, destinationY, sourceRect.Width, sourceRect.Height),
            pixels,
            stride,
            0);
    }
}
