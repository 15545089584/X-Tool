using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// 将已定位的长截图片段直接绘制为轻量缩略图，避免在每次滚动时生成完整大图。
/// </summary>
internal static class ScrollCapturePreviewRenderer
{
    internal const int DefaultPreviewWidth = 112;
    internal const int MaximumPreviewHeight = 390;
    internal const int MinimumPreviewHeight = 88;

    internal static BitmapSource Render(
        IReadOnlyList<VerticalBitmapStitcher.PositionedFrame> frames,
        FixedViewportInsets insets = default,
        int previewWidth = DefaultPreviewWidth,
        int maximumPreviewHeight = MaximumPreviewHeight)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("没有可用于预览的长截图片段。", nameof(frames));
        }

        var layout = VerticalBitmapStitcher.BuildPositionedLayout(frames, insets);
        return RenderLayout(layout, previewWidth, maximumPreviewHeight);
    }

    /// <summary>
    /// 根据已经在后台计算好的拼接布局绘制预览。该步骤只负责轻量绘制，
    /// 不再在界面线程重复执行像素匹配和接缝搜索。
    /// </summary>
    internal static BitmapSource RenderLayout(
        VerticalBitmapStitcher.PositionedLayout layout,
        int previewWidth = DefaultPreviewWidth,
        int maximumPreviewHeight = MaximumPreviewHeight)
    {
        previewWidth = Math.Max(48, previewWidth);
        maximumPreviewHeight = Math.Max(MinimumPreviewHeight, maximumPreviewHeight);
        var contentHeight = layout.PixelHeight;

        // 未达到高度上限前保持原始宽高比，缩略图会随采集范围自然拉长；
        // 达到上限后压缩纵向比例，始终保留整张长图的全局轮廓。
        var naturalHeight = (int)Math.Ceiling(contentHeight * (double)previewWidth / layout.PixelWidth);
        var previewHeight = Math.Clamp(
            naturalHeight,
            MinimumPreviewHeight,
            maximumPreviewHeight);
        var scaleY = (double)previewHeight / contentHeight;

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(247, 249, 252)),
                null,
                new Rect(0, 0, previewWidth, previewHeight));

            foreach (var slice in layout.Slices)
            {
                var cropped = new CroppedBitmap(slice.Bitmap, slice.SourceRect);
                cropped.Freeze();
                drawing.DrawImage(
                    cropped,
                    new Rect(
                        0,
                        slice.DestinationY * scaleY,
                        previewWidth,
                        slice.SourceRect.Height * scaleY));
            }
        }

        var bitmap = new RenderTargetBitmap(
            previewWidth,
            previewHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
