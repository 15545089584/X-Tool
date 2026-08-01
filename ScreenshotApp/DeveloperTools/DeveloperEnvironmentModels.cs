using System.Collections.ObjectModel;

namespace ScreenshotApp.DeveloperTools;

public enum DeveloperIssueSeverity
{
    Info,
    Warning,
    Error
}

public sealed class ToolchainInstallation
{
    public string ToolchainId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Version { get; init; } = "待验证";
    public string ExecutablePath { get; init; } = string.Empty;
    public string InstallationPath { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Architecture { get; init; } = Environment.Is64BitOperatingSystem ? "x64" : "x86";
    public string Evidence { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public bool IsVerified { get; init; }

    public string StateText => IsActive ? "当前命令" : IsVerified ? "已验证" : "已发现";
    public string StateBackground => IsActive ? "#DCEBFF" : IsVerified ? "#DDF6EA" : "#EEF1F5";
    public string StateForeground => IsActive ? "#326DAF" : IsVerified ? "#367A59" : "#66717E";
    public string LocationText => string.IsNullOrWhiteSpace(InstallationPath) ? ExecutablePath : InstallationPath;
}

public sealed class ToolchainSummary
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string IconGlyph { get; init; } = "\uE943";
    public string IconBackground { get; init; } = "#E6F0FF";
    public string IconForeground { get; init; } = "#2D7DFF";
    public ObservableCollection<ToolchainInstallation> Installations { get; } = new();

    public int InstallationCount => Installations.Count;
    public string CountText => InstallationCount == 0 ? "未发现" : $"{InstallationCount} 个安装";
    public string ActiveVersion => Installations.FirstOrDefault(item => item.IsActive)?.Version
        ?? Installations.FirstOrDefault(item => item.IsVerified)?.Version
        ?? "未配置";
    public string ActivePath => Installations.FirstOrDefault(item => item.IsActive)?.ExecutablePath
        ?? Installations.FirstOrDefault()?.ExecutablePath
        ?? "未在常用位置发现";
    public bool HasActiveCommand => Installations.Any(item => item.IsActive);
    public bool HasConflict => Installations.Count(item => item.IsActive) > 1;
    public string StatusText => InstallationCount == 0 ? "未发现" : HasConflict ? "存在冲突" : !HasActiveCommand ? "未加入 PATH" : Installations.Any(item => item.IsVerified) ? "正常" : "待验证";
    public string StatusBackground => InstallationCount == 0 ? "#EEF1F5" : HasConflict || !HasActiveCommand ? "#FFF0D9" : Installations.Any(item => item.IsVerified) ? "#DDF6EA" : "#E8F0FF";
    public string StatusForeground => InstallationCount == 0 ? "#6E7782" : HasConflict || !HasActiveCommand ? "#A56B13" : Installations.Any(item => item.IsVerified) ? "#367A59" : "#426FA8";
}

public sealed class DeveloperDiagnosticIssue
{
    public DeveloperIssueSeverity Severity { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;
    public string SeverityText => Severity switch
    {
        DeveloperIssueSeverity.Error => "错误",
        DeveloperIssueSeverity.Warning => "注意",
        _ => "提示"
    };
    public string AccentColor => Severity switch
    {
        DeveloperIssueSeverity.Error => "#D95656",
        DeveloperIssueSeverity.Warning => "#E7A43B",
        _ => "#4B86D9"
    };
    public string BadgeBackground => Severity switch
    {
        DeveloperIssueSeverity.Error => "#FFE5E5",
        DeveloperIssueSeverity.Warning => "#FFF0D9",
        _ => "#E4EFFF"
    };
}

public sealed class DeveloperEnvironmentSnapshot
{
    public DateTime ScannedAt { get; init; } = DateTime.Now;
    public ObservableCollection<ToolchainSummary> Toolchains { get; } = new();
    public ObservableCollection<DeveloperDiagnosticIssue> Issues { get; } = new();
    public int DetectedToolCount => Toolchains.Count(item => item.InstallationCount > 0);
    public int InstallationCount => Toolchains.Sum(item => item.InstallationCount);
    public int WarningCount => Issues.Count(item => item.Severity != DeveloperIssueSeverity.Info);
    public string ScannedAtText => $"更新于 {ScannedAt:HH:mm:ss}";
    public string SummaryText => $"已识别 {DetectedToolCount} 类工具、{InstallationCount} 个安装，发现 {WarningCount} 个需关注项";
}

public sealed record DeveloperScanProgress(string Message, int Completed, int Total)
{
    public double Percentage => Total <= 0 ? 0 : Completed * 100d / Total;
}
