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

    public string Host { get; set; } = string.Empty;

    public string Port { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

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
    public bool HasHost => !string.IsNullOrWhiteSpace(Host);

    [JsonIgnore]
    public bool HasPort => !string.IsNullOrWhiteSpace(Port);

    [JsonIgnore]
    public bool HasDatabase => !string.IsNullOrWhiteSpace(Database);

    [JsonIgnore]
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    [JsonIgnore]
    public bool HasRecoveryCodes => RecoveryCodes.Count > 0;

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
        InformationVaultEntryType.GitHubCredential => "#FFF0E1",
        InformationVaultEntryType.RecoveryCodes => "#EEE7FF",
        InformationVaultEntryType.DeepSeekApiKey => "#E2F2FF",
        InformationVaultEntryType.Steam => "#E4ECFF",
        InformationVaultEntryType.Ubisoft => "#E5F3FF",
        InformationVaultEntryType.Epic => "#F0EAFF",
        InformationVaultEntryType.MySql => "#E3F5F2",
        InformationVaultEntryType.Redis => "#FFE8E8",
        InformationVaultEntryType.VirtualMachine => "#E5F4E8",
        InformationVaultEntryType.WeChat => "#E1F7E8",
        InformationVaultEntryType.QQ => "#E4F1FF",
        _ => "#EEF2F7"
    };

    [JsonIgnore]
    public string AccentForeground => Type switch
    {
        InformationVaultEntryType.GitHubCredential => "#C57A18",
        InformationVaultEntryType.RecoveryCodes => "#7B57C5",
        InformationVaultEntryType.DeepSeekApiKey => "#397FB8",
        InformationVaultEntryType.Steam => "#4C68B7",
        InformationVaultEntryType.Ubisoft => "#3B7DA9",
        InformationVaultEntryType.Epic => "#7254AD",
        InformationVaultEntryType.MySql => "#377F72",
        InformationVaultEntryType.Redis => "#BA5353",
        InformationVaultEntryType.VirtualMachine => "#4D8757",
        InformationVaultEntryType.WeChat => "#3C955F",
        InformationVaultEntryType.QQ => "#477EAF",
        _ => "#60758A"
    };

    [JsonIgnore]
    public string UsageBadge => SupportsAutoFill
        ? "支持一键填入"
        : HasRecoveryCodes
            ? $"剩余 {RecoveryCodes.Count(code => !code.IsUsed)} 个"
            : "安全复制";

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
            Host = Host,
            Port = Port,
            Database = Database,
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
    public int Version { get; set; } = 2;

    public List<InformationVaultEntry> Entries { get; set; } = [];
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
        type is InformationVaultEntryType.Steam or InformationVaultEntryType.Ubisoft or
            InformationVaultEntryType.Epic or InformationVaultEntryType.MySql or
            InformationVaultEntryType.Redis or InformationVaultEntryType.VirtualMachine or
            InformationVaultEntryType.WeChat or InformationVaultEntryType.QQ or
            InformationVaultEntryType.Custom;

    internal static bool RequiresAccount(InformationVaultEntryType type) =>
        type is InformationVaultEntryType.Steam or InformationVaultEntryType.Ubisoft or
            InformationVaultEntryType.Epic or InformationVaultEntryType.WeChat or InformationVaultEntryType.QQ;

    internal static bool SupportsAutoFill(InformationVaultEntryType type) =>
        type is InformationVaultEntryType.Steam or InformationVaultEntryType.Ubisoft or InformationVaultEntryType.Epic;

    internal static bool UsesConnectionFields(InformationVaultEntryType type) =>
        type is InformationVaultEntryType.MySql or InformationVaultEntryType.Redis or InformationVaultEntryType.VirtualMachine;
}
