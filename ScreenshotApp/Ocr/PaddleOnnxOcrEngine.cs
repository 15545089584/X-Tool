using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace ScreenshotApp.Ocr;

/// <summary>
/// PP-OCRv5 检测与识别流水线。第一版固定使用 CPU，方向分类接口保留但不加载模型。
/// </summary>
internal sealed class PaddleOnnxOcrEngine : IOcrEngine, IDisposable
{
    private const int DetectorMaxSide = 1280;
    private const float DetectorPixelThreshold = 0.3f;
    private const float DetectorBoxThreshold = 0.55f;
    private const double DetectorUnclipRatio = 1.5;

    private readonly OcrModelPaths _paths;
    private readonly SemaphoreSlim _recognitionLock = new(1, 1);
    private InferenceSession? _detector;
    private InferenceSession? _recognizer;
    private string[]? _characters;
    private bool _disposed;

    internal PaddleOnnxOcrEngine(OcrModelPaths paths)
    {
        _paths = paths;
    }

    public async Task<OcrResult> RecognizeAsync(
        OcrImage image,
        OcrOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ExecutionProvider != OcrExecutionProvider.Cpu)
        {
            throw new NotSupportedException("当前版本仅启用 CPU OCR，硬件加速后端将在基准测试后接入。");
        }

        if (options.UseOrientationClassification)
        {
            throw new NotSupportedException("当前模型包未包含方向分类模型，请关闭方向分类后重试。");
        }

