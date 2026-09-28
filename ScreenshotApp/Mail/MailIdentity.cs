using MimeKit;
namespace ScreenshotApp.Mail;
public static class MailIdentity
{
    public static string? ProviderLogo(string provider) => provider == "TencentExmail"
        ? "pack://application:,,,/XTool;component/Assets/Mail/tencent-exmail.ico"
        : Logo(provider == "QQ" ? "x@qq.com" : "x@google.com");
    public static (string Name, string Address) Parse(string sender)
    {
        if (InternetAddressList.TryParse(sender, out var list) && list.Mailboxes.FirstOrDefault() is { } mailbox)
            return (string.IsNullOrWhiteSpace(mailbox.Name) ? mailbox.Address.Split('@')[0] : mailbox.Name, mailbox.Address);
        return (string.IsNullOrWhiteSpace(sender) ? "未知发件人" : sender.Trim('"'), "");
    }
    // 只匹配实际地址的域名边界，不依据容易伪造的显示名称；图标不代表身份认证。
    public static string? Logo(string address)
    {
        var at = address.LastIndexOf('@');
        if (at < 0) return null;
        var domain = address[(at + 1)..].ToLowerInvariant();
        foreach (var (host, file) in new[] { ("google.com","google.ico"), ("gmail.com","google.ico"), ("googlemail.com","google.ico"), ("github.com","github.png"), ("docker.com","docker.ico"),
            ("jetbrains.com","jetbrains.ico"), ("qq.com","qq.ico"), ("deepseek.com","deepseek.ico") })
            if (domain == host || domain.EndsWith("." + host, StringComparison.Ordinal))
                return "pack://application:,,,/XTool;component/Assets/Mail/" + file;
        return null;
    }
}
