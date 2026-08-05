using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using ScreenshotApp.Recording;
using ScreenshotApp.Settings;

namespace ScreenshotApp.History;

/// <summary>
/// 将图片、文字和录像按用户设置的目录保存，并提供统一的历史页记录。
/// </summary>
public sealed class ScreenshotHistoryStore
{
    private readonly AppPreferences _preferences;

    public ScreenshotHistoryStore(AppPreferences preferences)
    {
        _preferences = preferences;
    }

    public Task<string> SaveAsync(BitmapSource bitmap, bool isLongCapture)
    {
        if (bitmap.CanFreeze && !bitmap.IsFrozen)
        {
            bitmap.Freeze();
        }

        return Task.Run(() =>
        {
            var directory = isLongCapture
                ? _preferences.LongScreenshotDirectory
                : _preferences.ScreenshotDirectory;
            Directory.CreateDirectory(directory);
            var kind = isLongCapture ? "长截图" : "截图";
            var fileName = $"截影_{kind}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png";
            var filePath = Path.Combine(directory, fileName);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            encoder.Save(stream);
            return filePath;
        });
    }

    public Task<string> SaveTextAsync(HistoryTextContent content)
    {
        if (content.Kind is not (HistoryEntryKind.TextExtraction or HistoryEntryKind.Translation))
        {
            throw new ArgumentOutOfRangeException(nameof(content), "只有文字提取和翻译结果可以写入文本历史。 ");
        }

        return Task.Run(async () =>
        {
            var directory = content.Kind == HistoryEntryKind.TextExtraction
                ? _preferences.TextExtractionDirectory
                : _preferences.TranslationDirectory;
            var prefix = content.Kind == HistoryEntryKind.TextExtraction ? "文字提取" : "翻译";
            Directory.CreateDirectory(directory);
            var filePath = Path.Combine(directory, $"截影_{prefix}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.txt");
            await File.WriteAllTextAsync(filePath, content.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return filePath;
        });
    }

    public Task<IReadOnlyList<ScreenshotHistoryItem>> LoadAsync(int maximumCount = 160, bool imagesOnly = false)
    {
        return Task.Run<IReadOnlyList<ScreenshotHistoryItem>>(() =>
        {
            var items = new List<ScreenshotHistoryItem>();
            var loadedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            LoadImageEntries(items, _preferences.ScreenshotDirectory, HistoryEntryKind.Screenshot, loadedFiles, detectLongCapture: true, newestOnly: imagesOnly ? 80 : null);
            LoadImageEntries(items, _preferences.LongScreenshotDirectory, HistoryEntryKind.LongScreenshot, loadedFiles, newestOnly: imagesOnly ? 80 : null);
            if (!imagesOnly)
            {
                LoadTextEntries(items, _preferences.TextExtractionDirectory, HistoryEntryKind.TextExtraction, "文字提取", loadedFiles);
                LoadTextEntries(items, _preferences.TranslationDirectory, HistoryEntryKind.Translation, "翻译", loadedFiles);
            }
            LoadRecordingEntries(items, _preferences.RecordingDirectory, newestOnly: imagesOnly ? 40 : null);
            LoadExternalClipboardEntries(items, _preferences.ClipboardDirectory, loadedFiles, imagesOnly: imagesOnly);
            return items
                .OrderByDescending(item => item.CapturedAt)
                .Take(maximumCount)
                .ToArray();
        });
    }

    private static void LoadImageEntries(
        ICollection<ScreenshotHistoryItem> items,
        string directory,
        HistoryEntryKind expectedKind,
        ISet<string> loadedFiles,
        bool detectLongCapture = false,
        int? newestOnly = null)
    {
        Directory.CreateDirectory(directory);
        var files = Directory.EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly);
        if (newestOnly.HasValue)
        {
            // 仅图片模式只取最近文件，避免为历史全量解码缩略图。
            files = files
                .OrderByDescending(file => File.GetLastWriteTimeUtc(file))
                .Take(newestOnly.Value);
        }
        foreach (var filePath in files)
        {
            try
            {
                var fullPath = Path.GetFullPath(filePath);
                if (!loadedFiles.Add(fullPath))
                {
                    continue;
                }

                var kind = detectLongCapture && Path.GetFileName(filePath).Contains("长截图", StringComparison.OrdinalIgnoreCase)
                    ? HistoryEntryKind.LongScreenshot
                    : expectedKind;
                items.Add(CreateImageItem(filePath, kind));
            }
            catch
            {
                // 单个损坏或正在写入的图片不会阻止其余历史记录显示。
            }
        }
    }

