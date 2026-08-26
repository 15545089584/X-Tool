using System.Text.Json.Serialization;

namespace ScreenshotApp.InformationVault;

internal enum InformationVaultEntryType
{
    GitHubCredential,
    RecoveryCodes,
    DeepSeekApiKey,
    Steam,
    Ubisoft,
    Epic,
    MySql,
    Redis,
    VirtualMachine,
    WeChat,
    QQ,
    Custom
}

internal sealed class InformationVaultEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InformationVaultEntryType Type { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Account { get; set; } = string.Empty;

    public string Secret { get; set; } = string.Empty;

    public string GitHubPushKey { get; set; } = string.Empty;

    public bool GitHubTwoFactorEnabled { get; set; }

    public string Host { get; set; } = string.Empty;

    public string Port { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public string OperatingSystem { get; set; } = string.Empty;

    public string OperatingSystemDistribution { get; set; } = string.Empty;

    public string OperatingSystemVersion { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public List<InformationVaultRecoveryCode> RecoveryCodes { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string TypeDisplayName => InformationVaultEntryTypes.GetDisplayName(Type);

    [JsonIgnore]
    public string Summary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Account))
            {
                return Account;
            }

            if (!string.IsNullOrWhiteSpace(Host))
            {
                return string.IsNullOrWhiteSpace(Port) ? Host : $"{Host}:{Port}";
            }

            if (RecoveryCodes.Count > 0)
            {
                var remaining = RecoveryCodes.Count(code => !code.IsUsed);
                return $"剩余 {remaining} / {RecoveryCodes.Count} 个";
            }

            return "未填写摘要";
        }
    }

    [JsonIgnore]
    public bool SupportsAutoFill => InformationVaultEntryTypes.SupportsAutoFill(Type);

    [JsonIgnore]
    public bool HasAccount => !string.IsNullOrWhiteSpace(Account);

    [JsonIgnore]
    public bool HasSecret => !string.IsNullOrWhiteSpace(Secret);

    [JsonIgnore]
    public bool HasGitHubPushKey => !string.IsNullOrWhiteSpace(GitHubPushKey);

    [JsonIgnore]
    public bool HasHost => !string.IsNullOrWhiteSpace(Host);

    [JsonIgnore]
    public bool HasPort => !string.IsNullOrWhiteSpace(Port);

    [JsonIgnore]
    public bool HasDatabase => !string.IsNullOrWhiteSpace(Database);

    [JsonIgnore]
    public bool HasOperatingSystem => !string.IsNullOrWhiteSpace(OperatingSystem) ||
                                      !string.IsNullOrWhiteSpace(OperatingSystemDistribution) ||
                                      !string.IsNullOrWhiteSpace(OperatingSystemVersion);

    [JsonIgnore]
    public string OperatingSystemDisplay => InformationVaultOperatingSystems.GetDisplayName(
        OperatingSystem,
        OperatingSystemDistribution,
        OperatingSystemVersion);

    [JsonIgnore]
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    [JsonIgnore]
    public bool HasRecoveryCodes => RecoveryCodes.Count > 0;

    [JsonIgnore]
    public bool HasBrandIcon => BrandIconSource is not null;

    [JsonIgnore]
    public string? BrandIconSource => Type switch
    {
        InformationVaultEntryType.GitHubCredential => "/Assets/InformationVault/github.png",
        InformationVaultEntryType.DeepSeekApiKey => "/Assets/InformationVault/deepseek.png",
        InformationVaultEntryType.Steam => "/Assets/InformationVault/steam.png",
        InformationVaultEntryType.Ubisoft => "/Assets/InformationVault/ubisoft.png",
        InformationVaultEntryType.Epic => "/Assets/InformationVault/epicgames.png",
        InformationVaultEntryType.MySql => "/Assets/InformationVault/mysql.png",
        InformationVaultEntryType.Redis => "/Assets/InformationVault/redis.png",
        InformationVaultEntryType.VirtualMachine => InformationVaultOperatingSystems.GetIconSource(
            OperatingSystem,
            OperatingSystemDistribution),
        InformationVaultEntryType.WeChat => "/Assets/InformationVault/wechat.png",
        InformationVaultEntryType.QQ => "/Assets/InformationVault/tencentqq.png",
        _ => null
    };

    [JsonIgnore]
    public string IconGlyph => Type switch
    {
        InformationVaultEntryType.GitHubCredential => "\uE8D7",
        InformationVaultEntryType.RecoveryCodes => "\uE72E",
        InformationVaultEntryType.DeepSeekApiKey => "\uE8C8",
        InformationVaultEntryType.Steam => "\uE7FC",
        InformationVaultEntryType.Ubisoft => "\uE7FC",
        InformationVaultEntryType.Epic => "\uE7FC",
        InformationVaultEntryType.MySql => "\uE8A5",
        InformationVaultEntryType.Redis => "\uE71A",
        InformationVaultEntryType.VirtualMachine => "\uE7F4",
        InformationVaultEntryType.WeChat => "\uE8BD",
        InformationVaultEntryType.QQ => "\uE8BD",
        _ => "\uE8D7"
    };

    [JsonIgnore]
    public string AccentBackground => Type switch
    {
        InformationVaultEntryType.GitHubCredential => "#F0F1F3",
        InformationVaultEntryType.RecoveryCodes => "#EEE7FF",
        InformationVaultEntryType.DeepSeekApiKey => "#E9EDFF",
        InformationVaultEntryType.Steam => "#E7F3FB",
        InformationVaultEntryType.Ubisoft => "#E8F2FF",
        InformationVaultEntryType.Epic => "#F1ECFF",
        InformationVaultEntryType.MySql => "#E8F2F7",
        InformationVaultEntryType.Redis => "#FFEAE8",
        InformationVaultEntryType.VirtualMachine => InformationVaultOperatingSystems.GetAccentBackground(
            OperatingSystem,
            OperatingSystemDistribution),
        InformationVaultEntryType.WeChat => "#E7F8EE",
        InformationVaultEntryType.QQ => "#FFE9EC",
        _ => "#EEF2F7"
    };

    [JsonIgnore]
    public string AccentForeground => Type switch
    {
        InformationVaultEntryType.GitHubCredential => "#24292F",
        InformationVaultEntryType.RecoveryCodes => "#7B57C5",
        InformationVaultEntryType.DeepSeekApiKey => "#4D6BFE",
        InformationVaultEntryType.Steam => "#1B75BB",
        InformationVaultEntryType.Ubisoft => "#3976D1",
        InformationVaultEntryType.Epic => "#7257C7",
        InformationVaultEntryType.MySql => "#4479A1",
        InformationVaultEntryType.Redis => "#E43B32",
        InformationVaultEntryType.VirtualMachine => InformationVaultOperatingSystems.GetAccentForeground(
            OperatingSystem,
            OperatingSystemDistribution),
        InformationVaultEntryType.WeChat => "#079B50",
        InformationVaultEntryType.QQ => "#D7192D",
        _ => "#60758A"
    };

    [JsonIgnore]
    public string UsageBadge => SupportsAutoFill
        ? "支持一键填入"
        : Type is InformationVaultEntryType.GitHubCredential or InformationVaultEntryType.RecoveryCodes
            ? string.Empty
            : "安全复制";

    public bool HasUsageBadge => !string.IsNullOrEmpty(UsageBadge);

    [JsonIgnore]
    public string UpdatedAtDisplay => UpdatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");

    internal InformationVaultEntry Clone()
    {
        return new InformationVaultEntry
        {
            Id = Id,
            Type = Type,
            Title = Title,
            Account = Account,
            Secret = Secret,
            GitHubPushKey = GitHubPushKey,
            GitHubTwoFactorEnabled = GitHubTwoFactorEnabled,
            Host = Host,
            Port = Port,
            Database = Database,
            OperatingSystem = OperatingSystem,
            OperatingSystemDistribution = OperatingSystemDistribution,
            OperatingSystemVersion = OperatingSystemVersion,
            Notes = Notes,
            RecoveryCodes = RecoveryCodes.Select(code => code.Clone()).ToList(),
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc
        };
    }
}

