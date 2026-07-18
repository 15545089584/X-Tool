using System.IO;
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

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
    string VocabularyPath,
    string ManifestPath)
{
    internal static TranslationModelPaths CreateDefault()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "models", "translation", "default");
        return new TranslationModelPaths(
            Path.Combine(root, "encoder_model.onnx"),
            Path.Combine(root, "decoder_model.onnx"),
            Path.Combine(root, "source.spm"),
            Path.Combine(root, "target.spm"),
            Path.Combine(root, "vocab.json"),
            Path.Combine(root, "manifest.json"));
    }
}

internal sealed class OnnxTranslationEngine : ITranslationEngine
{
    private readonly TranslationModelPaths _paths;
    private readonly Lazy<ModelSessions> _sessions;

    internal OnnxTranslationEngine(TranslationModelPaths paths)
    {
        _paths = paths;
        _sessions = new Lazy<ModelSessions>(CreateSessions, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsReady => RequiredFiles.All(File.Exists);

    public string UnavailableReason => IsReady
        ? "离线英译中模型已就绪。"
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

        return Task.Run(() => TranslateCore(request, cancellationToken), cancellationToken);
    }

    private IEnumerable<string> RequiredFiles => new[]
    {
        _paths.EncoderPath,
        _paths.DecoderPath,
        _paths.SourceTokenizerPath,
        _paths.TargetTokenizerPath,
        _paths.VocabularyPath,
        _paths.ManifestPath
    };

    private ModelSessions CreateSessions()
    {
        var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED
        };
        return new ModelSessions(
            new InferenceSession(_paths.EncoderPath, options),
            new InferenceSession(_paths.DecoderPath, options),
            new SentencePieceMarianTokenizer(_paths.SourceTokenizerPath, _paths.TargetTokenizerPath, _paths.VocabularyPath));
    }

    private TranslationResult TranslateCore(TranslationRequest request, CancellationToken cancellationToken)
    {
        var sourceText = TranslationTextPreprocessor.Normalize(request.SourceText);
        if (sourceText.Length == 0)
        {
            return new TranslationResult(sourceText, string.Empty, TimeSpan.Zero, "opus-mt-en-zh-onnx-int8");
        }

        var stopwatch = Stopwatch.StartNew();
        var sessions = _sessions.Value;
        var sourceIds = sessions.Tokenizer.EncodeSource(sourceText).Select(id => (long)id).ToArray();
        if (sourceIds.Length == 0)
        {
            return new TranslationResult(sourceText, string.Empty, stopwatch.Elapsed, "opus-mt-en-zh-onnx-int8");
        }

        if (sourceIds.Length > 480)
        {
            throw new InvalidOperationException("当前离线模型一次最多翻译约 480 个词元，请缩小框选区域后重试。");
        }

        var attentionMask = Enumerable.Repeat(1L, sourceIds.Length).ToArray();
        var encoderInputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ToLongTensor(sourceIds)),
            NamedOnnxValue.CreateFromTensor("attention_mask", ToLongTensor(attentionMask))
        };
        DenseTensor<float> hiddenStates;
        using (var encoderOutputs = sessions.Encoder.Run(encoderInputs))
        {
            hiddenStates = CopyTensor(encoderOutputs.First(output => output.Name == "last_hidden_state").AsTensor<float>());
        }

        var generatedIds = new List<uint>();
        var decoderInput = new List<long> { DecoderStartTokenId };
        for (var step = 0; step < MaximumGeneratedTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoderInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", ToLongTensor(attentionMask)),
                NamedOnnxValue.CreateFromTensor("input_ids", ToLongTensor(decoderInput.ToArray())),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hiddenStates)
            };

            using var decoderOutputs = sessions.Decoder.Run(decoderInputs);
            var logits = decoderOutputs.First(output => output.Name == "logits").AsTensor<float>();
            var nextToken = GetMostLikelyToken(logits);
            if (nextToken == EndOfSentenceTokenId)
            {
                break;
            }

            generatedIds.Add((uint)nextToken);
            decoderInput.Add(nextToken);
        }

        var translated = sessions.Tokenizer.DecodeTarget(generatedIds).Trim();
        return new TranslationResult(sourceText, translated, stopwatch.Elapsed, "opus-mt-en-zh-onnx-int8");
    }

    private static DenseTensor<long> ToLongTensor(long[] values) =>
        new(values, new[] { 1, values.Length });

    private static DenseTensor<float> CopyTensor(Tensor<float> source) =>
        new(source.ToArray(), source.Dimensions.ToArray());

    private static long GetMostLikelyToken(Tensor<float> logits)
    {
        var maximum = float.NegativeInfinity;
        var token = 0L;
        var lastSequenceIndex = logits.Dimensions[1] - 1;
        for (var index = 0; index < VocabularySize; index++)
        {
            var value = logits[0, lastSequenceIndex, index];
            if (value > maximum)
            {
                maximum = value;
                token = index;
            }
        }

        return token;
    }

    private const long DecoderStartTokenId = 65000;
    private const long EndOfSentenceTokenId = 0;
    private const int VocabularySize = 65001;
    private const int MaximumGeneratedTokens = 192;

    private sealed record ModelSessions(
        InferenceSession Encoder,
        InferenceSession Decoder,
        SentencePieceMarianTokenizer Tokenizer);
}