    private static void LoadTextEntries(
        ICollection<ScreenshotHistoryItem> items,
        string directory,
        HistoryEntryKind kind,
        string kindText,
        ISet<string> loadedFiles)
    {
        Directory.CreateDirectory(directory);
        foreach (var filePath in Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (!loadedFiles.Add(Path.GetFullPath(filePath)))
                {
                    continue;
                }

                var content = File.ReadAllText(filePath, Encoding.UTF8);
                var timestamp = File.GetLastWriteTime(filePath);
                items.Add(new ScreenshotHistoryItem(
                    kind,
                    kindText,
                    filePath,
                    Path.GetFileName(filePath),
                    timestamp,
                    timestamp.ToString("yyyy-MM-dd  HH:mm:ss"),
                    $"{content.Count(character => !char.IsWhiteSpace(character)):N0} 个字符",
                    CreatePreview(content),
                    null));
            }
            catch
            {
                // 单个损坏的文本记录不影响其余历史条目。
            }
        }
    }

    private static void LoadRecordingEntries(ICollection<ScreenshotHistoryItem> items, string directory, int? newestOnly = null)
    {
        Directory.CreateDirectory(directory);
        var files = Directory.EnumerateFiles(directory, "*.mp4", SearchOption.TopDirectoryOnly);
        if (newestOnly.HasValue)
        {
            files = files
                .OrderByDescending(file => File.GetLastWriteTimeUtc(file))
                .Take(newestOnly.Value);
        }
        foreach (var filePath in files)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var timestamp = fileInfo.LastWriteTime;
                var coverPath = Path.Combine(
                    directory,
                    "Covers",
                    $"{Path.GetFileNameWithoutExtension(filePath)}.png");
                items.Add(new ScreenshotHistoryItem(
                    HistoryEntryKind.ScreenRecording,
                    "屏幕录制",
                    filePath,
                    fileInfo.Name,
                    timestamp,
                    timestamp.ToString("yyyy-MM-dd  HH:mm:ss"),
                    $"{Math.Max(1, fileInfo.Length / 1024d / 1024d):0.0} MB · MP4",
                    "点击即可播放这段屏幕录制",
                    File.Exists(coverPath) ? LoadThumbnail(coverPath) : null));
            }
            catch
            {
                // 删除中的录像文件会在下次刷新时自然消失。
            }
        }
    }

    private static void LoadExternalClipboardEntries(
        ICollection<ScreenshotHistoryItem> items,
        string directory,
        ISet<string> loadedFiles,
        bool imagesOnly = false)
    {
        LoadImageEntries(items, directory, HistoryEntryKind.ExternalClipboard, loadedFiles, newestOnly: imagesOnly ? 80 : null);
        if (!imagesOnly)
        {
            LoadTextEntries(items, directory, HistoryEntryKind.ExternalClipboard, "外部复制", loadedFiles);
        }
    }

    private static ScreenshotHistoryItem CreateImageItem(string filePath, HistoryEntryKind kind)
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

        var thumbnail = LoadThumbnail(filePath);

        var capturedAt = File.GetLastWriteTime(filePath);
        return new ScreenshotHistoryItem(
            kind,
            kind switch
            {
                HistoryEntryKind.LongScreenshot => "长截图",
                HistoryEntryKind.ExternalClipboard => "外部复制",
                _ => "普通截图"
            },
            filePath,
            Path.GetFileName(filePath),
            capturedAt,
            capturedAt.ToString("yyyy-MM-dd  HH:mm:ss"),
            $"{pixelWidth} × {pixelHeight}",
            string.Empty,
            thumbnail);
    }

    private static BitmapSource LoadThumbnail(string filePath)
    {
        var thumbnail = new BitmapImage();
        thumbnail.BeginInit();
        thumbnail.CacheOption = BitmapCacheOption.OnLoad;
        thumbnail.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        thumbnail.DecodePixelWidth = 360;
        thumbnail.UriSource = new Uri(filePath, UriKind.Absolute);
        thumbnail.EndInit();
        thumbnail.Freeze();
        return thumbnail;
    }

    private static string CreatePreview(string content)
    {
        var normalized = string.Join(" ", content
            .Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized;
    }

    public Task<string> SaveClipboardTextAsync(string content)
    {
        return Task.Run(async () =>
        {
            Directory.CreateDirectory(_preferences.ClipboardDirectory);
            var filePath = Path.Combine(_preferences.ClipboardDirectory, $"外部复制_文本_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.txt");
            await File.WriteAllTextAsync(filePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return filePath;
        });
    }

    public Task<string> SaveClipboardImageAsync(BitmapSource bitmap)
    {
        if (bitmap.CanFreeze && !bitmap.IsFrozen)
        {
            bitmap.Freeze();
        }

        return Task.Run(() =>
        {
            Directory.CreateDirectory(_preferences.ClipboardDirectory);
            var filePath = Path.Combine(_preferences.ClipboardDirectory, $"外部复制_图片_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            encoder.Save(stream);
            return filePath;
        });
    }

}