internal sealed class InformationVaultRecoveryCode
{
    public string Value { get; set; } = string.Empty;

    public bool IsUsed { get; set; }

    [JsonIgnore]
    public string StatusText => IsUsed ? "已使用" : "可用";

    [JsonIgnore]
    public string DisplayValue => IsUsed ? "••••••••" : Value;

    internal InformationVaultRecoveryCode Clone() => new()
    {
        Value = Value,
        IsUsed = IsUsed
    };
}

internal sealed class InformationVaultData
{
    public int Version { get; set; } = 4;

    public List<InformationVaultEntry> Entries { get; set; } = [];
}

internal static class InformationVaultOperatingSystems
{
    internal static IReadOnlyList<string> Families { get; } = ["Linux", "Windows", "macOS", "BSD", "其他"];

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Distributions { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Linux"] = ["Ubuntu", "CentOS", "Debian", "Fedora", "Rocky Linux", "AlmaLinux", "Red Hat Enterprise Linux", "Arch Linux", "openSUSE", "Kali Linux", "Linux（其他）"],
            ["Windows"] = ["Windows Server", "Windows 11", "Windows 10", "Windows（其他）"],
            ["macOS"] = ["macOS"],
            ["BSD"] = ["FreeBSD", "OpenBSD", "NetBSD", "BSD（其他）"],
            ["其他"] = ["其他系统"]
        };

