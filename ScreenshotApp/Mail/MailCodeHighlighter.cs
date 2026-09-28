using System.Text.RegularExpressions;
namespace ScreenshotApp.Mail;
/// <summary>仅识别验证码语境中的短码，不上传正文或执行邮件内容。</summary>
public static class MailCodeHighlighter
{
    private static readonly Regex Pattern = new(
        @"(?:验证码|校验码|授权码|动态码|verification\s+code|security\s+code|one[- ]time\s+(?:password|code)|authorization\s+code|your\s+code)(?:\s*(?:is|为|是))?[\s:：=【\[（(]*([A-Za-z0-9]{4,16})(?![A-Za-z0-9@./-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static IReadOnlyList<(int Start, int Length)> Find(string text)
    {
        var found = new List<(int, int)>();
        try
        {
            foreach (Match match in Pattern.Matches(text[..Math.Min(text.Length, 200000)]))
            {
                var code = match.Groups[1];
                // 至少有数字，避免普通单词误标；限制数量以免超长邮件阻塞界面。
                if (code.Value.Any(char.IsDigit)) found.Add((code.Index, code.Length));
                if (found.Count >= 200) break;
            }
        }
        catch (RegexMatchTimeoutException) { }
        return found;
    }
}
