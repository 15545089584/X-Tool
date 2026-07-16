using System.IO;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.History;

/// <summary>
/// 将截图持久化到 E 盘，并为历史页生成轻量缩略图。
/// </summary>
public sealed class ScreenshotHistoryStore
{
    public const string StorageDirectory = @"E:\截影\Screenshots";

    public Task<string> SaveAsync(BitmapSource bitmap, bool isLongCapture)
    {
        if (bitmap.CanFreeze && !bitmap.IsFrozen)
        {
            bitmap.Freeze();
        }

        return Task.Run(() =>
        {
            Directory.CreateDirectory(StorageDirectory);
            var kind = isLongCapture ? "长截图" : "截图";
            var fileName = $"截影_{kind}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png";
            var filePath = Path.Combine(StorageDirectory, fileName);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            encoder.Save(stream);
            return filePath;
        });
    }

    public Task<IReadOnlyList<ScreenshotHistoryItem>> LoadAsync(int maximumCount = 120)
    {
        return Task.Run<IReadOnlyList<ScreenshotHistoryItem>>(() =>
        {
            Directory.CreateDirectory(StorageDirectory);
            var items = new List<ScreenshotHistoryItem>();
            foreach (var filePath in Directory
                         .EnumerateFiles(StorageDirectory, "*.png", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(File.GetLastWriteTime)
                         .Take(maximumCount))
            {
                try
                {
                    items.Add(CreateItem(filePath));
                }
                catch
                {
                    // 单个损坏或正在写入的图片不会阻止其余历史记录显示。
                }
            }

            return items;
        });
    }

    private static ScreenshotHistoryItem CreateItem(string filePath)
    {
        int pixelWidth;
        int pixelHeight;
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            pixelWidth = decoder.Frames[0].PixelWidth;
            pixelHeight = decoder.Frames[0].PixelHeight;
        }

        var thumbnail = new BitmapImage();
        thumbnail.BeginInit();
        thumbnail.CacheOption = BitmapCacheOption.OnLoad;
        thumbnail.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        thumbnail.DecodePixelWidth = 360;
        thumbnail.UriSource = new Uri(filePath, UriKind.Absolute);
        thumbnail.EndInit();
        thumbnail.Freeze();

        var capturedAt = File.GetLastWriteTime(filePath);
        return new ScreenshotHistoryItem(
            filePath,
            Path.GetFileName(filePath),
            capturedAt.ToString("yyyy-MM-dd  HH:mm:ss"),
            $"{pixelWidth} × {pixelHeight}",
            thumbnail);
    }
}
