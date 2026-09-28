using System.Text.Json.Serialization;

namespace ScreenshotApp.Mail;

public sealed record MailAccount(string Id, string Provider, string Address, string Secret,
    bool Alerts = true, bool Preview = false, int QuietStart = 23, int QuietEnd = 8, bool Quiet = false, bool UseSystemProxy = false, string Remark = "")
{
    [JsonIgnore] public string Host => Provider switch { "QQ" => "imap.qq.com", "Gmail" => "imap.gmail.com", "TencentExmail" => "imap.exmail.qq.com", _ => throw new ArgumentException("不支持的邮箱服务") };
}
public sealed record MailRow(string AccountId, uint Validity, uint Uid, string Sender, string Subject,
    DateTimeOffset Date, bool Seen, uint Size)
{
    [JsonIgnore] public string Key => AccountId + ":" + Validity + ":" + Uid;
    [JsonIgnore] public string AccountLabel { get; init; } = "";
    [JsonIgnore] public string Caption => (Seen ? "" : "● ") + Subject;
    public bool HasAttachments { get; init; }
    public bool Flagged { get; init; }
    [JsonIgnore] public string SenderName => MailIdentity.Parse(Sender).Name;
    [JsonIgnore] public string SenderAddress => MailIdentity.Parse(Sender).Address;
    [JsonIgnore] public string? Logo => MailIdentity.Logo(SenderAddress);
    [JsonIgnore] public string Initial => Logo is not null ? "" : string.IsNullOrWhiteSpace(SenderName) ? "?" : System.Globalization.StringInfo.GetNextTextElement(SenderName).ToUpperInvariant();
    [JsonIgnore] public string StateLabel => Seen ? "已读" : "未读";
    [JsonIgnore] public string AttachmentLabel => HasAttachments ? "含附件" : "";
    [JsonIgnore] public string When => Date.ToLocalTime().ToString("MM-dd HH:mm");
}
public sealed record MailCheckpoint(uint Validity, uint HighUid, bool Initialized);
public sealed record MailAccountView(string Id, string Provider, string Address, string Status,
    bool Alerts, bool Preview, bool Quiet, int QuietStart, int QuietEnd, bool UseSystemProxy = false, string Remark = "")
{
    public int? InboxTotal { get; init; }
    public string ConnectionColor => Status.StartsWith("已连接", StringComparison.Ordinal) ? "#10B981"
        : Status.StartsWith("正在连接", StringComparison.Ordinal) || Status.StartsWith("等待", StringComparison.Ordinal) ? "#F59E0B" : "#EF4444";
    public string DisplayName => string.IsNullOrWhiteSpace(Remark) ? (Provider == "TencentExmail" ? "腾讯企业邮箱" : Provider) : Remark;
    public string? Logo => MailIdentity.ProviderLogo(Provider);
}
public sealed record MailSnapshot(long Revision, MailAccountView[] Accounts, MailRow[] Rows, string Error);
public sealed record MailArrival(string AccountId, MailRow[] Rows);
public sealed record MailDatabase(MailAccount[] Accounts, MailRow[] Rows, Dictionary<string, MailCheckpoint> Checkpoints);
public static class MailRules
{
    /// <summary>只读取 HTML 文本节点；不保留脚本、样式、属性及远程图片地址。</summary>
    public static string HtmlText(string html)
    {
        using var input = new System.IO.StringReader(html);
        var parser = new MimeKit.Text.HtmlTokenizer(input) { DecodeCharacterReferences = true };
        var text = new System.Text.StringBuilder();
        string? hidden = null;
        while (parser.ReadNextToken(out var token) && text.Length < 200000)
        {
            if (token is MimeKit.Text.HtmlTagToken tag)
            {
                var name = tag.Name.ToLowerInvariant();
                if (hidden is not null) { if (tag.IsEndTag && name == hidden) hidden = null; continue; }
                if (!tag.IsEndTag && name is "script" or "style" or "head") { hidden = name; continue; }
                if (name is "br" or "p" or "div" or "tr" or "li" or "h1" or "h2")
                    text.AppendLine();
            }
            else if (hidden is null && token is MimeKit.Text.HtmlDataToken data)
                text.Append(data.Data.AsSpan(0, Math.Min(data.Data.Length, 200000 - text.Length)));
        }
        return text.ToString().Trim();
    }
    public static bool IsQuiet(MailAccount a, int hour) => a.Quiet &&
        (a.QuietStart == a.QuietEnd || (a.QuietStart < a.QuietEnd
        ? hour >= a.QuietStart && hour < a.QuietEnd : hour >= a.QuietStart || hour < a.QuietEnd));
    public static bool IsNew(MailCheckpoint? previous, uint validity, uint uid) =>
        previous is { Initialized: true } && previous.Validity == validity && uid > previous.HighUid;
    public static string SafeFileName(string? name)
    {
        var result = System.IO.Path.GetFileName((name ?? "").Replace('\\', '/'));
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) result = result.Replace(c, '_');
        return string.IsNullOrWhiteSpace(result) ? "附件.bin" : result.Length > 180 ? result[..180] : result;
    }
}
