using System.IO;
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ScreenshotApp.Translation;

internal static class TranslationEngineProvider
{
    private static readonly Lazy<OnnxTranslationEngine> DefaultEngine = new(
        () => new OnnxTranslationEngine(TranslationModelPaths.CreateDefault(), "英译中", "opus-mt-en-zh-onnx-int8"),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<OnnxTranslationEngine> ChineseToEnglishEngine = new(
        () => new OnnxTranslationEngine(TranslationModelPaths.CreateChineseToEnglish(), "中译英", "opus-mt-zh-en-onnx-int8"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static ITranslationEngine Default => DefaultEngine.Value;
    internal static ITranslationEngine ChineseToEnglish => ChineseToEnglishEngine.Value;

    /// <summary>应用退出时释放已经创建的离线翻译会话，不触发尚未使用模型的加载。</summary>
    internal static void Dispose()
    {
        if (DefaultEngine.IsValueCreated)
        {
            DefaultEngine.Value.Dispose();
        }

        if (ChineseToEnglishEngine.IsValueCreated)
        {
            ChineseToEnglishEngine.Value.Dispose();
        }
    }
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

    internal static TranslationModelPaths CreateChineseToEnglish()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "models", "translation", "zh-en");
        return new TranslationModelPaths(
            Path.Combine(root, "encoder_model.onnx"),
            Path.Combine(root, "decoder_model.onnx"),
            Path.Combine(root, "source.spm"),
            Path.Combine(root, "target.spm"),
            Path.Combine(root, "vocab.json"),
            Path.Combine(root, "manifest.json"));
    }
}

internal sealed class OnnxTranslationEngine : ITranslationEngine, IDisposable
{
    // 中英两个模型共享限流，避免截图翻译与邮件翻译同时抢占 CPU。
    private static readonly SemaphoreSlim InferenceSlot = new(1, 1);
    // 截图翻译通常间隔较长，空闲后释放模型以降低后台内存占用。
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(5);
    private readonly TranslationModelPaths _paths;
    private readonly string _languageDescription;
    private readonly string _modelVersion;
    private readonly object _sessionSync = new();
    private readonly System.Threading.Timer _sessionIdleTimer;
    private ModelSessions? _sessions;
    private int _activeSessionLeases;
    private long _sessionReleaseDeadlineUtcTicks;
    private bool _disposed;

    internal OnnxTranslationEngine(TranslationModelPaths paths, string languageDescription, string modelVersion)
    {
        _paths = paths;
        _languageDescription = languageDescription;
        _modelVersion = modelVersion;
        _sessionIdleTimer = new System.Threading.Timer(
            ReleaseSessionsWhenIdle,
            null,
            System.Threading.Timeout.InfiniteTimeSpan,
            System.Threading.Timeout.InfiniteTimeSpan);
    }

    public bool IsReady => RequiredFiles.All(File.Exists);

    public string UnavailableReason => IsReady
        ? $"离线{_languageDescription}模型已就绪。"
        : $"离线{_languageDescription}模型尚未安装。该功能不会上传文字；模型安装完成后即可断网使用。";

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (!IsReady)
        {
            throw new InvalidOperationException(UnavailableReason);
        }

