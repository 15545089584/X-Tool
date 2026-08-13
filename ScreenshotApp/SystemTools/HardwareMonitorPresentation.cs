using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Windows.Media;

namespace ScreenshotApp.SystemTools;

/// <summary>采集层与设备信息页之间的中立快照，不包含任何管理员代理实现细节。</summary>
public sealed record HardwareMonitorSnapshot(
    DateTimeOffset CapturedAt,
    HardwareMonitorConnectionState Status,
    string StatusDetail,
    string Source,
    IReadOnlyList<HardwareMonitorSensorValue> Sensors,
    int HardwareNodeCount = 0,
    int SamplingWarningCount = 0);

public enum HardwareMonitorConnectionState
{
    Disabled,
    Connecting,
    Connected,
    Unavailable,
    Error
}

public enum HardwareMonitorDeviceKind
{
    Cpu,
    Gpu,
    Memory,
    Mainboard,
    Storage,
    Fan,
    Other
}

public enum HardwareMonitorMetricKind
{
    Load,
    Clock,
    Voltage,
    Power,
    Temperature,
    MemoryUsed,
    MemoryTotal,
    FanSpeed
}

/// <summary>单个传感器的结构化值。采集端应提供稳定设备标识，界面不依赖英文名称猜测类型。</summary>
public sealed record HardwareMonitorSensorValue(
    string SensorId,
    string SensorName,
    string DeviceId,
    string DeviceName,
    HardwareMonitorDeviceKind DeviceKind,
    HardwareMonitorMetricKind MetricKind,
    double? Value,
    string Unit,
    bool IsSupported = true);

/// <summary>设备页实时监控的显示模型，同时维护最多 30 分钟的内存环形缓冲。</summary>
public sealed class HardwareMonitorPresenter : INotifyPropertyChanged
{
    private const int HistoryCapacity = 30 * 60;
    private readonly HardwareHistoryPoint[] _history = new HardwareHistoryPoint[HistoryCapacity];
    private int _historyStart;
    private int _historyCount;
    private string _statusText = "等待传感器代理";
    private string _statusDetail = "请先在设置中授权并启用硬件实时监控";
    private string _lastUpdatedText = "尚无数据";
    private string _diagnosticSummary = "代理未连接 · 尚无传感器诊断数据";
    private Brush _statusAccent = new SolidColorBrush(Color.FromRgb(122, 144, 166));

    public ObservableCollection<HardwareMetricDisplay> InstantMetrics { get; } = new();
    public ObservableCollection<HardwareTemperatureDisplay> Temperatures { get; } = new();
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string StatusDetail { get => _statusDetail; private set => SetField(ref _statusDetail, value); }
    public string LastUpdatedText { get => _lastUpdatedText; private set => SetField(ref _lastUpdatedText, value); }
    public string DiagnosticSummary { get => _diagnosticSummary; private set => SetField(ref _diagnosticSummary, value); }
    public Brush StatusAccent { get => _statusAccent; private set => SetField(ref _statusAccent, value); }
    public event PropertyChangedEventHandler? PropertyChanged;

    public HardwareMonitorPresenter() => ShowWaitingState();

