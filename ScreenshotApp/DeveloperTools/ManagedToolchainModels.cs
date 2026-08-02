using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenshotApp.DeveloperTools;

public enum ManagedDownloadSource
{
    Official,
    Tuna
}

public sealed class ManagedToolchainRelease : INotifyPropertyChanged
{
    private bool _isInstalled;
    private bool _isBusy;
    private double _progressPercentage;
    private string _progressText = string.Empty;

    public string ToolchainId { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Architecture { get; init; } = "x64";
    public string DownloadUrl { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string HashAlgorithm { get; init; } = "SHA256";
    public string FileName { get; init; } = string.Empty;
    public long DownloadSize { get; init; }
    public int FeatureVersion { get; init; }
    public bool IsLts { get; init; }
    public bool IsRecommended { get; init; }
    public string ReleaseChannelText { get; init; } = string.Empty;
    public bool IsHistorical => !IsRecommended;
    public bool IsProviderAvailable { get; init; } = true;
    public bool IsReadOnlyInstalled { get; init; }

    public string VersionText => IsLts ? $"{Version} · LTS" : Version;
    public string SupportText => IsLts ? "LTS" :
        !string.IsNullOrWhiteSpace(ReleaseChannelText) ? ReleaseChannelText :
        IsRecommended ? "推荐版本" : "历史版本";
    public string SupportBackground => IsHistorical ? "#FFF0D9" : "#DDF6EA";
    public string SupportForeground => IsHistorical ? "#9A671A" : "#367A59";
    public string SizeText => DownloadSize <= 0 ? "大小未知" : $"{DownloadSize / 1024d / 1024d:N1} MB";
    public string SourceText => ProviderId switch
    {
        "temurin" => "Eclipse Adoptium 官方发行版",
        "uv" => "uv 官方托管 Python",
        "mysql" => "MySQL 官方 CDN 归档",
        "docker-desktop" => "Docker Desktop 官方更新源",
        "docker-cli" => "Docker 官方 Windows 静态包",
        _ => ProviderId
    };
    public string ActionText => !IsProviderAvailable ? "需要 Volta" :
        IsReadOnlyInstalled ? (ProviderId == "volta" ? "已缓存" : "已安装") :
        IsInstalled ? "卸载" :
        ProviderId == "volta" ? "缓存版本" :
        ProviderId == "docker-desktop" ? "下载安装器" : "安装";
    public bool CanExecuteAction => IsProviderAvailable && !IsReadOnlyInstalled;

    public bool IsInstalled
    {
        get => _isInstalled;
        set { if (_isInstalled == value) return; _isInstalled = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActionText)); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { if (_isBusy == value) return; _isBusy = value; OnPropertyChanged(); }
    }

    public double ProgressPercentage
    {
        get => _progressPercentage;
        set { if (Math.Abs(_progressPercentage - value) < 0.01) return; _progressPercentage = value; OnPropertyChanged(); }
    }

    public string ProgressText
    {
        get => _progressText;
        set { if (_progressText == value) return; _progressText = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ManagedToolchainEntry
{
    public string ToolchainId { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string InstallationPath { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string PackageKey { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string HashAlgorithm { get; init; } = "SHA256";
    public DateTime InstalledAtUtc { get; init; }
    public bool ManagedByXTool { get; init; } = true;
}

public sealed class ManagedToolchainManifest
{
    public int SchemaVersion { get; init; } = 1;
    public List<ManagedToolchainEntry> Installations { get; init; } = new();
}

public sealed record ManagedInstallProgress(
    string Stage,
    long BytesReceived = 0,
    long TotalBytes = 0,
    double BytesPerSecond = 0)
{
    public double Percentage => TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100);
    public string DisplayText
    {
        get
        {
            if (TotalBytes <= 0) return Stage;
            var text = $"{Stage} · {BytesReceived / 1024d / 1024d:N1} / {TotalBytes / 1024d / 1024d:N1} MB";
            if (BytesPerSecond <= 0) return text;
            var seconds = Math.Max(0, (TotalBytes - BytesReceived) / BytesPerSecond);
            var remaining = seconds < 60 ? $"约 {Math.Ceiling(seconds):N0} 秒" : $"约 {Math.Ceiling(seconds / 60):N0} 分钟";
            return $"{text} · {BytesPerSecond / 1024d / 1024d:N1} MB/s · 剩余 {remaining}";
        }
    }
}

public sealed record ManagedToolchainOperationResult(bool Succeeded, string Message, ManagedToolchainEntry? Entry = null);
