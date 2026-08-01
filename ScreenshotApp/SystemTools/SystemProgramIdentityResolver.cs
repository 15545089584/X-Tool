using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace ScreenshotApp.SystemTools;

internal sealed record SystemProgramIdentity(
    string FriendlyName,
    string ExecutableName,
    string Publisher,
    string IdentityType,
    string Evidence)
{
    public bool HasFriendlyName => !string.IsNullOrWhiteSpace(FriendlyName) &&
                                   !string.Equals(FriendlyName, ExecutableName, StringComparison.OrdinalIgnoreCase);
    public string PublisherText => string.IsNullOrWhiteSpace(Publisher) ? "发布者未知" : Publisher;
    public string DetailText => string.Join(" · ", new[] { ExecutableName, PublisherText, IdentityType, Evidence }
        .Where(value => !string.IsNullOrWhiteSpace(value)));

    public static SystemProgramIdentity FromSubject(string subject)
    {
        var executableName = Path.GetFileName(subject ?? string.Empty);
        return new SystemProgramIdentity(executableName, executableName, string.Empty, "应用程序", "仅依据事件名称");
    }
}

/// <summary>在事件完成聚合后按路径解析程序身份，避免对每条原始事件重复访问磁盘。</summary>
internal static class SystemProgramIdentityResolver
{
    private static readonly ConcurrentDictionary<string, SystemProgramIdentity> IdentityCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ServiceCatalogLock = new();
    private static ServiceIdentityCatalog? _cachedServiceCatalog;
    private static DateTime _serviceCatalogCachedAtUtc;
    private static readonly IReadOnlyDictionary<string, KnownProgramIdentity> KnownPrograms =
        new Dictionary<string, KnownProgramIdentity>(StringComparer.OrdinalIgnoreCase)
        {
            ["MsMpEng.exe"] = new("Microsoft Defender 防病毒服务", "Microsoft Corporation", "Windows 安全组件"),
            ["explorer.exe"] = new("Windows 文件资源管理器", "Microsoft Corporation", "Windows 系统组件"),
            ["svchost.exe"] = new("Windows 服务主机", "Microsoft Corporation", "Windows 系统组件"),
            ["dwm.exe"] = new("桌面窗口管理器", "Microsoft Corporation", "Windows 系统组件"),
            ["SearchHost.exe"] = new("Windows 搜索", "Microsoft Corporation", "Windows 系统组件"),
            ["RuntimeBroker.exe"] = new("Windows 运行时代理", "Microsoft Corporation", "Windows 系统组件"),
            ["SecurityHealthService.exe"] = new("Windows 安全中心服务", "Microsoft Corporation", "Windows 安全组件"),
            ["TiWorker.exe"] = new("Windows 模块安装程序", "Microsoft Corporation", "Windows 系统组件"),
            ["MoUsoCoreWorker.exe"] = new("Windows 更新协调服务", "Microsoft Corporation", "Windows 系统组件")
        };

    internal static IReadOnlyList<SystemDiagnosticGroup> Resolve(
        IReadOnlyList<SystemDiagnosticGroup> groups,
        CancellationToken cancellationToken)
    {
        if (!groups.Any(group => group.Category == SystemDiagnosticCategory.Application))
        {
            return groups;
        }

        var services = GetServiceIdentities();
        var resolved = new SystemDiagnosticGroup[groups.Count];
        for (var index = 0; index < groups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = groups[index];
            resolved[index] = group.Category == SystemDiagnosticCategory.Application
                ? group with { ProgramIdentity = Resolve(group, services) }
                : group;
        }

        return resolved;
    }

    private static SystemProgramIdentity Resolve(SystemDiagnosticGroup group, ServiceIdentityCatalog services)
    {
        var executablePath = group.ApplicationExecutablePath;
        var executableName = GetExecutableName(executablePath, group.Subject);
        var cacheKey = !string.IsNullOrWhiteSpace(executablePath)
            ? executablePath
            : $"name:{executableName}";
        return IdentityCache.GetOrAdd(cacheKey, _ => ResolveCore(executablePath, executableName, services));
    }

    private static SystemProgramIdentity ResolveCore(
        string executablePath,
        string executableName,
        ServiceIdentityCatalog services)
    {
        var service = services.Find(executablePath, executableName);
        KnownPrograms.TryGetValue(executableName, out var known);

        string productName = string.Empty;
        string fileDescription = string.Empty;
        string companyName = string.Empty;
        string signedPublisher = string.Empty;
        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(executablePath);
                productName = CleanMetadata(version.ProductName);
                fileDescription = CleanMetadata(version.FileDescription);
                companyName = CleanMetadata(version.CompanyName);
            }
            catch
            {
                // 受保护文件或不完整版本资源不应影响系统诊断结果。
            }