    public void Apply(HardwareMonitorSnapshot snapshot, bool updatePresentation = true, bool recordHistory = true)
    {
        var sensors = snapshot.Sensors ?? Array.Empty<HardwareMonitorSensorValue>();
        if (recordHistory && snapshot.Status == HardwareMonitorConnectionState.Connected)
        {
            var cpuTemperature = FindPreferred(sensors, HardwareMonitorDeviceKind.Cpu, HardwareMonitorMetricKind.Temperature)?.Value;
            var gpuTemperature = FindPreferred(sensors, HardwareMonitorDeviceKind.Gpu, HardwareMonitorMetricKind.Temperature)?.Value;
            var cpuLoad = FindPreferred(sensors, HardwareMonitorDeviceKind.Cpu, HardwareMonitorMetricKind.Load)?.Value;
            AddHistory(new HardwareHistoryPoint(snapshot.CapturedAt, cpuTemperature, gpuTemperature, cpuLoad));
        }
        if (!updatePresentation) return;

        StatusText = snapshot.Status switch
        {
            HardwareMonitorConnectionState.Connected => "高级传感器监控中",
            HardwareMonitorConnectionState.Connecting => "正在连接传感器代理",
            HardwareMonitorConnectionState.Disabled => "硬件监控未启用",
            HardwareMonitorConnectionState.Unavailable => "传感器暂不可用",
            _ => "传感器连接异常"
        };
        StatusDetail = string.IsNullOrWhiteSpace(snapshot.StatusDetail)
            ? (string.IsNullOrWhiteSpace(snapshot.Source) ? "只读采集，不修改硬件设置" : $"数据来源：{snapshot.Source}")
            : snapshot.StatusDetail;
        LastUpdatedText = snapshot.Status == HardwareMonitorConnectionState.Connected
            ? $"更新于 {snapshot.CapturedAt.ToLocalTime():HH:mm:ss}"
            : "等待有效数据";
        DiagnosticSummary = BuildDiagnosticSummary(snapshot, sensors);
        StatusAccent = new SolidColorBrush(snapshot.Status switch
        {
            HardwareMonitorConnectionState.Connected => Color.FromRgb(22, 185, 155),
            HardwareMonitorConnectionState.Connecting => Color.FromRgb(77, 124, 254),
            HardwareMonitorConnectionState.Error => Color.FromRgb(229, 105, 118),
            _ => Color.FromRgb(122, 144, 166)
        });

        Replace(InstantMetrics, BuildInstantMetrics(sensors));
        Replace(Temperatures, BuildTemperatures(sensors, snapshot.Status));
    }

    public IReadOnlyList<HardwareHistoryPoint> GetHistory(TimeSpan range)
    {
        if (_historyCount == 0) return Array.Empty<HardwareHistoryPoint>();
        var cutoff = DateTimeOffset.Now - range;
        var result = new List<HardwareHistoryPoint>(_historyCount);
        for (var index = 0; index < _historyCount; index++)
        {
            var point = _history[(_historyStart + index) % HistoryCapacity];
            if (point.CapturedAt >= cutoff) result.Add(point);
        }
        return result;
    }

    private void ShowWaitingState()
    {
        Replace(InstantMetrics, BuildInstantMetrics(Array.Empty<HardwareMonitorSensorValue>()));
        Replace(Temperatures, BuildTemperatures(Array.Empty<HardwareMonitorSensorValue>(), HardwareMonitorConnectionState.Disabled));
    }

    private static IEnumerable<HardwareMetricDisplay> BuildInstantMetrics(IReadOnlyList<HardwareMonitorSensorValue> sensors)
    {
        yield return Metric("CPU 占用", "CPU", FindPreferred(sensors, HardwareMonitorDeviceKind.Cpu, HardwareMonitorMetricKind.Load), "%", "#4D7CFE");
        yield return Metric("CPU 频率", "CPU", FindPreferred(sensors, HardwareMonitorDeviceKind.Cpu, HardwareMonitorMetricKind.Clock), "MHz", "#4D7CFE");
        yield return Metric("CPU 电压", "CPU", FindPreferred(sensors, HardwareMonitorDeviceKind.Cpu, HardwareMonitorMetricKind.Voltage), "V", "#9B6AE8");
        yield return Metric("CPU 功耗", "CPU", FindPreferred(sensors, HardwareMonitorDeviceKind.Cpu, HardwareMonitorMetricKind.Power), "W", "#F0A14A");
        yield return Metric("GPU 占用", "GPU", FindPreferred(sensors, HardwareMonitorDeviceKind.Gpu, HardwareMonitorMetricKind.Load), "%", "#8B68DE");
        yield return Metric("GPU 功耗", "GPU", FindPreferred(sensors, HardwareMonitorDeviceKind.Gpu, HardwareMonitorMetricKind.Power), "W", "#8B68DE");
        yield return Metric("内存占用", "内存", FindPreferred(sensors, HardwareMonitorDeviceKind.Memory, HardwareMonitorMetricKind.Load), "%", "#16B99B");
        yield return Metric("风扇转速", "散热", FindPreferred(sensors, HardwareMonitorDeviceKind.Fan, HardwareMonitorMetricKind.FanSpeed), "RPM", "#38A8D8");
    }

