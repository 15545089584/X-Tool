namespace ScreenshotApp.Translation;

public enum TranslationExecutionProvider
{
    Cpu,
    DirectML,
    WindowsML
}

public sealed class TranslationRequest
{
    public TranslationRequest(string sourceText)
    {
        SourceText = sourceText ?? throw new ArgumentNullException(nameof(sourceText));
    }

    public string SourceText { get; }

    public string SourceLanguage { get; init; } = "en";

    public string TargetLanguage { get; init; } = "zh-Hans";

    public TranslationExecutionProvider ExecutionProvider { get; init; } = TranslationExecutionProvider.Cpu;
}

public sealed record TranslationResult(
    string SourceText,
    string TranslatedText,
    TimeSpan Elapsed,
    string ModelVersion);

public interface ITranslationEngine
{
    bool IsReady { get; }

    string UnavailableReason { get; }

    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken);
}

internal static class TranslationTextPreprocessor
{
    internal static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .ToArray();
        var result = new List<string>();
        var previousWasBlank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (!previousWasBlank && result.Count > 0)
                {
                    result.Add(string.Empty);
                }

                previousWasBlank = true;
                continue;
            }

            result.Add(RepairSplitContractions(line));
            previousWasBlank = false;
        }

        return string.Join(Environment.NewLine, result).Trim();
    }

    /// <summary>
    /// 修正 OCR 常见的英文缩写断开，例如 can' t、don' t、we' d。
    /// 仅处理确定的一字母缩写，避免误改朋友名、普通单词等内容。
    /// </summary>
    private static string RepairSplitContractions(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\b([A-Za-z]+)'\s+(t|s|d|m|r|ve|ll)\b",
            match => $"{match.Groups[1].Value}'{match.Groups[2].Value}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static bool ContainsEnglish(string text) => text.Any(character =>
        (character >= 'A' && character <= 'Z') ||
        (character >= 'a' && character <= 'z'));
}
