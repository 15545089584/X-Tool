using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Xml.Linq;

namespace ScreenshotApp.SystemTools;

/// <summary>按需读取 Windows System 与 Application 日志；不订阅实时事件，也不修改日志或系统状态。</summary>
internal static class SystemDiagnosticService
{
    private static readonly string[] DiagnosticLogs = { "System", "Application" };

    internal static SystemDiagnosticSnapshot Scan(
        SystemDiagnosticQuery query,
        IProgress<SystemDiagnosticProgress>? progress,
        CancellationToken cancellationToken)
    {
        var accumulators = new Dictionary<string, DiagnosticGroupAccumulator>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<SystemDiagnosticReadFailure>();
        var timelineEvents = new List<SystemDiagnosticTimelineEvent>();
        var eventsRead = 0;
        var wasTruncated = false;
        var rangeEndUtc = DateTime.UtcNow;
        var rangeStartUtc = rangeEndUtc - query.TimeRange;
        var reliabilityAvailable = false;

        progress?.Report(new SystemDiagnosticProgress(
            "正在读取 Windows 可靠性记录",
            "识别应用停止工作、Windows 故障与硬件故障",
            0,
            DiagnosticLogs.Length + 1,
            eventsRead));

        try
        {
            reliabilityAvailable = ReadReliabilityRecords(
                accumulators,
                timelineEvents,
                rangeStartUtc,
                query.MaximumReliabilityRecords,
                ref eventsRead,
                ref wasTruncated,
                cancellationToken);
        }
        catch (ManagementException exception)
        {
            failures.Add(new SystemDiagnosticReadFailure("可靠性记录", $"读取失败：{exception.Message}"));
        }
        catch (UnauthorizedAccessException exception)
        {
            failures.Add(new SystemDiagnosticReadFailure("可靠性记录", $"权限不足：{exception.Message}"));
        }

        var timeSegments = BuildTimeSegments(rangeStartUtc, rangeEndUtc, query.TimeRange);

        for (var logIndex = 0; logIndex < DiagnosticLogs.Length; logIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logName = DiagnosticLogs[logIndex];
            progress?.Report(new SystemDiagnosticProgress(
                $"正在读取 {logName} 日志",
                "分时段补充关机、蓝屏、硬件、存储、驱动与服务记录",
                logIndex + 1,
                DiagnosticLogs.Length + 1,
                eventsRead));

            try
            {
                for (var segmentIndex = 0; segmentIndex < timeSegments.Count; segmentIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (eventsRead >= query.MaximumTotalEvents)
                    {
                        wasTruncated = true;
                        break;
                    }

                    var segment = timeSegments[segmentIndex];
                    var xpath = BuildEventLogXPath(segment.StartUtc, segment.EndUtc);
                    var queryDefinition = new EventLogQuery(logName, PathType.LogName, xpath)
                    {
                        ReverseDirection = true,
                        TolerateQueryErrors = true
                    };
                    using var reader = new EventLogReader(queryDefinition) { BatchSize = 64 };
                    using var cancellationRegistration = cancellationToken.Register(reader.CancelReading);
                    var segmentEventsRead = 0;
                    while (segmentEventsRead < query.MaximumEventsPerSegment && eventsRead < query.MaximumTotalEvents)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        EventRecord? record;
                        try
                        {
                            record = reader.ReadEvent(TimeSpan.FromSeconds(1));
                        }
                        catch (EventLogException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        if (record is null)
                        {
                            break;
                        }

                        using (record)
                        {
                            if (!reliabilityAvailable || !IsReliabilityCoveredEvent(record.ProviderName, record.Id))
                            {
                                AddRecord(accumulators, timelineEvents, logName, record);
                                eventsRead++;
                            }
                        }

                        segmentEventsRead++;
                    }

                    if (segmentEventsRead >= query.MaximumEventsPerSegment)
                    {
                        wasTruncated = true;
                    }

                    progress?.Report(new SystemDiagnosticProgress(
                        $"正在读取 {logName} 日志",
                        $"已覆盖 {segmentIndex + 1:N0} / {timeSegments.Count:N0} 个时间段",
                        logIndex + 1,
                        DiagnosticLogs.Length + 1,
                        eventsRead));
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                failures.Add(new SystemDiagnosticReadFailure(logName, $"权限不足：{exception.Message}"));
            }
            catch (EventLogNotFoundException exception)
            {
                failures.Add(new SystemDiagnosticReadFailure(logName, $"日志不可用：{exception.Message}"));
            }
            catch (EventLogException exception)
            {
                failures.Add(new SystemDiagnosticReadFailure(logName, $"读取失败：{exception.Message}"));
            }

            progress?.Report(new SystemDiagnosticProgress(
                $"已完成 {logName} 日志",
                "正在准备下一项",
                logIndex + 2,
                DiagnosticLogs.Length + 1,
                eventsRead));
            if (eventsRead >= query.MaximumTotalEvents)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var groups = accumulators.Values
            .Select(accumulator => accumulator.ToGroup())
            .OrderBy(group => group.SeverityPriority)
            .ThenByDescending(group => group.Count)
            .ThenByDescending(group => group.LastSeen)
            .Take(300)
            .ToArray();
        if (accumulators.Count > groups.Length)
        {
            wasTruncated = true;
        }

        return new SystemDiagnosticSnapshot(
            groups,
            failures,
            timelineEvents,
            query.TimeRange,
            eventsRead,
            wasTruncated,
            rangeEndUtc.ToLocalTime());
    }

    /// <summary>可靠性监视器的“关键事件”不是事件日志 Level 1；这里按可靠性影响重新分类。</summary>
    private static bool ReadReliabilityRecords(
        IDictionary<string, DiagnosticGroupAccumulator> accumulators,
        ICollection<SystemDiagnosticTimelineEvent> timelineEvents,
        DateTime rangeStartUtc,
        int maximumRecords,
        ref int eventsRead,
        ref bool wasTruncated,
        CancellationToken cancellationToken)
    {
        var since = ManagementDateTimeConverter.ToDmtfDateTime(rangeStartUtc.ToLocalTime());
        var wql = "SELECT SourceName,EventIdentifier,ProductName,Logfile,RecordNumber,Message,TimeGenerated " +
                  $"FROM Win32_ReliabilityRecords WHERE TimeGenerated >= '{since}'";
        var scope = new ManagementScope(@"\\.\root\cimv2");
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var records = searcher.Get();
        var recordsRead = 0;
        foreach (ManagementObject record in records)
        {
            using (record)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (recordsRead >= maximumRecords)
                {
                    wasTruncated = true;
                    break;
                }

                recordsRead++;
                var sourceName = GetManagementString(record, "SourceName");
                var eventId = GetManagementInt32(record, "EventIdentifier");
                var productName = GetManagementString(record, "ProductName");
                var message = GetManagementString(record, "Message");
                var rule = MatchReliabilityRecord(sourceName, eventId, productName, message);
                if (rule is null)
                {
                    continue;
                }

                var timeCreated = GetManagementDateTime(record, "TimeGenerated") ?? DateTime.Now;
                var logName = GetManagementString(record, "Logfile");
                if (string.IsNullOrWhiteSpace(logName))
                {
                    logName = rule.Category == SystemDiagnosticCategory.Application ? "Application" : "System";
                }

                var recordId = GetManagementInt64(record, "RecordNumber");
                var subject = CleanSubject(productName);
                var groupKey = string.Join("\u001F", logName, sourceName, eventId, subject);
                AddUnifiedRecord(
                    accumulators,
                    timelineEvents,
                    groupKey,
                    logName,
                    sourceName,
                    eventId,
                    recordId,
                    rule,
                    subject,
                    message,
                    string.Empty,
                    timeCreated);
                eventsRead++;
            }
        }

        return true;
    }

    private static SystemDiagnosticRule? MatchReliabilityRecord(
        string sourceName,
        int eventId,
        string productName,
        string message)
    {
        if (ProviderEquals(sourceName, "Application Error") && eventId == 1000)
        {
            return new SystemDiagnosticRule(
                SystemDiagnosticCategory.Application,
                SystemDiagnosticSeverity.Critical,
                "应用程序停止工作",
                "Windows 可靠性记录确认该应用发生崩溃。可靠性监视器将此类故障归为关键事件，即使对应事件日志的原始级别通常只是“错误”。",
                "检查应用版本、插件和故障模块；频繁出现时可复制诊断详情提供给应用开发者。");
        }

        if (ProviderEquals(sourceName, "Application Hang") && eventId == 1002)
        {
            return new SystemDiagnosticRule(
                SystemDiagnosticCategory.Application,
                SystemDiagnosticSeverity.Critical,
                "应用程序停止响应",
                "Windows 可靠性记录确认该应用曾长时间无响应，并将其归为影响可靠性的关键事件。",
                "检查当时的资源占用、应用插件和外部设备；若持续发生，更新或修复对应应用。");
        }

        if ((ProviderContains(sourceName, "WER-SystemErrorReporting") && eventId == 1001) ||
            ContainsAny(productName, message, "BlueScreen", "LiveKernelEvent"))
        {
            var hardwareFailure = ContainsAny(productName, message, "LiveKernelEvent", "WHEA");
            return new SystemDiagnosticRule(
                hardwareFailure ? SystemDiagnosticCategory.Hardware : SystemDiagnosticCategory.Shutdown,
                SystemDiagnosticSeverity.Critical,
                hardwareFailure ? "Windows 记录到硬件相关故障" : "Windows 记录到系统停止故障",
                hardwareFailure
                    ? "可靠性记录包含 LiveKernelEvent 或硬件错误报告。它表示 Windows 检测到影响稳定性的硬件或驱动故障，但不能单独证明硬件已经损坏。"
                    : "可靠性记录包含蓝屏或系统停止报告，属于 Windows 可靠性监视器中的关键事件。",
                "结合停止代码、转储文件以及同一时间的 WHEA、驱动和存储事件进一步排查。");
        }

        if (ProviderContains(sourceName, "Windows Error Reporting") && eventId == 1001)
        {
            return new SystemDiagnosticRule(
                SystemDiagnosticCategory.Application,
                SystemDiagnosticSeverity.Critical,
                "应用程序故障报告",
                "Windows 错误报告收录了影响应用稳定性的故障，可靠性监视器会将其作为关键事件呈现。",
                "结合产品名称、故障模块与异常代码判断是否需要更新或修复应用。");
        }

        if (ProviderContains(sourceName, "WindowsUpdateClient") && eventId == 20)
        {
            return new SystemDiagnosticRule(
                SystemDiagnosticCategory.Update,
                SystemDiagnosticSeverity.Warning,
                "Windows 更新安装失败",
                "可靠性记录显示某次 Windows 或应用更新没有成功完成。该记录属于可靠性警告，不代表后续重试仍然失败。",
                "打开 Windows 更新检查当前状态；若同一更新持续失败，再根据错误代码进一步排查。");
        }

        return null;
    }

    private static IReadOnlyList<DiagnosticTimeSegment> BuildTimeSegments(DateTime startUtc, DateTime endUtc, TimeSpan range)
    {
        var segmentCount = range.TotalDays <= 1.1
            ? 6
            : Math.Min(15, Math.Max(1, (int)Math.Ceiling(range.TotalDays)));
        var duration = TimeSpan.FromTicks(range.Ticks / segmentCount);
        var segments = new DiagnosticTimeSegment[segmentCount];
        for (var index = 0; index < segmentCount; index++)
        {
            var segmentStart = startUtc + TimeSpan.FromTicks(duration.Ticks * index);
            var segmentEnd = index == segmentCount - 1 ? endUtc : segmentStart + duration;
            segments[index] = new DiagnosticTimeSegment(segmentStart, segmentEnd);
        }

        return segments;
    }

    private static string BuildEventLogXPath(DateTime startUtc, DateTime endUtc)
    {
        var startText = startUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var endText = endUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        return $"*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[@SystemTime >= '{startText}' and @SystemTime < '{endText}']]]";
    }

    private static bool IsReliabilityCoveredEvent(string? providerName, int eventId)
    {
        var source = providerName ?? string.Empty;
        return (ProviderEquals(source, "Application Error") && eventId == 1000) ||
               (ProviderEquals(source, "Application Hang") && eventId == 1002) ||
               (ProviderContains(source, "Windows Error Reporting") && eventId == 1001) ||
               (ProviderContains(source, "WER-SystemErrorReporting") && eventId == 1001) ||
               (ProviderContains(source, "WindowsUpdateClient") && eventId == 20);
    }

    private static bool ContainsAny(string first, string second, params string[] values) =>
        values.Any(value => first.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                            second.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string GetManagementString(ManagementBaseObject record, string propertyName) =>
        record[propertyName]?.ToString()?.Trim() ?? string.Empty;

    private static int GetManagementInt32(ManagementBaseObject record, string propertyName)
    {
        try { return Convert.ToInt32(record[propertyName]); }
        catch { return 0; }
    }

    private static long? GetManagementInt64(ManagementBaseObject record, string propertyName)
    {
        try { return Convert.ToInt64(record[propertyName]); }
        catch { return null; }
    }

    private static DateTime? GetManagementDateTime(ManagementBaseObject record, string propertyName)
    {
        var value = GetManagementString(record, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try { return ManagementDateTimeConverter.ToDateTime(value); }
        catch { return null; }
    }

    private static bool ProviderEquals(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static bool ProviderContains(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static void AddRecord(
        IDictionary<string, DiagnosticGroupAccumulator> accumulators,
        ICollection<SystemDiagnosticTimelineEvent> timelineEvents,
        string logName,
        EventRecord record)
    {
        var providerName = string.IsNullOrWhiteSpace(record.ProviderName) ? "未知事件来源" : record.ProviderName;
        var eventId = record.Id;
        var severity = record.Level switch
        {
            1 => SystemDiagnosticSeverity.Critical,
            2 => SystemDiagnosticSeverity.Error,
            _ => SystemDiagnosticSeverity.Warning
        };
        var rule = SystemDiagnosticRules.Match(logName, providerName, eventId, severity);
        var timeCreated = record.TimeCreated?.ToLocalTime() ?? DateTime.Now;
        var eventXml = TryGetEventXml(record);
        var eventData = ExtractEventData(eventXml);
        var subject = ExtractSubject(rule.Category, eventData);
        var groupKey = string.Join("\u001F", logName, providerName, eventId, subject);
        AddUnifiedRecord(
            accumulators,
            timelineEvents,
            groupKey,
            logName,
            providerName,
            eventId,
            record.RecordId,
            rule,
            subject,
            TryFormatDescription(record),
            eventXml,
            timeCreated);
    }

    /// <summary>所有来源必须在这里写入最终等级，确保时间格、筛选和结果卡片完全一致。</summary>
    private static void AddUnifiedRecord(
        IDictionary<string, DiagnosticGroupAccumulator> accumulators,
        ICollection<SystemDiagnosticTimelineEvent> timelineEvents,
        string groupKey,
        string logName,
        string providerName,
        int eventId,
        long? recordId,
        SystemDiagnosticRule rule,
        string subject,
        string message,
        string eventXml,
        DateTime timeCreated)
    {
        timelineEvents.Add(new SystemDiagnosticTimelineEvent(timeCreated, rule.Severity, rule.Category, groupKey));
        if (!accumulators.TryGetValue(groupKey, out var accumulator))
        {
            accumulator = new DiagnosticGroupAccumulator(
                groupKey,
                logName,
                providerName,
                eventId,
                recordId,
                rule,
                subject,
                message,
                eventXml,
                timeCreated);
            accumulators[groupKey] = accumulator;
        }
        else
        {
            accumulator.AddOccurrence(timeCreated);
        }
    }

    private static string TryFormatDescription(EventRecord record)
    {
        try
        {
            return record.FormatDescription()?.Trim() ?? string.Empty;
        }
        catch (EventLogException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string TryGetEventXml(EventRecord record)
    {
        try
        {
            return record.ToXml();
        }
        catch (EventLogException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static IReadOnlyDictionary<string, string> ExtractEventData(string eventXml)
    {
        if (string.IsNullOrWhiteSpace(eventXml))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var document = XDocument.Parse(eventXml, LoadOptions.None);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unnamedIndex = 0;
            foreach (var element in document.Descendants().Where(element => element.Name.LocalName == "Data"))
            {
                var value = element.Value.Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var name = element.Attribute("Name")?.Value;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"param{++unnamedIndex}";
                }

                values.TryAdd(name, value);
            }

            return values;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string ExtractSubject(SystemDiagnosticCategory category, IReadOnlyDictionary<string, string> data)
    {
        string First(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return CleanSubject(value);
                }
            }

            return string.Empty;
        }

        return category switch
        {
            SystemDiagnosticCategory.Application => First("AppName", "FaultingApplicationName", "ApplicationName", "param1"),
            SystemDiagnosticCategory.Service => First("ServiceName", "param1"),
            SystemDiagnosticCategory.Storage => First("DeviceName", "Device", "VolumeName", "DriveName"),
            SystemDiagnosticCategory.Driver => First("DeviceName", "DriverName", "DeviceId"),
            _ => string.Empty
        };
    }

    private static string CleanSubject(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (singleLine.Contains('\\') || singleLine.Contains('/'))
        {
            try
            {
                var fileName = Path.GetFileName(singleLine);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    singleLine = fileName;
                }
            }
            catch
            {
                // 第三方事件可能包含并非文件系统路径的斜杠文本，保留原值即可。
            }
        }

        return singleLine.Length <= 80 ? singleLine : singleLine[..77] + "…";
    }

    private sealed record DiagnosticTimeSegment(DateTime StartUtc, DateTime EndUtc);

    private sealed class DiagnosticGroupAccumulator
    {
        private readonly List<DateTime> _occurrences = new();

        internal DiagnosticGroupAccumulator(
            string groupKey,
            string logName,
            string providerName,
            int eventId,
            long? recordId,
            SystemDiagnosticRule rule,
            string subject,
            string message,
            string eventXml,
            DateTime timeCreated)
        {
            GroupKey = groupKey;
            LogName = logName;
            ProviderName = providerName;
            EventId = eventId;
            RecordId = recordId;
            Rule = rule;
            Subject = subject;
            Message = message;
            EventXml = eventXml;
            FirstSeen = timeCreated;
            LastSeen = timeCreated;
            Count = 1;
            _occurrences.Add(timeCreated);
        }

        private string GroupKey { get; }
        private string LogName { get; }
        private string ProviderName { get; }
        private int EventId { get; }
        private long? RecordId { get; }
        private SystemDiagnosticRule Rule { get; }
        private string Subject { get; }
        private string Message { get; }
        private string EventXml { get; }
        private DateTime FirstSeen { get; set; }
        private DateTime LastSeen { get; set; }
        private int Count { get; set; }

        internal void AddOccurrence(DateTime timeCreated)
        {
            FirstSeen = timeCreated < FirstSeen ? timeCreated : FirstSeen;
            LastSeen = timeCreated > LastSeen ? timeCreated : LastSeen;
            Count++;
            if (_occurrences.Count < 20)
            {
                _occurrences.Add(timeCreated);
            }
        }

        internal SystemDiagnosticGroup ToGroup() => new(
            GroupKey,
            LogName,
            ProviderName,
            EventId,
            RecordId,
            Rule.Severity,
            Rule.Category,
            Rule.Title,
            Subject,
            Rule.Explanation,
            Rule.Recommendation,
            Message,
            EventXml,
            FirstSeen,
            LastSeen,
            Count,
            _occurrences.ToArray());
    }
}