    internal static IReadOnlyList<string> GetDistributions(string? operatingSystem)
    {
        return !string.IsNullOrWhiteSpace(operatingSystem) &&
               Distributions.TryGetValue(operatingSystem, out var distributions)
            ? distributions
            : Distributions["Linux"];
    }

    internal static string GetDisplayName(string? operatingSystem, string? distribution, string? version)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(operatingSystem))
        {
            parts.Add(operatingSystem.Trim());
        }

        if (!string.IsNullOrWhiteSpace(distribution) &&
            !string.Equals(operatingSystem, distribution, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(distribution.Trim());
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            parts.Add(version.Trim());
        }

        return parts.Count == 0 ? "未填写" : string.Join(" · ", parts);
    }

    internal static (string OperatingSystem, string Distribution, string Version) ParseLegacyValue(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var linuxDistributions = new[]
        {
            (Aliases: new[] { "Red Hat Enterprise Linux", "RHEL" }, Name: "Red Hat Enterprise Linux"),
            (Aliases: new[] { "Rocky Linux", "Rocky" }, Name: "Rocky Linux"),
            (Aliases: new[] { "AlmaLinux", "Alma" }, Name: "AlmaLinux"),
            (Aliases: new[] { "Arch Linux", "Arch" }, Name: "Arch Linux"),
            (Aliases: new[] { "Kali Linux", "Kali" }, Name: "Kali Linux"),
            (Aliases: new[] { "openSUSE", "SUSE" }, Name: "openSUSE"),
            (Aliases: new[] { "Ubuntu" }, Name: "Ubuntu"),
            (Aliases: new[] { "CentOS" }, Name: "CentOS"),
            (Aliases: new[] { "Debian" }, Name: "Debian"),
            (Aliases: new[] { "Fedora" }, Name: "Fedora")
        };
        foreach (var distribution in linuxDistributions)
        {
            var alias = distribution.Aliases.FirstOrDefault(candidate =>
                text.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (alias is not null)
            {
                return ("Linux", distribution.Name, TrimVersion(text[alias.Length..]));
            }
        }

        if (text.StartsWith("Windows", StringComparison.OrdinalIgnoreCase))
        {
            var distribution = text.StartsWith("Windows Server", StringComparison.OrdinalIgnoreCase)
                ? "Windows Server"
                : text.StartsWith("Windows 11", StringComparison.OrdinalIgnoreCase)
                    ? "Windows 11"
                    : text.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase)
                        ? "Windows 10"
                        : "Windows（其他）";
            return ("Windows", distribution, TrimVersion(text[distribution.Replace("（其他）", string.Empty).Length..]));
        }

        if (text.StartsWith("macOS", StringComparison.OrdinalIgnoreCase))
        {
            return ("macOS", "macOS", TrimVersion(text[5..]));
        }

        foreach (var distribution in new[] { "FreeBSD", "OpenBSD", "NetBSD" })
        {
            if (text.StartsWith(distribution, StringComparison.OrdinalIgnoreCase))
            {
                return ("BSD", distribution, TrimVersion(text[distribution.Length..]));
            }
        }

        return ("其他", "其他系统", text);
    }

    internal static string? GetIconSource(string? operatingSystem, string? distribution)
    {
        var iconName = distribution?.Trim() switch
        {
            "Ubuntu" => "ubuntu",
            "CentOS" => "centos",
            "Debian" => "debian",
            "Fedora" => "fedora",
            "Rocky Linux" => "rockylinux",
            "AlmaLinux" => "almalinux",
            "Red Hat Enterprise Linux" => "redhat",
            "Arch Linux" => "archlinux",
            "openSUSE" => "opensuse",
            "Kali Linux" => "kalilinux",
            "Windows Server" or "Windows 11" or "Windows 10" or "Windows（其他）" => "windows11",
            "macOS" => "apple",
            "FreeBSD" => "freebsd",
            "OpenBSD" => "openbsd",
            "NetBSD" => "netbsd",
            _ => operatingSystem?.Trim() switch
            {
                "Linux" => "linux",
                "Windows" => "windows11",
                "macOS" => "apple",
                "BSD" => "freebsd",
                _ => null
            }
        };
        return iconName is null ? null : $"/Assets/InformationVault/{iconName}.png";
    }

    internal static string GetAccentBackground(string? operatingSystem, string? distribution) =>
        distribution?.Trim() switch
        {
            "Ubuntu" => "#FFF0EA",
            "CentOS" => "#ECECF8",
            "Debian" => "#F9E9EC",
            "Fedora" => "#EAF5FB",
            "Rocky Linux" => "#E7F8F1",
            "AlmaLinux" => "#F0F1F3",
            "Red Hat Enterprise Linux" => "#FFE9E9",
            "Arch Linux" => "#E7F4FA",
            "openSUSE" => "#EFF8E5",
            "Kali Linux" => "#EBF1F5",
            "Windows Server" or "Windows 11" or "Windows 10" or "Windows（其他）" => "#E6F2FB",
            "macOS" => "#F0F1F3",
            "FreeBSD" => "#F9EAE9",
            "OpenBSD" => "#FFF8D9",
            "NetBSD" => "#FFF0E5",
            _ => operatingSystem?.Trim() switch
            {
                "Windows" => "#E6F2FB",
                "BSD" => "#F9EAE9",
                _ => "#F0F1F3"
            }
        };

    internal static string GetAccentForeground(string? operatingSystem, string? distribution)
    {
        return distribution?.Trim() switch
        {
            "Ubuntu" => "#E95420",
            "CentOS" => "#262577",
            "Debian" => "#A81D33",
            "Fedora" => "#3986BA",
            "Rocky Linux" => "#0D9369",
            "AlmaLinux" => "#222222",
            "Red Hat Enterprise Linux" => "#D90000",
            "Arch Linux" => "#147FB4",
            "openSUSE" => "#5B9C1E",
            "Kali Linux" => "#557C94",
            "Windows Server" or "Windows 11" or "Windows 10" or "Windows（其他）" => "#0078D4",
            "macOS" => "#222222",
            "FreeBSD" => "#AB2B28",
            "OpenBSD" => "#A78300",
            "NetBSD" => "#D95700",
            _ => operatingSystem?.Trim() switch
            {
                "Windows" => "#0078D4",
                "BSD" => "#AB2B28",
                _ => "#4E5966"
            }
        };
    }

    private static string TrimVersion(string value) => value.Trim().TrimStart('-', '_', '·', ' ');
}

