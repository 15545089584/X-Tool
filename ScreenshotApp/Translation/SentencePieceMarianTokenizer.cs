using System.Text.Json;
using System.IO;
using SIL.Machine.Tokenization.SentencePiece;

namespace ScreenshotApp.Translation;

/// <summary>
/// Marian 模型使用 SentencePiece 切分，但词元编号由模型附带词表定义。
/// 不依赖 Python，也不依赖与此模型不兼容的通用 tokenizer.json 解析器。
/// </summary>
internal sealed class SentencePieceMarianTokenizer : IDisposable
{
    private const int EndOfSentenceTokenId = 0;
    private const int UnknownTokenId = 1;
    private const int PaddingTokenId = 65000;
    private readonly SentencePieceTokenizer _sourceTokenizer;
    private readonly SentencePieceDetokenizer _targetDetokenizer = new();
    private readonly IReadOnlyDictionary<string, int> _tokenToId;
    private readonly IReadOnlyDictionary<int, string> _idToToken;

    internal SentencePieceMarianTokenizer(string sourceModelPath, string targetModelPath, string vocabularyPath)
    {
        _sourceTokenizer = new SentencePieceTokenizer(sourceModelPath);
        if (!File.Exists(targetModelPath))
        {
            throw new FileNotFoundException("未找到目标语言 SentencePiece 模型。", targetModelPath);
        }

        using var stream = File.OpenRead(vocabularyPath);
        var vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(stream)
            ?? throw new InvalidOperationException("离线翻译词表格式无效。");
        _tokenToId = vocabulary;
        _idToToken = vocabulary.ToDictionary(pair => pair.Value, pair => pair.Key);
    }

    internal IEnumerable<int> EncodeSource(string sourceText)
    {
        foreach (var piece in _sourceTokenizer.Tokenize(sourceText))
        {
            yield return _tokenToId.TryGetValue(piece, out var tokenId) ? tokenId : UnknownTokenId;
        }

        yield return EndOfSentenceTokenId;
    }

    internal string DecodeTarget(IEnumerable<uint> tokenIds)
    {
        var pieces = tokenIds
            .Select(tokenId => (int)tokenId)
            .Where(tokenId => tokenId is not EndOfSentenceTokenId and not PaddingTokenId)
            .Select(tokenId => _idToToken.TryGetValue(tokenId, out var piece) ? piece : string.Empty)
            .Where(piece => piece.Length > 0);
        return _targetDetokenizer.Detokenize(pieces);
    }

    public void Dispose() => _sourceTokenizer.Dispose();
}
