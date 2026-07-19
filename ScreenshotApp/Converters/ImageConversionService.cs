using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Converters;

/// <summary>图片处理工作台的本地编码器，仅处理用户主动选择的文件并始终生成新文件。</summary>
internal static class ImageConversionService
{
    internal static BitmapSource? CreatePreview(string inputPath, ImageOutputFormat outputFormat, int scalePercent, int jpegQuality)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(inputPath, UriKind.Absolute);
            image.DecodePixelWidth = 640;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            var targetWidth = Math.Max(1, (int)Math.Round(image.PixelWidth * Math.Clamp(scalePercent, 10, 200) / 100d));
            var targetHeight = Math.Max(1, (int)Math.Round(image.PixelHeight * Math.Clamp(scalePercent, 10, 200) / 100d));
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                if (outputFormat == ImageOutputFormat.Jpeg)
                {
                    drawing.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, targetWidth, targetHeight));
                }

                drawing.DrawImage(image, new System.Windows.Rect(0, 0, targetWidth, targetHeight));
            }

            var rendered = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            rendered.Freeze();
            if (outputFormat != ImageOutputFormat.Jpeg)
            {
                return rendered;
            }

            var stream = new MemoryStream();
            var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 20, 100) };
            encoder.Frames.Add(BitmapFrame.Create(rendered));
            encoder.Save(stream);
            stream.Position = 0;
            var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            decoded.Freeze();
            return decoded;
        }
        catch
        {
            return null;
        }
    }

    internal static Task<ImageConversionResult> ConvertAsync(
        IReadOnlyCollection<string> inputPaths,
        ImageOutputFormat outputFormat,
        int scalePercent,
        int jpegQuality,
        string destinationDirectory,
        IProgress<string>? progress = null)
    {
        var completion = new TaskCompletionSource<ImageConversionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                completion.SetResult(ConvertCore(
                    inputPaths,
                    outputFormat,
                    Math.Clamp(scalePercent, 10, 200),
                    Math.Clamp(jpegQuality, 20, 100),
                    destinationDirectory,
                    progress));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "X-Tool 图片处理"
        };
        // WPF 的位图渲染和系统图片解码器需要 STA，不能直接在线程池的 MTA 线程运行。
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private static ImageConversionResult ConvertCore(
        IReadOnlyCollection<string> inputPaths,
        ImageOutputFormat outputFormat,
        int scalePercent,
        int jpegQuality,
        string destinationDirectory,
        IProgress<string>? progress)
    {
        Directory.CreateDirectory(destinationDirectory);
        var succeeded = 0;
        var failed = 0;
        var messages = new List<string>();

        foreach (var inputPath in inputPaths)
        {
            try
            {
                var outputPath = ConvertFile(inputPath, outputFormat, scalePercent, jpegQuality, destinationDirectory);
                succeeded++;
                progress?.Report($"已处理 {Path.GetFileName(outputPath)}");
            }
            catch (Exception exception)
            {
                failed++;
                messages.Add($"{Path.GetFileName(inputPath)}：{exception.Message}");
            }
        }

        return new ImageConversionResult(succeeded, failed, messages);
    }

    private static string ConvertFile(
        string inputPath,
        ImageOutputFormat outputFormat,
        int scalePercent,
        int jpegQuality,
        string destinationDirectory)
    {
        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(
            input,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];
        source.Freeze();

        var targetWidth = Math.Max(1, (int)Math.Round(source.PixelWidth * scalePercent / 100d));
        var targetHeight = Math.Max(1, (int)Math.Round(source.PixelHeight * scalePercent / 100d));
        var needsRender = targetWidth != source.PixelWidth || targetHeight != source.PixelHeight || outputFormat == ImageOutputFormat.Jpeg;
        BitmapSource output = source;

        if (needsRender)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                // JPEG 不支持透明通道，统一以白色铺底，避免透明区域被编码为不可预期的黑色。
                if (outputFormat == ImageOutputFormat.Jpeg)
                {
                    drawing.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, targetWidth, targetHeight));
                }

                drawing.DrawImage(source, new System.Windows.Rect(0, 0, targetWidth, targetHeight));
            }

            var rendered = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            rendered.Freeze();
            output = rendered;
        }

        var extension = outputFormat switch
        {
            ImageOutputFormat.Png => ".png",
            ImageOutputFormat.Jpeg => ".jpg",
            ImageOutputFormat.Bmp => ".bmp",
            ImageOutputFormat.Tiff => ".tiff",
            _ => throw new ArgumentOutOfRangeException(nameof(outputFormat))
        };
        var outputPath = CreateOutputPath(destinationDirectory, Path.GetFileNameWithoutExtension(inputPath), extension);
        var encoder = CreateEncoder(outputFormat, jpegQuality);
        encoder.Frames.Add(BitmapFrame.Create(output));

        using var outputStream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(outputStream);
        return outputPath;
    }

    private static BitmapEncoder CreateEncoder(ImageOutputFormat outputFormat, int jpegQuality) => outputFormat switch
    {
        ImageOutputFormat.Png => new PngBitmapEncoder(),
        ImageOutputFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = jpegQuality },
        ImageOutputFormat.Bmp => new BmpBitmapEncoder(),
        ImageOutputFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
        _ => throw new ArgumentOutOfRangeException(nameof(outputFormat))
    };

    private static string CreateOutputPath(string directory, string sourceName, string extension)
    {
        var initial = Path.Combine(directory, $"{sourceName}_converted{extension}");
        if (!File.Exists(initial))
        {
            return initial;
        }

        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{sourceName}_converted_{index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}

internal enum ImageOutputFormat
{
    Png,
    Jpeg,
    Bmp,
    Tiff
}

internal sealed record ImageConversionResult(int Succeeded, int Failed, IReadOnlyList<string> Errors);
