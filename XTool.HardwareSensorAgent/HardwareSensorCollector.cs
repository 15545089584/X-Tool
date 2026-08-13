using LibreHardwareMonitor.Hardware;
using ScreenshotApp.HardwareMonitoring;

namespace XTool.HardwareSensorAgent;

internal sealed class HardwareSensorCollector : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
        IsControllerEnabled = true,
        IsPsuEnabled = true
    };
    private bool _opened;

    public int HardwareCount => _computer.Hardware.Sum(CountHardwareTree);

    public void Open()
    {
        _computer.Open();
        _opened = true;
    }

    public (IReadOnlyList<HardwareSensorReading> Sensors, IReadOnlyList<string> Warnings, string EcSuperIoSummary) Capture()
    {
        List<HardwareSensorReading> readings = [];
        List<string> warnings = [];
        var diagnostics = new EcSuperIoDiagnostics();

        foreach (IHardware hardware in _computer.Hardware)
        {
            CaptureHardware(hardware, null, readings, warnings, diagnostics);
            if (readings.Count >= HardwareSensorProtocol.MaximumSensorCount)
            {
                warnings.Add($"传感器数量超过 {HardwareSensorProtocol.MaximumSensorCount}，其余项目已忽略。");
                break;
            }
        }

        return (readings, warnings, diagnostics.ToSummary());
    }

    public void Dispose()
    {
        if (_opened)
        {
            _computer.Close();
            _opened = false;
        }

    }

    private static void CaptureHardware(
        IHardware hardware,
        string? parentHardwareId,
        ICollection<HardwareSensorReading> readings,
        ICollection<string> warnings,
        EcSuperIoDiagnostics diagnostics)
    {
        try
        {
            hardware.Update();
        }
        catch (Exception ex)
        {
            // 跨权限边界只报告受控摘要，不转发可能包含路径或驱动细节的异常文本。
            warnings.Add($"{LimitText(hardware.Name)} 节点更新失败（{ex.GetType().Name}）。");
        }

        string hardwareId = LimitText(hardware.Identifier.ToString());
        diagnostics.Observe(hardware);
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (readings.Count >= HardwareSensorProtocol.MaximumSensorCount)
            {
                return;
            }

            if (sensor.Value is not float value || !float.IsFinite(value))
            {
                continue;
            }

            readings.Add(new HardwareSensorReading(
                hardwareId,
                hardware.HardwareType.ToString(),
                LimitText(hardware.Name),
                LimitNullableText(parentHardwareId),
                LimitText(sensor.Identifier.ToString()),
                sensor.SensorType.ToString(),
                LimitText(sensor.Name),
                GetUnit(sensor.SensorType),
                value,
                ToFiniteDouble(sensor.Min),
                ToFiniteDouble(sensor.Max)));
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            if (readings.Count >= HardwareSensorProtocol.MaximumSensorCount)
            {
                return;
            }

            CaptureHardware(subHardware, hardwareId, readings, warnings, diagnostics);
        }
    }

    private static int CountHardwareTree(IHardware hardware)
    {
        return 1 + hardware.SubHardware.Sum(CountHardwareTree);
    }

    private static double? ToFiniteDouble(float? value)
    {
        return value is float number && float.IsFinite(number) ? number : null;
    }

    private static string GetUnit(SensorType sensorType)
    {
        return sensorType switch
        {
            SensorType.Voltage => "V",
            SensorType.Current => "A",
            SensorType.Clock => "MHz",
            SensorType.Load => "%",
            SensorType.Temperature => "°C",
            SensorType.Fan => "RPM",
            SensorType.Flow => "L/h",
            SensorType.Control => "%",
            SensorType.Level => "%",
            SensorType.Power => "W",
            SensorType.Data => "GB",
            SensorType.SmallData => "MB",
            SensorType.Throughput => "B/s",
            SensorType.TimeSpan => "s",
            SensorType.Energy => "mWh",
            SensorType.Frequency => "Hz",
            _ => string.Empty
        };
    }

    private static string LimitText(string? value)
    {
        string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= HardwareSensorProtocol.MaximumStringLength
            ? normalized
            : normalized[..HardwareSensorProtocol.MaximumStringLength];
    }


    private static string? LimitNullableText(string? value) =>
        value is null ? null : LimitText(value);

    /// <summary>仅汇总 EC/SuperIO 温度元数据，用于确认是否存在未归类的真实读数。</summary>
    private sealed class EcSuperIoDiagnostics
    {
        private const int MaximumNames = 6;
        private int _nodes;
        private int _temperatureSensors;
        private int _realtimeValues;
        private int _thresholds;
        private int _invalidValues;
        private readonly List<string> _names = [];

        public void Observe(IHardware hardware)
        {
            if (hardware.HardwareType is not (HardwareType.SuperIO or HardwareType.Motherboard))
            {
                return;
            }

            _nodes++;
            foreach (ISensor sensor in hardware.Sensors.Where(item => item.SensorType == SensorType.Temperature))
            {
                _temperatureSensors++;
                string name = LimitText(sensor.Name);
                if (IsThreshold(name))
                {
                    _thresholds++;
                }
                else if (sensor.Value is float value && float.IsFinite(value) && value > 0 && value < 130)
                {
                    _realtimeValues++;
                }
                else
                {
                    _invalidValues++;
                }

                if (_names.Count < MaximumNames && !string.IsNullOrWhiteSpace(name) && !_names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    _names.Add(name);
                }
            }
        }

        public string ToSummary()
        {
            string names = _names.Count == 0 ? "无" : string.Join("、", _names);
            return $"EC/SuperIO 原始诊断：节点 {_nodes} 个，温度项 {_temperatureSensors} 个（当前值 {_realtimeValues} / 阈值 {_thresholds} / 无效 {_invalidValues}）；名称：{names}";
        }

        private static bool IsThreshold(string name) =>
            name.Contains("Critical", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Limit", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
            || name.Contains("TjMax", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Shutdown", StringComparison.OrdinalIgnoreCase);
    }
}