            signedPublisher = ReadSignedPublisher(executablePath);
        }

        var friendlyName = FirstUsefulName(
            known?.FriendlyName,
            service?.DisplayName,
            productName,
            fileDescription,
            executableName);
        var publisher = FirstNonEmpty(signedPublisher, companyName, known?.Publisher);
        var identityType = FirstNonEmpty(
            known?.IdentityType,
            service is not null ? "Windows 服务" : string.Empty,
            IsWindowsComponent(executablePath, publisher) ? "Windows 系统组件" : "桌面应用");
        var evidence = !string.IsNullOrWhiteSpace(signedPublisher)
            ? "已读取数字签名与文件信息"
            : service is not null
                ? "已关联 Windows 服务"
                : known is not null
                    ? "来自 Windows 组件目录"
                    : !string.IsNullOrWhiteSpace(productName) || !string.IsNullOrWhiteSpace(fileDescription)
                        ? "已读取文件版本信息"
                        : "仅依据事件名称";

        return new SystemProgramIdentity(friendlyName, executableName, publisher, identityType, evidence);
    }

    private static string ReadSignedPublisher(string executablePath)
    {
        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(executablePath);
            using var certificate2 = new X509Certificate2(certificate);
            return CleanMetadata(certificate2.GetNameInfo(X509NameType.SimpleName, false));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ServiceIdentityCatalog GetServiceIdentities()
    {
        lock (ServiceCatalogLock)
        {
            if (_cachedServiceCatalog is not null && DateTime.UtcNow - _serviceCatalogCachedAtUtc < TimeSpan.FromMinutes(10))
            {
                return _cachedServiceCatalog;
            }

            _cachedServiceCatalog = ReadServiceIdentities();
            _serviceCatalogCachedAtUtc = DateTime.UtcNow;
            return _cachedServiceCatalog;
        }
    }

    private static ServiceIdentityCatalog ReadServiceIdentities()
    {
        var identities = new List<ServiceIdentity>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DisplayName,PathName FROM Win32_Service");
            using var services = searcher.Get();
            foreach (ManagementObject service in services)
            {
                using (service)
                {
                    var displayName = service["DisplayName"]?.ToString()?.Trim() ?? string.Empty;
                    var executablePath = ExtractExecutablePath(service["PathName"]?.ToString());
                    if (!string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(executablePath))
                    {
                        identities.Add(new ServiceIdentity(displayName, executablePath, Path.GetFileName(executablePath)));
                    }
                }
            }
        }
        catch
        {
            // 服务清单是增强信息，WMI 不可用时继续使用文件版本和内置组件目录。
        }

        return new ServiceIdentityCatalog(identities);
    }

    private static string ExtractExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        var expanded = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            return closingQuote > 1 ? expanded[1..closingQuote] : string.Empty;
        }

        var match = Regex.Match(expanded, @"^(.+?\.exe)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static string GetExecutableName(string executablePath, string subject)
    {
        var value = !string.IsNullOrWhiteSpace(executablePath) ? Path.GetFileName(executablePath) : Path.GetFileName(subject);
        return string.IsNullOrWhiteSpace(value) ? subject.Trim() : value;
    }

    private static string FirstUsefulName(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var value = CleanMetadata(candidate);
            if (!string.IsNullOrWhiteSpace(value) && !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return CleanMetadata(candidates.LastOrDefault());
    }

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.Select(CleanMetadata).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string CleanMetadata(string? value) => value?.Replace('\r', ' ').Replace('\n', ' ').Trim() ?? string.Empty;

    private static bool IsWindowsComponent(string executablePath, string publisher) =>
        publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(executablePath) && executablePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase));

    private sealed record KnownProgramIdentity(string FriendlyName, string Publisher, string IdentityType);
    private sealed record ServiceIdentity(string DisplayName, string ExecutablePath, string ExecutableName);

    private sealed class ServiceIdentityCatalog
    {
        private readonly IReadOnlyList<ServiceIdentity> _identities;

        internal ServiceIdentityCatalog(IReadOnlyList<ServiceIdentity> identities) => _identities = identities;

        internal ServiceIdentity? Find(string executablePath, string executableName)
        {
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var exact = _identities.FirstOrDefault(identity =>
                    string.Equals(identity.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase));
                if (exact is not null)
                {
                    return exact;
                }
            }

            var byName = _identities
                .Where(identity => string.Equals(identity.ExecutableName, executableName, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            return byName.Length == 1 ? byName[0] : null;
        }
    }
}
