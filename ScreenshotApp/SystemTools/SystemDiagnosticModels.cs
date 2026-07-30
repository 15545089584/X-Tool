using System.Text.RegularExpressions;

namespace ScreenshotApp.SystemTools;

internal enum SystemDiagnosticSeverity
{
    Critical = 1,
    Error = 2,
    Warning = 3
}

/// <summary>统一时间格、分组标题和事件标签的等级视觉语义。</summary>
internal sealed record SystemDiagnosticSeverityDisplay(
    string Title,
    string Description,
    string Color,
    string TextColor,
    string SoftBackground,
    string Glyph)
{
    private static readonly SystemDiagnosticSeverityDisplay CriticalDisplay = new(
        "关键事件",
        "影响可靠性的停止工作、无响应或系统故障",
        "#E56565",
        "#C54848",
        "#20E56565",
        "\uE711");

    private static readonly SystemDiagnosticSeverityDisplay ErrorDisplay = new(
        "错误事件",
        "需要进一步排查的系统或应用错误",
        "#F08A62",
        "#C65D36",
        "#20F08A62",
        "\uE783");

    private static readonly SystemDiagnosticSeverityDisplay WarningDisplay = new(
        "警告事件",
        "可能影响稳定性的系统与应用警告",
        "#3B9EFF",
        "#247CC7",
        "#203B9EFF",
        "\uE7BA");

    public static SystemDiagnosticSeverityDisplay For(SystemDiagnosticSeverity severity) => severity switch
    {
        SystemDiagnosticSeverity.Critical => CriticalDisplay,
        SystemDiagnosticSeverity.Error => ErrorDisplay,
        _ => WarningDisplay
    };
}

internal enum SystemDiagnosticCategory
{
    Shutdown,
    Hardware,
    Storage,
    Driver,
    Application,
    Service,
    Update,
    System,
    Other
}

internal sealed record SystemDiagnosticQuery(
    TimeSpan TimeRange,
    int MaximumEventsPerSegment = 400,
    int MaximumTotalEvents = 12000,
    int MaximumReliabilityRecords = 10000);

internal sealed record SystemDiagnosticProgress(string Stage, string Detail, int LogsCompleted, int TotalLogs, int EventsRead)
{
    public string SummaryText => $"{Stage} · {EventsRead:N0} 条事件";
}

internal sealed record SystemDiagnosticReadFailure(string LogName, string Reason);

/// <summary>用于异常分布图的轻量事件时间点，不保留事件正文。</summary>
internal sealed record SystemDiagnosticTimelineEvent(
    DateTime OccurredAt,
    SystemDiagnosticSeverity Severity,
    SystemDiagnosticCategory Category,
    string GroupKey);

internal sealed record SystemDiagnosticTimelineBucket(
    DateTime Start,
    DateTime End,
    string Label,
    int CriticalCount,
    int ErrorCount,
    int WarningCount)
{
    public int TotalCount => CriticalCount + ErrorCount + WarningCount;
}

internal sealed record SystemDiagnosticRecommendationStep(int Number, string Text);

internal sealed record SystemDiagnosticSnapshot(
    IReadOnlyList<SystemDiagnosticGroup> Groups,
    IReadOnlyList<SystemDiagnosticReadFailure> Failures,
    IReadOnlyList<SystemDiagnosticTimelineEvent> TimelineEvents,
    TimeSpan TimeRange,
    int EventsRead,
    bool WasTruncated,
    DateTime CompletedAt)
{
    public int CriticalCount => Groups.Count(group => group.Severity == SystemDiagnosticSeverity.Critical);
    public int ErrorCount => Groups.Count(group => group.Severity == SystemDiagnosticSeverity.Error);
    public int ApplicationCount => Groups.Count(group => group.Category == SystemDiagnosticCategory.Application);
    public int FrequentCount => Groups.Count(group => group.Count >= 3);
    public int AttentionCount => Groups.Count;

    public string SummaryText
    {
        get
        {
            var main = Groups.Count == 0
                ? $"已检查 {EventsRead:N0} 条可靠性与日志记录，未发现符合当前范围的异常"
                : $"从 {EventsRead:N0} 条可靠性与日志记录中归纳出 {Groups.Count:N0} 组需要关注的问题";
            if (WasTruncated)
            {
                main += "；日志较多，已达到本次读取上限";
            }

            if (Failures.Count > 0)
            {
                main += $"；{Failures.Count:N0} 个日志未能完整读取";
            }

            return main;
        }
    }
}

