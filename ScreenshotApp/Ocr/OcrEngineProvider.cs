using System.IO;

namespace ScreenshotApp.Ocr;

internal static class OcrEngineProvider
{
    private static readonly Lazy<IOcrEngine> DefaultEngine = new(
        () => new PaddleOnnxOcrEngine(OcrModelPaths.CreateDefault()),
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static IOcrEngine Default => DefaultEngine.Value;
}

internal sealed record OcrModelPaths(
    string DetectorPath,
    string RecognizerPath,
    string DictionaryPath,
    string ManifestPath)
{
    internal static OcrModelPaths CreateDefault()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "models", "ocr", "default");
        return new OcrModelPaths(
            Path.Combine(root, "det.onnx"),
            Path.Combine(root, "rec.onnx"),
            Path.Combine(root, "dict.txt"),
            Path.Combine(root, "manifest.json"));
    }
}
