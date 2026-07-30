using System.Diagnostics.Eventing.Reader;
using System.IO;
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
        var rangeMilliseconds = Math.Max(1L, (long)query.TimeRange.TotalMilliseconds);
        var xpath = $"*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= {rangeMilliseconds}]]]";

        for (var logIndex = 0; logIndex < DiagnosticLogs.Length; logIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logName = DiagnosticLogs[logIndex];
            progress?.Report(new SystemDiagnosticProgress(
                $"正在读取 {logName} 日志",
                "只读取关键、错误和警告记录",
                logIndex,
                DiagnosticLogs.Length,
                eventsRead));

            try
            {
                var queryDefinition = new EventLogQuery(logName, PathType.LogName, xpath)
                {
                    ReverseDirection = true,
                    TolerateQueryErrors = true
                };
                using var reader = new EventLogReader(queryDefinition) { BatchSize = 64 };
                using var cancellationRegistration = cancellationToken.Register(reader.CancelReading);
                var logEventsRead = 0;
                while (logEventsRead < query.MaximumEventsPerLog && eventsRead < query.MaximumTotalEvents)
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
                        AddRecord(accumulators, timelineEvents, logName, record);
                    }

                    logEventsRead++;
                    eventsRead++;
                    if (eventsRead % 50 == 0)
                    {
                        progress?.Report(new SystemDiagnosticProgress(
                            $"正在读取 {logName} 日志",
                            "正在归纳重复记录",
                            logIndex,
                            DiagnosticLogs.Length,
                            eventsRead));
                    }
                }

                if (logEventsRead >= query.MaximumEventsPerLog || eventsRead >= query.MaximumTotalEvents)
                {
                    wasTruncated = true;
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
                logIndex + 1,
                DiagnosticLogs.Length,
                eventsRead));
            if (eventsRead >= query.MaximumTotalEvents)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var groups = accumulators.Values
            .Select(accumulator => accumulator.ToGroup())
            .OrderBy(group => group.Severity)
            .ThenByDescending(group => group.Count)
            .ThenByDescending(group => group.LastSeen)
            .Take(300)
            .ToArray();
        if (accumulators.Count > groups.Length)
        {
            wasTruncated = true;
        }

        return new SystemDiagnosticSnapshot(groups, failures, timelineEvents, query.TimeRange, eventsRead, wasTruncated, DateTime.Now);
    }

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
        timelineEvents.Add(new SystemDiagnosticTimelineEvent(timeCreated, severity, rule.Category, groupKey));
        if (!accumulators.TryGetValue(groupKey, out var accumulator))
        {
            accumulator = new DiagnosticGroupAccumulator(
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