internal sealed record InformationVaultEntryTypeOption(InformationVaultEntryType Type, string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal static class InformationVaultEntryTypes
{
    internal static IReadOnlyList<InformationVaultEntryTypeOption> Options { get; } =
    [
        new(InformationVaultEntryType.GitHubCredential, "GitHub 凭据"),
        new(InformationVaultEntryType.DeepSeekApiKey, "DeepSeek API 密钥"),
        new(InformationVaultEntryType.Steam, "Steam"),
        new(InformationVaultEntryType.Ubisoft, "Ubisoft Connect"),
        new(InformationVaultEntryType.Epic, "Epic Games"),
        new(InformationVaultEntryType.MySql, "MySQL"),
        new(InformationVaultEntryType.Redis, "Redis"),
        new(InformationVaultEntryType.VirtualMachine, "虚拟机"),
        new(InformationVaultEntryType.WeChat, "微信"),
        new(InformationVaultEntryType.QQ, "QQ"),
        new(InformationVaultEntryType.Custom, "自定义安全信息")
    ];

    internal static string GetDisplayName(InformationVaultEntryType type) =>
        type == InformationVaultEntryType.RecoveryCodes
            ? "GitHub 凭据"
            : Options.FirstOrDefault(option => option.Type == type)?.DisplayName ?? "自定义安全信息";

    internal static string GetDefaultTitle(InformationVaultEntryType type) => type switch
    {
        InformationVaultEntryType.GitHubCredential or InformationVaultEntryType.RecoveryCodes => "GitHub 凭据",
        InformationVaultEntryType.DeepSeekApiKey => "DeepSeek API 密钥",
        InformationVaultEntryType.Steam => "Steam",
        InformationVaultEntryType.Ubisoft => "Ubisoft Connect",
        InformationVaultEntryType.Epic => "Epic Games",
        InformationVaultEntryType.WeChat => "微信",
        InformationVaultEntryType.QQ => "QQ",
        _ => string.Empty
    };

    internal static bool UsesEditableTitle(InformationVaultEntryType type) =>
        type is InformationVaultEntryType.MySql or InformationVaultEntryType.Redis or
            InformationVaultEntryType.VirtualMachine or InformationVaultEntryType.Custom;

    internal static bool UsesAccount(InformationVaultEntryType type) =>
        type is InformationVaultEntryType.GitHubCredential or InformationVaultEntryType.Steam or InformationVaultEntryType.Ubisoft or
            InformationVaultEntryType.Epic or InformationVaultEntryType.MySql or
            InformationVaultEntryType.Redis or InformationVaultEntryType.VirtualMachine or
            InformationVaultEntryType.WeChat or InformationVaultEntryType.QQ or
            InformationVaultEntryType.Custom;

    internal static bool RequiresAccount(InformationVaultEntryType type) =>
        type is InformationVaultEntryType.GitHubCredential or InformationVaultEntryType.Steam or InformationVaultEntryType.Ubisoft or
            InformationVaultEntryType.Epic or InformationVaultEntryType.WeChat or InformationVaultEntryType.QQ;

    internal static bool SupportsAutoFill(InformationVaultEntryType type) =>
        type is InformationVaultEntryType.Steam or InformationVaultEntryType.Ubisoft or InformationVaultEntryType.Epic;

    internal static bool UsesConnectionFields(InformationVaultEntryType type) =>
        type is InformationVaultEntryType.MySql or InformationVaultEntryType.Redis or InformationVaultEntryType.VirtualMachine;
}
