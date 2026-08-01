using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenshotApp.DeveloperTools;

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
    public string FileName { get; init; } = string.Empty;
    public long DownloadSize { get; init; }
    public bool IsLts { get; init; }

    public string VersionText => IsLts ? $"{Version} · LTS" : Version;
    public string SizeText => DownloadSize <= 0 ? "大小未知" : $"{DownloadSize / 1024d / 1024d:N1} MB";
    public string SourceText => ProviderId switch
    {
        "temurin" => "Eclipse Adoptium 官方发行版",
        "uv" => "uv 官方托管 Python",
        _ => ProviderId
    };
    public string ActionText => IsInstalled ? "卸载" : "安装";

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
    public DateTime InstalledAtUtc { get; init; }
    public bool ManagedByXTool { get; init; } = true;
}

public sealed class ManagedToolchainManifest
{
    public int SchemaVersion { get; init; } = 1;
    public List<ManagedToolchainEntry> Installations { get; init; } = new();
}

public sealed record ManagedInstallProgress(string Stage, long BytesReceived = 0, long TotalBytes = 0)
{
    public double Percentage => TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100);
    public string DisplayText => TotalBytes > 0
        ? $"{Stage} · {BytesReceived / 1024d / 1024d:N1} / {TotalBytes / 1024d / 1024d:N1} MB"
        : Stage;
}

public sealed record ManagedToolchainOperationResult(bool Succeeded, string Message, ManagedToolchainEntry? Entry = null);
