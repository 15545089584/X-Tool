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

            if (Type == InformationVaultEntryType.RecoveryCodes)
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
    public int Version { get; set; } = 1;

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
        new(InformationVaultEntryType.RecoveryCodes, "恢复码"),
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
        Options.First(option => option.Type == type).DisplayName;

    internal static bool SupportsAutoFill(InformationVaultEntryType type) =>
        type is InformationVaultEntryType.Steam or InformationVaultEntryType.Ubisoft or InformationVaultEntryType.Epic;

    internal static bool UsesConnectionFields(InformationVaultEntryType type) =>
        type is InformationVaultEntryType.MySql or InformationVaultEntryType.Redis or InformationVaultEntryType.VirtualMachine;
}