    private static IEnumerable<HardwareTemperatureDisplay> BuildTemperatures(
        IReadOnlyList<HardwareMonitorSensorValue> sensors,
        HardwareMonitorConnectionState connectionState)
    {
        // 高级监控只展示五个稳定的用户关心类别，不把 Composite、Core #N 等底层原始项目直接铺满页面。
        var categories = new[]
        {
            ("CPU 温度", HardwareMonitorDeviceKind.Cpu, "#4D7CFE"),
            ("显卡温度", HardwareMonitorDeviceKind.Gpu, "#8B68DE"),
            ("内存温度", HardwareMonitorDeviceKind.Memory, "#16B99B"),
            ("主板温度", HardwareMonitorDeviceKind.Mainboard, "#F09345"),
            ("磁盘温度", HardwareMonitorDeviceKind.Storage, "#38A8D8")
        };

        foreach (var category in categories)
        {
            var categorySensors = sensors
                .Where(item => item.DeviceKind == category.Item2
                    && item.MetricKind == HardwareMonitorMetricKind.Temperature)
                .ToArray();
            var sensor = categorySensors
                .Where(item => item.IsSupported && item.Value is > 0 and < 130)
                .OrderByDescending(item => IsPreferredName(item.SensorName))
                .ThenByDescending(item => item.Value)
                .FirstOrDefault();
            if (sensor is null)
            {
                string reason = connectionState != HardwareMonitorConnectionState.Connected
                    ? "等待代理连接并返回有效快照"
                    : categorySensors.Length == 0
                        ? "代理已连接；底层未返回此类温度传感器"
                        : $"代理已发现 {categorySensors.Length} 个此类传感器，但当前读数无效";
                yield return HardwareTemperatureDisplay.Unsupported(category.Item1, reason, category.Item3);
                continue;
            }

            var value = Math.Clamp(sensor.Value!.Value, 0, 110);
            yield return new HardwareTemperatureDisplay(
                category.Item1,
                sensor.SensorName,
                $"{value:F0}°C",
                value,
                true,
                TemperatureAccent(value));
        }
    }

    private static string BuildDiagnosticSummary(
        HardwareMonitorSnapshot snapshot,
        IReadOnlyList<HardwareMonitorSensorValue> sensors)
    {
        if (snapshot.Status != HardwareMonitorConnectionState.Connected)
            return $"代理未连接 · {ConnectionReason(snapshot.Status)}";

        int Count(HardwareMonitorDeviceKind kind) => sensors.Count(item =>
            item.DeviceKind == kind && item.MetricKind == HardwareMonitorMetricKind.Temperature);
        int temperatureCount = sensors.Count(item => item.MetricKind == HardwareMonitorMetricKind.Temperature);
        return $"代理已连接 · 硬件节点 {snapshot.HardwareNodeCount} · 温度传感器 {temperatureCount} 个（CPU {Count(HardwareMonitorDeviceKind.Cpu)} / 显卡 {Count(HardwareMonitorDeviceKind.Gpu)} / 内存 {Count(HardwareMonitorDeviceKind.Memory)} / 主板 {Count(HardwareMonitorDeviceKind.Mainboard)} / 磁盘 {Count(HardwareMonitorDeviceKind.Storage)}） · 采样警告 {snapshot.SamplingWarningCount} 项";
    }