        await InferenceSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => TranslateCore(request, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { InferenceSlot.Release(); }
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
        using var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 2),
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED
        };
        // 禁止空转等待，给鼠标、桌宠和 WPF 渲染线程留出调度时间。
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
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
            return new TranslationResult(sourceText, string.Empty, TimeSpan.Zero, _modelVersion);
        }

        var stopwatch = Stopwatch.StartNew();
        using var sessionLease = AcquireSessionLease();
        var sessions = sessionLease.Sessions;
        var units = SplitIntoUnits(sourceText, sessions.Tokenizer).ToArray();
        if (units.Length == 0)
        {
            return new TranslationResult(sourceText, string.Empty, stopwatch.Elapsed, _modelVersion);
        }

        var translatedUnits = new List<string>(units.Length);
        foreach (var unit in units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var translated = TranslateUnit(unit, sessions, cancellationToken);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                translatedUnits.Add(translated);
            }
        }

        return new TranslationResult(
            sourceText,
            string.Join(Environment.NewLine, translatedUnits),
            stopwatch.Elapsed,
            _modelVersion);
    }

    /// <summary>取得翻译会话使用权，并停止已有的空闲释放计时。</summary>
    private ModelSessionLease AcquireSessionLease()
    {
        lock (_sessionSync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(OnnxTranslationEngine));
            }

            _sessionReleaseDeadlineUtcTicks = 0;
            _sessionIdleTimer.Change(
                System.Threading.Timeout.InfiniteTimeSpan,
                System.Threading.Timeout.InfiniteTimeSpan);
            _sessions ??= CreateSessions();
            _activeSessionLeases++;
            return new ModelSessionLease(this, _sessions);
        }
    }

    /// <summary>归还翻译会话；无活跃任务时开始空闲释放倒计时。</summary>
    private void ReleaseSessionLease()
    {
        ModelSessions? sessionsToDispose = null;
        lock (_sessionSync)
        {
            if (_activeSessionLeases == 0)
            {
                return;
            }

            _activeSessionLeases--;
            if (_activeSessionLeases > 0)
            {
                return;
            }

            if (_disposed)
            {
                sessionsToDispose = _sessions;
                _sessions = null;
                _sessionReleaseDeadlineUtcTicks = 0;
            }
            else if (_sessions is not null)
            {
                _sessionReleaseDeadlineUtcTicks = DateTime.UtcNow.Add(SessionIdleTimeout).Ticks;
                _sessionIdleTimer.Change(SessionIdleTimeout, System.Threading.Timeout.InfiniteTimeSpan);
            }
        }

        sessionsToDispose?.Dispose();
    }

    /// <summary>空闲到期后仅在没有翻译任务时释放模型会话。</summary>
    private void ReleaseSessionsWhenIdle(object? state)
    {
        ModelSessions? sessionsToDispose = null;
        TimeSpan? retryDelay = null;
        lock (_sessionSync)
        {
            if (_disposed || _activeSessionLeases > 0 || _sessions is null)
            {
                return;
            }

            var deadline = _sessionReleaseDeadlineUtcTicks;
            if (deadline <= 0)
            {
                return;
            }

            var remainingTicks = deadline - DateTime.UtcNow.Ticks;
            if (remainingTicks > 0)
            {
                retryDelay = TimeSpan.FromTicks(remainingTicks);
            }
            else
            {
                sessionsToDispose = _sessions;
                _sessions = null;
                _sessionReleaseDeadlineUtcTicks = 0;
            }
        }

        if (retryDelay is TimeSpan delay)
        {
            try
            {
                _sessionIdleTimer.Change(delay, System.Threading.Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // 应用退出时计时器可能与释放流程重叠。
            }

            return;
        }

        sessionsToDispose?.Dispose();
    }

    public void Dispose()
    {
        ModelSessions? sessionsToDispose = null;
        lock (_sessionSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sessionReleaseDeadlineUtcTicks = 0;
            if (_activeSessionLeases == 0)
            {
                sessionsToDispose = _sessions;
                _sessions = null;
            }
        }

        _sessionIdleTimer.Dispose();
        sessionsToDispose?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        lock (_sessionSync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(OnnxTranslationEngine));
            }
        }
    }

    /// <summary>
    /// 将较长段落先按句子切开，再按模型输入词元上限切块。
    /// 小型离线模型整段生成时容易在输出长度达到上限后丢失开头或结尾；
    /// 分句能让每一段都拥有完整的编码与解码预算。
    /// </summary>
    private static IEnumerable<string> SplitIntoUnits(string sourceText, SentencePieceMarianTokenizer tokenizer)
    {
        var sentenceStart = 0;
        for (var index = 0; index < sourceText.Length; index++)
        {
            if (sourceText[index] is not ('.' or '!' or '?' or '。' or '！' or '？' or '\n'))
            {
                continue;
            }

            var sentenceEnd = index + 1;
            if (sourceText[index] is not ('\n' or '。' or '！' or '？') &&
                sentenceEnd < sourceText.Length &&
                !char.IsWhiteSpace(sourceText[sentenceEnd]))
            {
                continue;
            }

            foreach (var unit in SplitLongUnit(sourceText[sentenceStart..sentenceEnd], tokenizer))
            {
                yield return unit;
            }

            sentenceStart = sentenceEnd;
        }

        if (sentenceStart < sourceText.Length)
        {
            foreach (var unit in SplitLongUnit(sourceText[sentenceStart..], tokenizer))
            {
                yield return unit;
            }
        }
    }

    private static IEnumerable<string> SplitLongUnit(string unit, SentencePieceMarianTokenizer tokenizer)
    {
        var normalized = unit.Trim();
        if (normalized.Length == 0)
        {
            yield break;
        }

        if (GetSourceTokenCount(normalized, tokenizer) <= MaximumSourceTokensPerUnit)
        {
            yield return normalized;
            yield break;
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new List<string>();
        foreach (var word in words)
        {
            var candidate = string.Join(' ', current.Append(word));
            if (current.Count > 0 && GetSourceTokenCount(candidate, tokenizer) > MaximumSourceTokensPerUnit)
            {
                yield return string.Join(' ', current);
                current.Clear();
            }

            current.Add(word);
        }

        if (current.Count > 0)
        {
            yield return string.Join(' ', current);
        }
    }

    private static int GetSourceTokenCount(string sourceText, SentencePieceMarianTokenizer tokenizer) =>
        tokenizer.EncodeSource(sourceText).Count();

    private static string TranslateUnit(
        string sourceText,
        ModelSessions sessions,
        CancellationToken cancellationToken)
    {
        var sourceIds = sessions.Tokenizer.EncodeSource(sourceText).Select(id => (long)id).ToArray();
        if (sourceIds.Length == 0)
        {
            return string.Empty;
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
        var maximumGeneratedTokens = Math.Clamp(sourceIds.Length * 3, MinimumGeneratedTokens, MaximumGeneratedTokens);
        for (var step = 0; step < maximumGeneratedTokens; step++)
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
        return translated;
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
    private const int MinimumGeneratedTokens = 32;
    private const int MaximumSourceTokensPerUnit = 160;

    private sealed record ModelSessions(
        InferenceSession Encoder,
        InferenceSession Decoder,
        SentencePieceMarianTokenizer Tokenizer) : IDisposable
    {
        public void Dispose()
        {
            Encoder.Dispose();
            Decoder.Dispose();
            Tokenizer.Dispose();
        }
    }

    /// <summary>确保正在进行的翻译完成前不会释放其依赖的 ONNX 会话。</summary>
    private sealed class ModelSessionLease : IDisposable
    {
        private OnnxTranslationEngine? _owner;

        internal ModelSessionLease(OnnxTranslationEngine owner, ModelSessions sessions)
        {
            _owner = owner;
            Sessions = sessions;
        }

        internal ModelSessions Sessions { get; }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseSessionLease();
        }
    }
}