internal sealed record SystemDiagnosticGroup(
    string GroupKey,
    string LogName,
    string ProviderName,
    int EventId,
    long? RecordId,
    SystemDiagnosticSeverity Severity,
    SystemDiagnosticCategory Category,
    string Title,
    string Subject,
    string Explanation,
    string Recommendation,
    string Message,
    string EventXml,
    DateTime FirstSeen,
    DateTime LastSeen,
    int Count,
    IReadOnlyList<DateTime> Occurrences)
{
    /// <summary>用于界面稳定地按关键、错误、警告排序，不依赖枚举的隐式数值。</summary>
    public int SeverityPriority => Severity switch
    {
        SystemDiagnosticSeverity.Critical => 0,
        SystemDiagnosticSeverity.Error => 1,
        _ => 2
    };

    public string SeverityText => Severity switch
    {
        SystemDiagnosticSeverity.Critical => "关键",
        SystemDiagnosticSeverity.Error => "错误",
        _ => "警告"
    };

    public SystemDiagnosticSeverityDisplay SeverityDisplay => SystemDiagnosticSeverityDisplay.For(Severity);
    public string SeverityColor => SeverityDisplay.Color;
    public string SeverityTextColor => SeverityDisplay.TextColor;

    public string CategoryText => Category switch
    {
        SystemDiagnosticCategory.Shutdown => "启动与关机",
        SystemDiagnosticCategory.Hardware => "硬件",
        SystemDiagnosticCategory.Storage => "存储",
        SystemDiagnosticCategory.Driver => "驱动与设备",
        SystemDiagnosticCategory.Application => "应用程序",
        SystemDiagnosticCategory.Service => "系统服务",
        SystemDiagnosticCategory.Update => "Windows 更新",
        SystemDiagnosticCategory.System => "系统组件",
        _ => "其他"
    };

    public string CategoryColor => Category switch
    {
        SystemDiagnosticCategory.Shutdown => "#E56565",
        SystemDiagnosticCategory.Hardware => "#8B6CFF",
        SystemDiagnosticCategory.Storage => "#16B99B",
        SystemDiagnosticCategory.Driver => "#3B9EFF",
        SystemDiagnosticCategory.Application => "#F08A62",
        SystemDiagnosticCategory.Service => "#607D91",
        SystemDiagnosticCategory.Update => "#4D7CFE",
        _ => "#6D87A1"
    };

    public string IconGlyph => Category switch
    {
        SystemDiagnosticCategory.Shutdown => "\uE7E8",
        SystemDiagnosticCategory.Hardware => "\uEEA1",
        SystemDiagnosticCategory.Storage => "\uEDA2",
        SystemDiagnosticCategory.Driver => "\uE7BA",
        SystemDiagnosticCategory.Application => "\uE8A5",
        SystemDiagnosticCategory.Service => "\uE90F",
        SystemDiagnosticCategory.Update => "\uE895",
        _ => "\uE7BA"
    };

    public string DisplayTitle => string.IsNullOrWhiteSpace(Subject) ? Title : $"{Title} · {Subject}";
    public string EventIdentityText => $"{ProviderName} · 事件 {EventId} · {LogName}";
    public string LastSeenText => $"最近发生 {LastSeen:yyyy-MM-dd HH:mm}";
    public string OccurrenceText => Count >= 3 ? $"频繁发生 · {Count:N0} 次" : $"{Count:N0} 次";
    public string FrequencyValueText => $"{Count:N0} 次";
    public string LatestOccurredAtText => LastSeen.ToString("yyyy-MM-dd HH:mm:ss");
    public string TimeRangeText => FirstSeen == LastSeen
        ? FirstSeen.ToString("yyyy-MM-dd HH:mm:ss")
        : $"{FirstSeen:yyyy-MM-dd HH:mm:ss} 至 {LastSeen:yyyy-MM-dd HH:mm:ss}";
    public string OccurrenceTimesText => string.Join(Environment.NewLine, Occurrences
        .OrderByDescending(time => time)
        .Take(20)
        .Select(time => time.ToString("yyyy-MM-dd HH:mm:ss")));
    public string SafeMessage => string.IsNullOrWhiteSpace(Message) ? "Windows 未提供可格式化的事件描述，可在事件查看器中查看原始记录。" : Message;
    public string FaultingModuleText => ExtractMessageField("错误模块名称", "故障模块名称", "Faulting module name") ?? "Windows 未提供";
    public string ExceptionCodeText => ExtractMessageField("异常代码", "Exception code") ?? "Windows 未提供";
    public IReadOnlyList<SystemDiagnosticRecommendationStep> RecommendationSteps => Recommendation
        .Split(new[] { '；', '。', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(step => !string.IsNullOrWhiteSpace(step))
        .Take(3)
        .Select((step, index) => new SystemDiagnosticRecommendationStep(index + 1, step))
        .ToArray();

    public string CopyAdviceText => string.Join(Environment.NewLine, new[]
    {
        $"诊断：{DisplayTitle}",
        $"级别：{SeverityText}",
        $"事件：{EventIdentityText}",
        $"发生次数：{Count:N0}",
        $"最近发生：{LatestOccurredAtText}",
        $"故障模块：{FaultingModuleText}",
        $"异常代码：{ExceptionCodeText}",
        string.Empty,
        $"影响判断：{Explanation}",
        $"建议处理：{Recommendation}"
    });

    public string CopyText => string.Join(Environment.NewLine, new[]
    {
        $"诊断：{DisplayTitle}",
        $"级别：{SeverityText}",
        $"分类：{CategoryText}",
        $"事件：{EventIdentityText}",
        $"记录 ID：{RecordId?.ToString() ?? "未知"}",
        $"发生次数：{Count:N0}",
        $"时间范围：{TimeRangeText}",
        string.Empty,
        $"说明：{Explanation}",
        $"建议：{Recommendation}",
        string.Empty,
        "Windows 事件描述：",
        SafeMessage
    });

    private string? ExtractMessageField(params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = Regex.Match(
                SafeMessage,
                $@"(?im)(?:^|[，,])\s*{Regex.Escape(label)}\s*[:：]\s*([^，,\r\n]+)",
                RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var value = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}