        await _recognitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(
                () =>
                {
                    EnsureInitialized();
                    return RecognizeCore(image, options, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _recognitionLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _detector?.Dispose();
        _recognizer?.Dispose();
        _recognitionLock.Dispose();
    }

    private OcrResult RecognizeCore(OcrImage image, OcrOptions options, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var bgra = Mat.FromPixelData(
            image.PixelHeight,
            image.PixelWidth,
            MatType.CV_8UC4,
            image.BgraPixels);
        using var rgb = new Mat();
        Cv2.CvtColor(bgra, rgb, ColorConversionCodes.BGRA2RGB);

        var boxes = DetectTextBoxes(rgb, cancellationToken);
        var blocks = new List<OcrTextBlock>(boxes.Count);
        foreach (var box in boxes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var crop = CropTextLine(rgb, box.Points);
            if (crop.Empty())
            {
                continue;
            }

            var recognized = RecognizeTextLine(crop);
            if (recognized.Text.Length == 0 || recognized.Confidence < options.ConfidenceThreshold)
            {
                continue;
            }

            blocks.Add(new OcrTextBlock(
                recognized.Text,
                recognized.Confidence,
                box.Bounds.X,
                box.Bounds.Y,
                box.Bounds.Width,
                box.Bounds.Height));
        }

        blocks = blocks
            .OrderBy(block => block.Y + block.Height / 2d)
            .ThenBy(block => block.X)
            .ToList();
        stopwatch.Stop();
        return new OcrResult(OcrTextLayout.Compose(blocks), blocks, stopwatch.Elapsed);
    }

    private List<DetectedTextBox> DetectTextBoxes(Mat rgb, CancellationToken cancellationToken)
    {
        var scale = Math.Min(1d, DetectorMaxSide / (double)Math.Max(rgb.Width, rgb.Height));
        var width = Math.Max(32, RoundTo32((int)Math.Round(rgb.Width * scale)));
        var height = Math.Max(32, RoundTo32((int)Math.Round(rgb.Height * scale)));
        using var resized = new Mat();
        Cv2.Resize(rgb, resized, new OpenCvSharp.Size(width, height), 0, 0, InterpolationFlags.Linear);

        var input = CreateDetectorTensor(resized);
        var inputName = _detector!.InputMetadata.Keys.First();
        using var results = _detector.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
        var output = results.First().AsTensor<float>();
        var dimensions = output.Dimensions.ToArray();
        if (dimensions.Length < 2)
        {
            throw new InvalidDataException("OCR 检测模型输出维度无效。");
        }

        var mapHeight = dimensions[^2];
        var mapWidth = dimensions[^1];
        var probabilities = output.ToArray();
        if (probabilities.Length < mapWidth * mapHeight)
        {
            throw new InvalidDataException("OCR 检测模型输出像素数量不足。");
        }

        using var probabilityMap = Mat.FromPixelData(mapHeight, mapWidth, MatType.CV_32FC1, probabilities);
        using var thresholdFloat = new Mat();
        using var thresholdMap = new Mat();
        Cv2.Threshold(probabilityMap, thresholdFloat, DetectorPixelThreshold, 255, ThresholdTypes.Binary);
        thresholdFloat.ConvertTo(thresholdMap, MatType.CV_8UC1);
        Cv2.FindContours(
            thresholdMap,
            out OpenCvSharp.Point[][] contours,
            out _,
            RetrievalModes.List,
            ContourApproximationModes.ApproxSimple);

        var scaleX = rgb.Width / (double)mapWidth;
        var scaleY = rgb.Height / (double)mapHeight;
        var boxes = new List<DetectedTextBox>();
        foreach (var contour in contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (contour.Length < 4)
            {
                continue;
            }

            var rotated = Cv2.MinAreaRect(contour);
            if (Math.Min(rotated.Size.Width, rotated.Size.Height) < 3)
            {
                continue;
            }

            var score = CalculateBoxScore(probabilityMap, rotated.Points());
            if (score < DetectorBoxThreshold)
            {
                continue;
            }

            var expanded = ExpandBox(rotated, DetectorUnclipRatio);
            var scaled = expanded.Points()
                .Select(point => new Point2f(
                    (float)Math.Clamp(point.X * scaleX, 0, rgb.Width - 1),
                    (float)Math.Clamp(point.Y * scaleY, 0, rgb.Height - 1)))
                .ToArray();
            var ordered = OrderPoints(scaled);
            var bounds = ToBounds(ordered, rgb.Width, rgb.Height);
            if (bounds.Width < 4 || bounds.Height < 4)
            {
                continue;
            }

            boxes.Add(new DetectedTextBox(ordered, bounds));
        }

        return boxes
            .OrderBy(box => box.Bounds.Y + box.Bounds.Height / 2d)
            .ThenBy(box => box.Bounds.X)
            .ToList();
    }

    private RecognizedLine RecognizeTextLine(Mat crop)
    {
        const int targetHeight = 48;
        var metadata = _recognizer!.InputMetadata.Values.First();
        var fixedWidth = metadata.Dimensions.Length >= 4 && metadata.Dimensions[3] > 0
            ? metadata.Dimensions[3]
            : 0;
        var contentWidth = Math.Max(8, (int)Math.Ceiling(targetHeight * crop.Width / (double)crop.Height));
        var targetWidth = fixedWidth > 0
            ? fixedWidth
            : Math.Clamp(contentWidth, 32, 1600);
        var resizedWidth = Math.Min(targetWidth, contentWidth);

        using var resized = new Mat();
        Cv2.Resize(crop, resized, new OpenCvSharp.Size(resizedWidth, targetHeight), 0, 0, InterpolationFlags.Linear);
        using var padded = new Mat(targetHeight, targetWidth, MatType.CV_8UC3, new Scalar(127, 127, 127));
        resized.CopyTo(new Mat(padded, new OpenCvSharp.Rect(0, 0, resizedWidth, targetHeight)));

        var tensor = new DenseTensor<float>(new[] { 1, 3, targetHeight, targetWidth });
        for (var y = 0; y < targetHeight; y++)
        {
            for (var x = 0; x < targetWidth; x++)
            {
                var pixel = padded.At<Vec3b>(y, x);
                tensor[0, 0, y, x] = pixel.Item0 / 127.5f - 1f;
                tensor[0, 1, y, x] = pixel.Item1 / 127.5f - 1f;
                tensor[0, 2, y, x] = pixel.Item2 / 127.5f - 1f;
            }
        }

        var inputName = _recognizer.InputMetadata.Keys.First();
        using var results = _recognizer.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var output = results.First().AsTensor<float>();
        var dimensions = output.Dimensions.ToArray();
        if (dimensions.Length < 2)
        {
            return new RecognizedLine(string.Empty, 0);
        }

        var timeSteps = dimensions[^2];
        var classCount = dimensions[^1];
        var values = output.ToArray();
        var text = new System.Text.StringBuilder();
        var confidenceSum = 0f;
        var confidenceCount = 0;
        var previousIndex = -1;
        for (var time = 0; time < timeSteps; time++)
        {
            var offset = time * classCount;
            var bestIndex = 0;
            var bestScore = values[offset];
            for (var index = 1; index < classCount; index++)
            {
                if (values[offset + index] > bestScore)
                {
                    bestScore = values[offset + index];
                    bestIndex = index;
                }
            }

            if (bestIndex != 0 && bestIndex != previousIndex)
            {
                var character = DecodeCharacter(bestIndex);
                if (character is not null)
                {
                    text.Append(character);
                    confidenceSum += bestScore;
                    confidenceCount++;
                }
            }

            previousIndex = bestIndex;
        }

        return new RecognizedLine(
            text.ToString(),
            confidenceCount == 0 ? 0 : confidenceSum / confidenceCount);
    }

    private string? DecodeCharacter(int modelIndex)
    {
        var dictionaryIndex = modelIndex - 1;
        if (dictionaryIndex >= 0 && dictionaryIndex < _characters!.Length)
        {
            return _characters[dictionaryIndex];
        }

        return dictionaryIndex == _characters!.Length ? " " : null;
    }

    private static DenseTensor<float> CreateDetectorTensor(Mat rgb)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, rgb.Height, rgb.Width });
        var mean = new[] { 0.485f, 0.456f, 0.406f };
        var std = new[] { 0.229f, 0.224f, 0.225f };
        for (var y = 0; y < rgb.Height; y++)
        {
            for (var x = 0; x < rgb.Width; x++)
            {
                var pixel = rgb.At<Vec3b>(y, x);
                tensor[0, 0, y, x] = (pixel.Item0 / 255f - mean[0]) / std[0];
                tensor[0, 1, y, x] = (pixel.Item1 / 255f - mean[1]) / std[1];
                tensor[0, 2, y, x] = (pixel.Item2 / 255f - mean[2]) / std[2];
            }
        }

