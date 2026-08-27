using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenshotApp.SmartHome;

/// <summary>智能家居模块与 Home Assistant 的连接状态。</summary>
public enum SmartHomeConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    AuthenticationFailed,
    ServerUnavailable
}

/// <summary>设备卡片可动态组合的通用控制能力。</summary>
[Flags]
public enum SmartDeviceCapabilities
{
    None = 0,
    Toggle = 1 << 0,
    Brightness = 1 << 1,
    ColorTemperature = 1 << 2,
    Color = 1 << 3,
    TargetTemperature = 1 << 4,
    CoverPosition = 1 << 5,
    FanPercentage = 1 << 6,
    Preset = 1 << 7,
    HvacMode = 1 << 8,
    FanMode = 1 << 9,
    SwingMode = 1 << 10
}

public enum SmartHomeControlAction
{
    TurnOn,
    TurnOff,
    SetBrightness,
    SetTargetTemperature,
    OpenCover,
    CloseCover,
    StopCover,
    SetCoverPosition,
    SetFanPercentage,
    SetPreset,
    SetHvacMode,
    SetFanMode,
    SetSwingMode
}

/// <summary>由设备卡片发出的抽象控制请求，UI 不直接拼接 Home Assistant 接口。</summary>
public sealed record SmartHomeControlRequest(
    string EntityId,
    SmartHomeControlAction Action,
    double? NumericValue = null,
    string? TextValue = null);

public sealed record SmartArea(
    string Id,
    string Name);

public sealed record SmartEntity
{
    public required string EntityId { get; init; }

    public required string Name { get; init; }

    public required string State { get; init; }

    public string? DeviceId { get; init; }

    public string? AreaId { get; init; }

    public Dictionary<string, JsonElement> Attributes { get; init; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public string Domain
    {
        get
        {
            var separator = EntityId.IndexOf('.');
            return separator <= 0 ? string.Empty : EntityId[..separator];
        }
    }

    [JsonIgnore]
    public bool Available => !string.Equals(State, "unavailable", StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(State, "unknown", StringComparison.OrdinalIgnoreCase);

    public bool HasAttribute(string name) => Attributes.ContainsKey(name);

    public double? GetNumber(string name)
    {
        if (!Attributes.TryGetValue(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    public string? GetText(string name)
    {
        if (!Attributes.TryGetValue(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    public IReadOnlyList<string> GetTextList(string name)
    {
        if (!Attributes.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }
}

public sealed record SmartDevice
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Manufacturer { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string? AreaId { get; init; }

    public string AreaName { get; init; } = "未分配房间";

    public string TemperatureUnit { get; init; } = "°C";

    public List<SmartEntity> Entities { get; init; } = [];

    [JsonIgnore]
    public SmartEntity? PrimaryEntity => Entities
        .OrderBy(GetEntityPriority)
        .ThenBy(entity => entity.EntityId, StringComparer.Ordinal)
        .FirstOrDefault();

    [JsonIgnore]
    public SmartDeviceCapabilities Capabilities => DetermineCapabilities(PrimaryEntity);

    private static int GetDomainPriority(string domain) => domain switch
    {
        // 设备可能同时暴露指示灯或辅助开关；专用控制实体必须优先。
        "camera" => 0,
        "climate" => 1,
        "cover" => 2,
        "fan" => 3,
        "light" => 4,
        "switch" => 5,
        "binary_sensor" => 6,
        "sensor" => 7,
        _ => 100
    };

    private static int GetEntityPriority(SmartEntity entity)
    {
        var priority = GetDomainPriority(entity.Domain) * 10;
        if (entity.Domain != "sensor") return priority;
        var deviceClass = entity.GetText("device_class");
        var unit = entity.GetText("unit_of_measurement");
        if (deviceClass == "temperature" || unit is "℃" or "°C") return priority;
        if (deviceClass == "humidity" || unit == "%") return priority + 1;
        return priority + 2;
    }

    private static SmartDeviceCapabilities DetermineCapabilities(SmartEntity? entity)
    {
        if (entity is null) return SmartDeviceCapabilities.None;

        var capabilities = entity.Domain switch
        {
            "light" or "switch" or "climate" or "fan" => SmartDeviceCapabilities.Toggle,
            _ => SmartDeviceCapabilities.None
        };

        if (entity.Domain == "light")
        {
            var colorModes = entity.GetTextList("supported_color_modes");
            if (entity.HasAttribute("brightness") || colorModes.Any(mode => mode is "brightness" or "color_temp" or "hs" or "xy" or "rgb" or "rgbw" or "rgbww"))
            {
                capabilities |= SmartDeviceCapabilities.Brightness;
            }
            if (entity.HasAttribute("color_temp_kelvin") || colorModes.Contains("color_temp"))
            {
                capabilities |= SmartDeviceCapabilities.ColorTemperature;
            }
            if (colorModes.Any(mode => mode is "hs" or "xy" or "rgb" or "rgbw" or "rgbww"))
            {
                capabilities |= SmartDeviceCapabilities.Color;
            }
        }

        if (entity.Domain == "climate" && entity.HasAttribute("temperature"))
        {
            capabilities |= SmartDeviceCapabilities.TargetTemperature;
        }

        if (entity.Domain == "climate" && entity.GetTextList("hvac_modes").Count > 0)
        {
            capabilities |= SmartDeviceCapabilities.HvacMode;
        }

        if (entity.GetTextList("fan_modes").Count > 0)
        {
            capabilities |= SmartDeviceCapabilities.FanMode;
        }

        if (entity.GetTextList("swing_modes").Count > 0)
        {
            capabilities |= SmartDeviceCapabilities.SwingMode;
        }

        if (entity.Domain == "cover")
        {
            capabilities |= SmartDeviceCapabilities.CoverPosition;
        }

        if (entity.Domain == "fan" &&
            (entity.HasAttribute("percentage") || entity.HasAttribute("percentage_step")))
        {
            capabilities |= SmartDeviceCapabilities.FanPercentage;
        }

        if (entity.GetTextList("preset_modes").Count > 0)
        {
            capabilities |= SmartDeviceCapabilities.Preset;
        }

        return capabilities;
    }
}

public sealed record SmartHomeSnapshot
{
    public string HomeName { get; init; } = "Home Assistant";

    public List<SmartArea> Areas { get; init; } = [];

    public List<SmartDevice> Devices { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}
