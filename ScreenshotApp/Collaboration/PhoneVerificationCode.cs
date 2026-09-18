using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotApp.Collaboration;

/// <summary>只识别验证码语境附近唯一的数字串，不把手机号或有效期当成验证码。</summary>
internal sealed record PhoneVerificationCode(string Sender, string Code)
{
    private const string Keyword = @"(?:验证码|校验码|动态码|登录码|认证码|安全码|verification\s+code|security\s+code|one[- ]time\s+(?:code|password)|\bOTP\b)";
    private const string Digits = @"(?<![0-9])(?<code>[0-9]{4,8})(?![0-9]|\s*(?:年|月|日|时|分|秒|元|%))";
    private static readonly Regex AfterKeyword = new(Keyword + @"[^0-9\r\n]{0,20}" + Digits,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex BeforeKeyword = new(Digits + @"[^0-9\r\n]{0,16}" + Keyword,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex SenderPrefix = new(@"^\s*(?:【(?<sender>[^】\r\n]{1,40})】|\[(?<sender>[^\]\r\n]{1,40})\])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex CodeKeyword = new(Keyword, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    private static readonly Regex NumericCandidate = new(Digits, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    internal static bool LooksLikeVerification(PhoneNotificationItem item)
    {
        try { return CodeKeyword.IsMatch(item.Title + "\n" + item.Text); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    internal static PhoneVerificationCode? TryParse(PhoneNotificationItem item)
    {
        try
        {
            var title = item.Title.Normalize(NormalizationForm.FormKC);
            var body = item.Text.Normalize(NormalizationForm.FormKC);
            var text = title + "\n" + body;
            var candidates = AfterKeyword.Matches(text).Cast<Match>()
                .Concat(BeforeKeyword.Matches(text).Cast<Match>())
                .Select(match => match.Groups["code"].Value).Distinct().ToArray();
            if (candidates.Length != 1) return null;
            // 同一正文出现多个不同的短数字串时不猜测，避免误复制备用码、订单号或其他验证码。
            var numberText = body + (CodeKeyword.IsMatch(title) ? "\n" + title : "");
            if (NumericCandidate.Matches(numberText).Cast<Match>()
                .Select(match => match.Groups["code"].Value).Any(number => number != candidates[0])) return null;

            var prefix = SenderPrefix.Match(body);
            var sender = prefix.Success ? prefix.Groups["sender"].Value.Trim() : title.Trim();
            // 标题本身也是短信正文时，回退到应用名，避免在发送方区域泄露整段原文。
            if (string.IsNullOrWhiteSpace(sender) || sender.Contains('\n') || sender.Length > 50 ||
                sender.Contains(candidates[0], StringComparison.Ordinal) || CodeKeyword.IsMatch(sender))
                sender = item.App;
            if (string.IsNullOrWhiteSpace(sender)) sender = "手机验证码";
            return new PhoneVerificationCode(sender.Length > 50 ? sender[..49] + "…" : sender, candidates[0]);
        }
        catch (Exception exception) when (exception is RegexMatchTimeoutException or ArgumentException)
        {
            // 畸形通知或匹配超时只回退到普通通知，不影响其他消息。
            return null;
        }
    }
}