        return tensor;
    }

    private static float CalculateBoxScore(Mat probabilityMap, IReadOnlyList<Point2f> points)
    {
        var minX = Math.Clamp((int)Math.Floor(points.Min(point => point.X)), 0, probabilityMap.Width - 1);
        var minY = Math.Clamp((int)Math.Floor(points.Min(point => point.Y)), 0, probabilityMap.Height - 1);
        var maxX = Math.Clamp((int)Math.Ceiling(points.Max(point => point.X)), minX + 1, probabilityMap.Width);
        var maxY = Math.Clamp((int)Math.Ceiling(points.Max(point => point.Y)), minY + 1, probabilityMap.Height);
        var rect = new OpenCvSharp.Rect(minX, minY, maxX - minX, maxY - minY);
        using var roi = new Mat(probabilityMap, rect);
        using var mask = Mat.Zeros(rect.Height, rect.Width, MatType.CV_8UC1).ToMat();
        var local = points.Select(point => new OpenCvSharp.Point(
            (int)Math.Round(point.X - rect.X),
            (int)Math.Round(point.Y - rect.Y))).ToArray();
        Cv2.FillPoly(mask, new[] { local }, Scalar.White);
        return (float)Cv2.Mean(roi, mask).Val0;
    }

    private static RotatedRect ExpandBox(RotatedRect box, double unclipRatio)
    {
        var width = Math.Max(1, box.Size.Width);
        var height = Math.Max(1, box.Size.Height);
        var area = width * height;
        var perimeter = 2 * (width + height);
        var distance = area * unclipRatio / perimeter;
        return new RotatedRect(
            box.Center,
            new Size2f((float)(width + distance * 2), (float)(height + distance * 2)),
            box.Angle);
    }

    private static Mat CropTextLine(Mat source, IReadOnlyList<Point2f> points)
    {
        var width = Math.Max(
            Distance(points[0], points[1]),
            Distance(points[2], points[3]));
        var height = Math.Max(
            Distance(points[0], points[3]),
            Distance(points[1], points[2]));
        var targetWidth = Math.Max(1, (int)Math.Round(width));
        var targetHeight = Math.Max(1, (int)Math.Round(height));
        var destination = new[]
        {
            new Point2f(0, 0),
            new Point2f(targetWidth - 1, 0),
            new Point2f(targetWidth - 1, targetHeight - 1),
            new Point2f(0, targetHeight - 1)
        };
        using var transform = Cv2.GetPerspectiveTransform(points.ToArray(), destination);
        var crop = new Mat();
        Cv2.WarpPerspective(
            source,
            crop,
            transform,
            new OpenCvSharp.Size(targetWidth, targetHeight),
            InterpolationFlags.Cubic,
            BorderTypes.Replicate);
        if (crop.Height > crop.Width * 1.5)
        {
            var rotated = new Mat();
            Cv2.Rotate(crop, rotated, RotateFlags.Rotate90Clockwise);
            crop.Dispose();
            return rotated;
        }

        return crop;
    }

    private void EnsureInitialized()
    {
        if (_detector is not null && _recognizer is not null && _characters is not null)
        {
            return;
        }

        ValidateModelPackage();
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1)
        };
        _detector = new InferenceSession(_paths.DetectorPath, sessionOptions);
        _recognizer = new InferenceSession(_paths.RecognizerPath, sessionOptions);
        _characters = File.ReadAllLines(_paths.DictionaryPath)
            .Select(line => line.TrimEnd('\r', '\n'))
            .ToArray();
    }

    private void ValidateModelPackage()
    {
        foreach (var path in new[]
                 {
                     _paths.DetectorPath,
                     _paths.RecognizerPath,
                     _paths.DictionaryPath,
                     _paths.ManifestPath
                 })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("OCR 模型包不完整，请重新安装截影。", path);
            }
        }

        using var document = JsonDocument.Parse(File.ReadAllText(_paths.ManifestPath));
        var hashes = document.RootElement.GetProperty("sha256");
        VerifyHash(_paths.DetectorPath, hashes.GetProperty("detector").GetString());
        VerifyHash(_paths.RecognizerPath, hashes.GetProperty("recognizer").GetString());
        VerifyHash(_paths.DictionaryPath, hashes.GetProperty("dictionary").GetString());
    }

    private static void VerifyHash(string path, string? expected)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var actual = Convert.ToHexString(sha256.ComputeHash(stream));
        if (expected is null || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"OCR 模型校验失败：{Path.GetFileName(path)}");
        }
    }

    private static Point2f[] OrderPoints(IReadOnlyList<Point2f> points)
    {
        var topLeft = points.MinBy(point => point.X + point.Y);
        var bottomRight = points.MaxBy(point => point.X + point.Y);
        var topRight = points.MaxBy(point => point.X - point.Y);
        var bottomLeft = points.MinBy(point => point.X - point.Y);
        return new[] { topLeft, topRight, bottomRight, bottomLeft };
    }

    private static OpenCvSharp.Rect ToBounds(IReadOnlyList<Point2f> points, int width, int height)
    {
        var left = Math.Clamp((int)Math.Floor(points.Min(point => point.X)), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(points.Min(point => point.Y)), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling(points.Max(point => point.X)), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling(points.Max(point => point.Y)), top + 1, height);
        return new OpenCvSharp.Rect(left, top, right - left, bottom - top);
    }

    private static double Distance(Point2f first, Point2f second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static int RoundTo32(int value)
    {
        return Math.Max(32, (int)Math.Round(value / 32d) * 32);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PaddleOnnxOcrEngine));
        }
    }

    private sealed record DetectedTextBox(Point2f[] Points, OpenCvSharp.Rect Bounds);

    private sealed record RecognizedLine(string Text, float Confidence);
}
