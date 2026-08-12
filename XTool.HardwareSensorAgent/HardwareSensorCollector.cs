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

    public (IReadOnlyList<HardwareSensorReading> Sensors, IReadOnlyList<string> Warnings) Capture()
    {
        List<HardwareSensorReading> readings = [];
        List<string> warnings = [];

        foreach (IHardware hardware in _computer.Hardware)
        {
            CaptureHardware(hardware, null, readings, warnings);
            if (readings.Count >= HardwareSensorProtocol.MaximumSensorCount)
            {
                warnings.Add($"传感器数量超过 {HardwareSensorProtocol.MaximumSensorCount}，其余项目已忽略。");
                break;
            }
        }

        return (readings, warnings);
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
        ICollection<string> warnings)
    {
        try
        {
            hardware.Update();
        }
        catch (Exception ex)
        {
            warnings.Add($"{LimitText(hardware.Name)}：{LimitText(ex.Message)}");
        }

        string hardwareId = LimitText(hardware.Identifier.ToString());
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

            CaptureHardware(subHardware, hardwareId, readings, warnings);
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
}
