namespace ScreenshotApp.SystemTools;

/// <summary>把 Windows 事件来源与事件编号映射为用户可理解的只读排查线索。</summary>
internal static class SystemDiagnosticRules
{
    private static readonly HashSet<int> ServiceFailureEventIds = new()
    {
        7000, 7001, 7002, 7003, 7005, 7009, 7011, 7022, 7023, 7024, 7031, 7032, 7034
    };

    internal static SystemDiagnosticRule Match(string logName, string providerName, int eventId, SystemDiagnosticSeverity originalSeverity)
    {
        if (ProviderEquals(providerName, "Microsoft-Windows-Kernel-Power") && eventId == 41)
        {
            return new(SystemDiagnosticCategory.Shutdown, SystemDiagnosticSeverity.Critical, "系统曾意外重新启动",
                "Windows 发现上一次关机流程没有正常完成。它只能证明系统意外中断，不能单独确定是电源、蓝屏、死机还是强制关机。",
                "结合相同时间附近的蓝屏、WHEA、磁盘与驱动事件继续排查；若仅偶发一次，可先观察是否再次出现。");
        }

        if (ProviderEquals(providerName, "EventLog") && eventId == 6008)
        {
            return new(SystemDiagnosticCategory.Shutdown, SystemDiagnosticSeverity.Error, "Windows 记录到异常关机",
                "系统日志确认此前存在一次非正常关机或重启。",
                "查看同一时间前后的 Kernel-Power、BugCheck、存储或驱动事件，避免仅凭此事件判断具体原因。");
        }

        if (ProviderEquals(providerName, "Microsoft-Windows-WER-SystemErrorReporting") && eventId == 1001)
        {
            return new(SystemDiagnosticCategory.Shutdown, SystemDiagnosticSeverity.Critical, "Windows 记录到蓝屏错误",
                "Windows 错误报告记录了系统停止错误。事件描述中通常包含停止代码与转储文件位置。",
                "保存停止代码并检查近期驱动、硬件稳定性和转储文件；X-Tool 不会自动修改或删除转储文件。");
        }

        if (ProviderContains(providerName, "WHEA-Logger"))
        {
            return new(SystemDiagnosticCategory.Hardware,
                originalSeverity == SystemDiagnosticSeverity.Warning ? SystemDiagnosticSeverity.Warning : SystemDiagnosticSeverity.Critical,
                "检测到硬件错误记录",
                "WHEA 记录可能来自处理器、内存、PCIe 或其他硬件。部分事件是已纠正错误，并不等同于硬件已经损坏。",
                "结合事件原始描述确认错误来源；若持续重复，检查超频、电源、散热、内存和相关设备驱动。");
        }

        if (IsStorageProvider(providerName))
        {
            return new(SystemDiagnosticCategory.Storage, originalSeverity, "检测到存储或文件系统异常",
                "该记录来自 Windows 存储、磁盘控制器、卷或文件系统组件，可能涉及 I/O 重试、超时或文件系统状态。",
                "先确认事件是否重复发生，并检查对应卷的剩余空间与系统日志；不要仅凭单条警告判断磁盘寿命。");
        }

        if (IsDriverProvider(providerName))
        {
            return new(SystemDiagnosticCategory.Driver, originalSeverity, "检测到驱动或设备异常",
                "Windows 设备或驱动框架记录了加载、启动、重置或响应异常。",
                "前往“设备信息”的异常项核对设备状态与驱动版本；更新或回退驱动应由用户在确认设备后自行执行。");
        }

        if (IsApplicationCrash(providerName, eventId))
        {
            var title = ProviderContains(providerName, "Hang") ? "应用程序曾无响应" : "应用程序发生崩溃";
            var reliabilitySeverity =
                (ProviderEquals(providerName, "Application Error") && eventId == 1000) ||
                (ProviderEquals(providerName, "Application Hang") && eventId == 1002) ||
                (ProviderEquals(providerName, "Windows Error Reporting") && eventId == 1001)
                    ? SystemDiagnosticSeverity.Critical
                    : originalSeverity;
            return new(SystemDiagnosticCategory.Application, reliabilitySeverity, title,
                reliabilitySeverity == SystemDiagnosticSeverity.Critical
                    ? "Windows 记录了影响应用稳定性的崩溃或无响应。按照可靠性监视器口径，此类故障归为关键事件。"
                    : "Windows 记录了运行时未处理异常；它作为崩溃诊断线索保留，但不会重复计入可靠性关键事件。",
                "检查应用版本、插件和故障模块；频繁出现时可复制事件详情提供给应用开发者。");
        }

        if (ProviderEquals(providerName, "Service Control Manager") && ServiceFailureEventIds.Contains(eventId))
        {
            return new(SystemDiagnosticCategory.Service, originalSeverity, "系统服务启动或运行异常",
                "服务控制管理器记录了服务启动失败、超时或意外终止。部分按需服务偶发失败并不一定影响正常使用。",
                "确认服务名称和重复次数；需要操作时前往“资源管理 → 服务管理”，系统诊断页不会自动启动或停止服务。");
        }

        if (ProviderContains(providerName, "WindowsUpdateClient"))
        {
            return new(SystemDiagnosticCategory.Update, originalSeverity, "Windows 更新记录了异常",
                "Windows Update 客户端记录了下载、安装或状态检查异常。",
                "打开 Windows 更新设置查看当前状态与重试结果；不要仅依据一次网络或服务警告判断更新已损坏。");
        }

        var category = string.Equals(logName, "Application", StringComparison.OrdinalIgnoreCase)
            ? SystemDiagnosticCategory.Application
            : SystemDiagnosticCategory.System;
        return new(category, originalSeverity,
            category == SystemDiagnosticCategory.Application ? "应用程序记录了异常" : "系统组件记录了异常",
            "该事件没有命中特定诊断规则，X-Tool 保留 Windows 原始级别与描述供进一步判断。",
            "优先查看事件来源、原始描述与重复次数；单次警告通常不足以确认存在故障。");
    }

    private static bool IsStorageProvider(string providerName) =>
        ProviderEquals(providerName, "Disk") ||
        ProviderEquals(providerName, "Ntfs") ||
        ProviderEquals(providerName, "volmgr") ||
        ProviderContains(providerName, "stornvme") ||
        ProviderContains(providerName, "storahci") ||
        ProviderContains(providerName, "Storage-ClassPnP") ||
        ProviderContains(providerName, "Partition") ||
        ProviderContains(providerName, "FileSystem");

    private static bool IsDriverProvider(string providerName) =>
        ProviderContains(providerName, "Kernel-PnP") ||
        ProviderContains(providerName, "DriverFrameworks") ||
        ProviderEquals(providerName, "Display") ||
        ProviderContains(providerName, "UserPnp");

    private static bool IsApplicationCrash(string providerName, int eventId) =>
        (ProviderEquals(providerName, "Application Error") && eventId == 1000) ||
        (ProviderEquals(providerName, ".NET Runtime") && eventId == 1026) ||
        (ProviderEquals(providerName, "Windows Error Reporting") && eventId == 1001) ||
        (ProviderEquals(providerName, "Application Hang") && eventId == 1002);

    private static bool ProviderEquals(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static bool ProviderContains(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);
}

internal sealed record SystemDiagnosticRule(
    SystemDiagnosticCategory Category,
    SystemDiagnosticSeverity Severity,
    string Title,
    string Explanation,
    string Recommendation);