    private static string ConnectionReason(HardwareMonitorConnectionState state) => state switch
    {
        HardwareMonitorConnectionState.Disabled => "监控未启用",
        HardwareMonitorConnectionState.Connecting => "正在建立安全连接",
        HardwareMonitorConnectionState.Unavailable => "代理暂不可用",
        _ => "连接异常"
    };

    private static HardwareMetricDisplay Metric(string title, string scope, HardwareMonitorSensorValue? sensor, string fallbackUnit, string accent)
    {
        if (sensor is not { IsSupported: true, Value: not null })
            return new HardwareMetricDisplay(title, scope, "不受支持", "当前设备未提供此传感器", accent, false);
        var unit = string.IsNullOrWhiteSpace(sensor.Unit) ? fallbackUnit : sensor.Unit;
        var decimals = sensor.MetricKind == HardwareMonitorMetricKind.Voltage ? 3 : sensor.Value.Value >= 100 ? 0 : 1;
        return new HardwareMetricDisplay(title, scope, $"{sensor.Value.Value.ToString($"F{decimals}", CultureInfo.CurrentCulture)} {unit}", sensor.SensorName, accent, true);
    }

    private static HardwareMonitorSensorValue? FindPreferred(IEnumerable<HardwareMonitorSensorValue> sensors, HardwareMonitorDeviceKind deviceKind, HardwareMonitorMetricKind metricKind)
        => sensors.Where(item => item.DeviceKind == deviceKind && item.MetricKind == metricKind && item.IsSupported && item.Value.HasValue)
            .OrderByDescending(item => IsPreferredName(item.SensorName))
            .ThenByDescending(item => item.Value)
            .FirstOrDefault();

    private static bool IsPreferredName(string name)
        => name.Contains("Package", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Core", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Total", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Memory", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Composite", StringComparison.OrdinalIgnoreCase);

    private void AddHistory(HardwareHistoryPoint point)
    {
        if (_historyCount < HistoryCapacity)
        {
            _history[(_historyStart + _historyCount) % HistoryCapacity] = point;
            _historyCount++;
            return;
        }
        _history[_historyStart] = point;
        _historyStart = (_historyStart + 1) % HistoryCapacity;
    }

    private static string FriendlyDeviceName(HardwareMonitorDeviceKind kind) => kind switch
    {
        HardwareMonitorDeviceKind.Cpu => "处理器",
        HardwareMonitorDeviceKind.Gpu => "显卡",
        HardwareMonitorDeviceKind.Mainboard => "主板",
        HardwareMonitorDeviceKind.Storage => "存储设备",
        HardwareMonitorDeviceKind.Memory => "内存",
        _ => "硬件传感器"
    };

    private static int DeviceOrder(HardwareMonitorDeviceKind kind) => kind switch
    {
        HardwareMonitorDeviceKind.Cpu => 0,
        HardwareMonitorDeviceKind.Gpu => 1,
        HardwareMonitorDeviceKind.Mainboard => 2,
        HardwareMonitorDeviceKind.Storage => 3,
        HardwareMonitorDeviceKind.Memory => 4,
        _ => 5
    };

    private static string TemperatureAccent(double value) => value switch
    {
        >= 90 => "#E35E70",
        >= 75 => "#F09345",
        >= 55 => "#4D7CFE",
        _ => "#16B99B"
    };

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record HardwareMetricDisplay(string Title, string Scope, string ValueText, string DetailText, string Accent, bool IsSupported);
public sealed record HardwareTemperatureDisplay(string DeviceName, string SensorName, string ValueText, double Value, bool IsSupported, string Accent)
{
    public static HardwareTemperatureDisplay Unsupported(string deviceName, string sensorName, string accent = "#7890A6")
        => new(deviceName, sensorName, "不受支持", 0, false, accent);
}
public readonly record struct HardwareHistoryPoint(DateTimeOffset CapturedAt, double? CpuTemperature, double? GpuTemperature, double? CpuLoad);
