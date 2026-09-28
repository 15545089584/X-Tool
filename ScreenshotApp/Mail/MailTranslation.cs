using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScreenshotApp.Translation;

namespace ScreenshotApp.Mail;

public sealed record MailTranslationResult(string Subject, string Body, int RetainedSegments);

/// <summary>按邮件段落翻译，过滤排版填充字符；异常片段保留原文，不展示模型控制词。</summary>
public static class MailTranslation
{
    // 保留常用产品名及账号标识，防止小模型音译品牌或改写用户名。
    private static readonly Regex ProtectedText = new(@"https?://[^\s<>]+|[\w.+-]+@[\w.-]+\.[a-zA-Z]{2,}|\b(?:Docker(?: Desktop| Hub|file)?|GitHub|GitLab|DeepSeek|OpenAI|ChatGPT|LangChain|Gmail|Google|Microsoft|Windows|Linux)\b|\b(?=[A-Za-z0-9_]*[A-Za-z])(?=[A-Za-z0-9_]*\d)[A-Za-z0-9_]{4,}\b|\[|\]|\+|=|[\uD800-\uDBFF][\uDC00-\uDFFF]|\p{So}", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex EnglishWord = new(@"[A-Za-z]{2,}", RegexOptions.None, TimeSpan.FromSeconds(1));
    // 品牌两侧的短操作文案上下文不足，使用明确术语，避免将登录误译为签名。
    private static readonly IReadOnlyDictionary<string, string> ShortPhrases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Download"] = "下载", ["Contact"] = "联系", ["You"] = "你",
        ["Sign in"] = "登录", ["Sign in to"] = "登录", ["and sign in to"] = "并登录"
    };

    public static string Normalize(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.Replace("\r\n", "\n").Replace('\r', '\n').EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            // 邮件预览填充常包含零宽字符、软连字符和组合字素连接符，不应进入模型。
            if (category == UnicodeCategory.Format || rune.Value is 0x034F or 0xFE0E or 0xFE0F) continue;
            if (rune.Value == '\n') result.Append('\n');
            else if (Rune.IsWhiteSpace(rune)) result.Append(' ');
            else if (category != UnicodeCategory.Control) result.Append(rune.ToString());
        }
        return Regex.Replace(result.ToString(), @" +", " ").Trim();
    }

    public static async Task<MailTranslationResult> TranslateAsync(string subject, string body, ITranslationEngine engine,
        CancellationToken token, IProgress<int>? progress = null)
    {
        var retained = 0;
        var completed = 0;
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        async Task<string> TranslatePart(string part)
        {
            var output = new List<string>();
            foreach (var line in Normalize(part).Split('\n'))
            {
                token.ThrowIfCancellationRequested();
                var translatedLine = new StringBuilder();
                var offset = 0;
                foreach (Match match in ProtectedText.Matches(line))
                {
                    translatedLine.Append(await TranslateText(line[offset..match.Index]));
                    translatedLine.Append(match.Value);
                    offset = match.Index + match.Length;
                }
                translatedLine.Append(await TranslateText(line[offset..]));
                output.Add(translatedLine.ToString());
            }
            return string.Join(Environment.NewLine, output);
        }
        async Task<string> TranslateText(string text)
        {
            var output = new StringBuilder();
            foreach (var segment in Segments(text))
            {
                token.ThrowIfCancellationRequested();
                var source = segment.Trim();
                if (ShortPhrases.TryGetValue(source, out var phrase))
                {
                    if (char.IsWhiteSpace(segment[0])) output.Append(' ');
                    output.Append(phrase);
                    if (char.IsWhiteSpace(segment[^1])) output.Append(' ');
                    continue;
                }
                // 中文、数字和孤立的未知单词保留原文，不让模型凭空补全上下文。
                if (EnglishWord.Matches(source).Count < 2 || source.Any(c => c is >= '\u4e00' and <= '\u9fff'))
                { output.Append(segment); continue; }
                if (!cache.TryGetValue(source, out var translated))
                {
                    try
                    {
                        var result = await engine.TranslateAsync(new TranslationRequest(source), token);
                        translated = result.TranslatedText.Trim();
                        if (!IsUsable(translated)) { translated = source; retained++; }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { translated = source; retained++; }
                    token.ThrowIfCancellationRequested();
                    cache[source] = translated;
                    progress?.Report(++completed);
                }
                if (segment.Length > 0 && char.IsWhiteSpace(segment[0])) output.Append(' ');
                output.Append(translated);
                if (segment.Length > 0 && char.IsWhiteSpace(segment[^1])) output.Append(' ');
            }
            return output.ToString();
        }
        var translatedSubject = await TranslatePart(subject);
        var translatedBody = await TranslatePart(body);
        return new(translatedSubject, translatedBody, retained);
    }

    public static bool IsUsable(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('<') && Regex.IsMatch(text, @"<(unk|pad|/?s)>", RegexOptions.IgnoreCase)
            || text.Contains('\uFFFD')) return false;
        // 连续重复短语属于解码退化，不应替换用户原文。
        return !Regex.IsMatch(text, @"(.{1,32}?)\1{5,}", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    }

    public static IEnumerable<string> Segments(string text)
    {
        const int limit = 280;
        var offset = 0;
        while (offset < text.Length)
        {
            var end = Math.Min(offset + limit, text.Length);
            var boundary = -1;
            for (var i = offset; i < end; i++)
            {
                if (text[i] is '.' or '!' or '?' or ';' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
                { boundary = i + 1; break; }
            }
            if (boundary > offset) end = boundary;
            else if (end < text.Length)
            {
                var space = text.LastIndexOf(' ', end - 1, end - offset);
                if (space > offset) end = space + 1;
                else if (char.IsHighSurrogate(text[end - 1])) end--;
            }
            yield return text[offset..end];
            offset = end;
        }
    }
}
