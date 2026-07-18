using System.IO;

namespace ScreenshotApp.Translation;

internal static class TranslationEngineProvider
{
    private static readonly Lazy<ITranslationEngine> DefaultEngine = new(
        () => new OnnxTranslationEngine(TranslationModelPaths.CreateDefault()),
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static ITranslationEngine Default => DefaultEngine.Value;
}

/// <summary>
/// 离线英译中模型路径。模型在开发机完成转换和验证后才会加入发布包，
/// 用户电脑不会进行现场转换，也不会访问任何翻译服务。
/// </summary>
internal sealed record TranslationModelPaths(
    string EncoderPath,
    string DecoderPath,
    string SourceTokenizerPath,
    string TargetTokenizerPath,
    string ManifestPath)
{
    internal static TranslationModelPaths CreateDefault()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "models", "translation", "default");
        return new TranslationModelPaths(
            Path.Combine(root, "encoder_model.onnx"),
            Path.Combine(root, "decoder_model_merged.onnx"),
            Path.Combine(root, "source.spm"),
            Path.Combine(root, "target.spm"),
            Path.Combine(root, "manifest.json"));
    }
}

internal sealed class OnnxTranslationEngine : ITranslationEngine
{
    private readonly TranslationModelPaths _paths;

    internal OnnxTranslationEngine(TranslationModelPaths paths)
    {
        _paths = paths;
    }

    public bool IsReady => RequiredFiles.All(File.Exists);

    public string UnavailableReason => IsReady
        ? "离线翻译模型已找到，正在等待推理适配器完成。"
        : "离线英译中模型尚未安装。该功能不会上传截图或文字；模型安装完成后即可断网使用。";

    public Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReady)
        {
            throw new InvalidOperationException(UnavailableReason);
        }

        // 下一阶段接入 SentencePiece 分词、ONNX 编码器/解码器循环和束搜索。
        // 此处不使用词典或在线服务伪造翻译结果。
        throw new NotSupportedException("离线翻译模型推理适配器尚未完成。");
    }

    private IEnumerable<string> RequiredFiles => new[]
    {
        _paths.EncoderPath,
        _paths.DecoderPath,
        _paths.SourceTokenizerPath,
        _paths.TargetTokenizerPath,
        _paths.ManifestPath
    };
}
