using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ScreenshotApp.SmartHome;

public sealed record SmartModeOption(string Value, string DisplayName);

public sealed class SmartDeviceViewModel : INotifyPropertyChanged
{
    private SmartDevice _device;
    private bool _isBusy;
    private string _operationMessage = string.Empty;
    private bool? _optimisticIsOn;
    private double? _optimisticValue;
    private string? _optimisticHvacMode;
    private string? _optimisticFanMode;
    private string? _optimisticSwingMode;
    private string? _optimisticPresetMode;

    public SmartDeviceViewModel(SmartDevice device)
    {
        _device = device;
    }

    public string Id => _device.Id;

    public string Name => _device.Name;

    public string AreaName => _device.AreaName;

    public string? AreaId => _device.AreaId;

    public string EntityId => Primary?.EntityId ?? string.Empty;

    public string Domain => Primary?.Domain ?? string.Empty;

    public bool IsAvailable => Primary?.Available == true;

    public bool CanInteract => IsAvailable && !IsBusy;

    public bool IsOn => _optimisticIsOn ?? GetActualIsOn();

    public bool IsCameraLike => ContainsAny(
        string.Join(' ', new[] { Primary?.GetText("icon"), Name, _device.Model }.Where(value => !string.IsNullOrWhiteSpace(value))),
        "camera", "摄像", "监控");

    public bool CanToggle => !IsCameraLike && (_device.Capabilities & SmartDeviceCapabilities.Toggle) != 0;

    public bool SupportsBrightness => !IsCameraLike && (_device.Capabilities & SmartDeviceCapabilities.Brightness) != 0;

    public bool SupportsTargetTemperature => (_device.Capabilities & SmartDeviceCapabilities.TargetTemperature) != 0;

    public bool SupportsCoverPosition => (_device.Capabilities & SmartDeviceCapabilities.CoverPosition) != 0;

    public bool SupportsFanPercentage => (_device.Capabilities & SmartDeviceCapabilities.FanPercentage) != 0;

    public bool SupportsHvacMode => (_device.Capabilities & SmartDeviceCapabilities.HvacMode) != 0;

    public bool SupportsFanMode => (_device.Capabilities & SmartDeviceCapabilities.FanMode) != 0;

    public bool SupportsSwingMode => (_device.Capabilities & SmartDeviceCapabilities.SwingMode) != 0;

    public bool SupportsPreset => (_device.Capabilities & SmartDeviceCapabilities.Preset) != 0;

    public bool IsClimate => Domain == "climate";

    public bool IsFan => Domain == "fan";

    public double BrightnessPercent => _optimisticValue ?? Math.Clamp((Primary?.GetNumber("brightness") ?? 0) / 2.55, 0, 100);

    public double CoverPosition => _optimisticValue ?? Math.Clamp(Primary?.GetNumber("current_position") ?? 0, 0, 100);

    public double FanPercentage => _optimisticValue ?? Math.Clamp(Primary?.GetNumber("percentage") ?? 0, 0, 100);

    public string TemperatureUnit => _device.TemperatureUnit;

    public double TargetTemperature => _optimisticValue ?? Primary?.GetNumber("temperature") ??
        (TemperatureUnit == "°F" ? 75 : 24);

    public double MinimumTemperature => Primary?.GetNumber("min_temp") ?? (TemperatureUnit == "°F" ? 60 : 16);

    public double MaximumTemperature => Primary?.GetNumber("max_temp") ?? (TemperatureUnit == "°F" ? 86 : 30);

    public double TemperatureStep => Math.Max(0.5, Primary?.GetNumber("target_temp_step") ?? 1);

    public string TargetTemperatureText => $"{TargetTemperature:0.#}{TemperatureUnit}";

    public IReadOnlyList<SmartModeOption> HvacModeOptions => BuildModeOptions(
        Primary?.GetTextList("hvac_modes"), TranslateHvacMode);

    public IReadOnlyList<SmartModeOption> FanModeOptions => BuildModeOptions(
        Primary?.GetTextList("fan_modes"), TranslateFanMode);

    public IReadOnlyList<SmartModeOption> SwingModeOptions => BuildModeOptions(
        Primary?.GetTextList("swing_modes"), TranslateSwingMode);

    public IReadOnlyList<SmartModeOption> PresetModeOptions => BuildModeOptions(
        Primary?.GetTextList("preset_modes"), value => value);

    public string CurrentHvacMode => _optimisticHvacMode ?? Primary?.State ?? string.Empty;

    public string CurrentFanMode => _optimisticFanMode ?? Primary?.GetText("fan_mode") ?? string.Empty;

    public string CurrentSwingMode => _optimisticSwingMode ?? Primary?.GetText("swing_mode") ?? string.Empty;

    public string CurrentPresetMode => _optimisticPresetMode ?? Primary?.GetText("preset_mode") ?? string.Empty;

    public string MainState => BuildMainState();

    public string SupportingSummary => BuildSupportingSummary();

    public string StatusText => !IsAvailable ? "离线" : IsBusy ? "正在同步" : "在线";

    public string ToggleLabel => IsOn ? "关闭" : "开启";

    public string CompactState => !IsAvailable ? "设备离线" : IsCameraLike ? "在线" : MainState;

    public string DeviceTypeLabel => IsCameraLike ? "摄像机" : Domain switch
    {
        "camera" => "摄像机",
        "light" => "灯光",
        "switch" => "智能开关",
        "climate" => "空调",
        "cover" => "窗帘",
        "fan" => "风扇",
        "binary_sensor" => "状态传感器",
        "sensor" => "传感器",
        _ => "智能设备"
    };

    public string IconGlyph => ResolveIconGlyph();

    public Brush AccentBrush => CreateBrush(Domain switch
    {
        "camera" => "#E5A522",
        "light" => "#E7A62A",
        "climate" => "#398CCB",
        "cover" => "#8264C8",
        "fan" => "#31A17C",
        "binary_sensor" => "#D56A78",
        "sensor" => "#3C93A6",
        _ => "#5B78C9"
    });

    public Brush AccentBackground => CreateBrush(Domain switch
    {
        "camera" => "#FFF3D8",
        "light" => "#FFF3D8",
        "climate" => "#E4F4FF",
        "cover" => "#F0E9FF",
        "fan" => "#E2F7EF",
        "binary_sensor" => "#FCE9ED",
        "sensor" => "#E3F6F7",
        _ => "#E8EEFF"
    });

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanInteract));
        }
    }

    public string OperationMessage
    {
        get => _operationMessage;
        set
        {
            if (_operationMessage == value) return;
            _operationMessage = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(SmartDevice device)
    {
        _device = device;
        _optimisticIsOn = null;
        _optimisticValue = null;
        _optimisticHvacMode = null;
        _optimisticFanMode = null;
        _optimisticSwingMode = null;
        _optimisticPresetMode = null;
        IsBusy = false;
        OperationMessage = string.Empty;
        NotifyAll();
    }

    public void ApplyOptimistic(SmartHomeControlRequest request)
    {
        _optimisticIsOn = request.Action switch
        {
            SmartHomeControlAction.TurnOn => true,
            SmartHomeControlAction.TurnOff => false,
            _ => _optimisticIsOn
        };
        if (request.NumericValue is not null) _optimisticValue = request.NumericValue;
        switch (request.Action)
        {
            case SmartHomeControlAction.SetHvacMode:
                _optimisticHvacMode = request.TextValue;
                _optimisticIsOn = !string.Equals(request.TextValue, "off", StringComparison.OrdinalIgnoreCase);
                break;
            case SmartHomeControlAction.SetFanMode:
                _optimisticFanMode = request.TextValue;
                break;
            case SmartHomeControlAction.SetSwingMode:
                _optimisticSwingMode = request.TextValue;
                break;
            case SmartHomeControlAction.SetPreset:
                _optimisticPresetMode = request.TextValue;
                break;
        }
        IsBusy = true;
        OperationMessage = "正在同步真实状态…";
        NotifyAll();
    }

    public void Rollback(string message)
    {
        _optimisticIsOn = null;
        _optimisticValue = null;
        _optimisticHvacMode = null;
        _optimisticFanMode = null;
        _optimisticSwingMode = null;
        _optimisticPresetMode = null;
        IsBusy = false;
        OperationMessage = message;
        NotifyAll();
    }

    private SmartEntity? Primary => _device.PrimaryEntity;

    private bool GetActualIsOn()
    {
        var state = Primary?.State;
        if (string.IsNullOrWhiteSpace(state)) return false;
        return Domain switch
        {
            "climate" => !string.Equals(state, "off", StringComparison.OrdinalIgnoreCase),
            "cover" => state is "open" or "opening",
            _ => string.Equals(state, "on", StringComparison.OrdinalIgnoreCase)
        };
    }

    private string BuildMainState()
    {
        if (!IsAvailable) return "设备不可用";
        if (IsCameraLike) return "在线";
        var entity = Primary;
        if (entity is null) return "暂无状态";
        return Domain switch
        {
            "camera" => entity.State.ToLowerInvariant() switch
            {
                "streaming" => "实时画面可用",
                "recording" => "正在录像",
                "idle" => "待机",
                _ => "在线"
            },
            "light" or "switch" => IsOn ? "已开启" : "已关闭",
            "climate" => BuildClimateState(entity),
            "cover" => $"{CoverPosition:0}% · {TranslateState(entity.State)}",
            "fan" => IsOn ? $"运行中 · {FanPercentage:0}%" : "已关闭",
            "sensor" => $"{entity.State}{entity.GetText("unit_of_measurement")}",
            "binary_sensor" => TranslateBinarySensor(entity),
            _ => TranslateState(entity.State)
        };
    }

    private string BuildSupportingSummary()
    {
        var values = _device.Entities
            .Where(entity => !ReferenceEquals(entity, Primary) && entity.Available && entity.Domain == "sensor")
            .Select(entity => $"{entity.Name} {entity.State}{entity.GetText("unit_of_measurement")}")
            .Take(2)
            .ToArray();
        if (values.Length > 0) return string.Join(" · ", values);
        if (!string.IsNullOrWhiteSpace(_device.Model)) return _device.Model;
        if (!string.IsNullOrWhiteSpace(_device.Manufacturer)) return _device.Manufacturer;
        return Domain switch
        {
            "camera" => "摄像与监控设备",
            "light" => "灯光设备",
            "switch" => "开关设备",
            "climate" => "空调与恒温设备",
            "cover" => "窗帘与遮罩设备",
            "fan" => "风扇与净化设备",
            "sensor" => "环境传感器",
            "binary_sensor" => "状态传感器",
            _ => "智能家居设备"
        };
    }

    private string BuildClimateState(SmartEntity entity)
    {
        var current = entity.GetNumber("current_temperature");
        var mode = TranslateHvacMode(CurrentHvacMode);
        return current is null ? mode : $"{current:0.#}{TemperatureUnit} · {mode}";
    }

    private static IReadOnlyList<SmartModeOption> BuildModeOptions(
        IReadOnlyList<string>? values,
        Func<string, string> translate)
    {
        if (values is null || values.Count == 0) return Array.Empty<SmartModeOption>();
        return values.Select(value => new SmartModeOption(value, translate(value))).ToArray();
    }

    private static string TranslateHvacMode(string value) => value.ToLowerInvariant() switch
    {
        "off" => "关闭",
        "heat" => "制热",
        "cool" => "制冷",
        "auto" or "heat_cool" => "自动",
        "dry" => "除湿",
        "fan_only" => "送风",
        _ => value
    };

    private static string TranslateFanMode(string value) => value.ToLowerInvariant() switch
    {
        "auto" => "自动",
        "low" => "低",
        "medium" or "mid" => "中",
        "high" => "高",
        _ => value
    };

    private static string TranslateSwingMode(string value) => value.ToLowerInvariant() switch
    {
        "off" => "关闭摆风",
        "on" => "开启摆风",
        "vertical" => "上下摆风",
        "horizontal" => "左右摆风",
        "both" => "全向摆风",
        _ => value
    };

    private static string TranslateBinarySensor(SmartEntity entity)
    {
        var on = string.Equals(entity.State, "on", StringComparison.OrdinalIgnoreCase);
        return entity.GetText("device_class") switch
        {
            "door" or "window" or "opening" => on ? "已打开" : "已关闭",
            "motion" or "occupancy" => on ? "检测到活动" : "无人活动",
            "smoke" => on ? "检测到烟雾" : "状态正常",
            "moisture" => on ? "检测到水浸" : "状态正常",
            _ => on ? "已触发" : "状态正常"
        };
    }

    private static string TranslateState(string state) => state.ToLowerInvariant() switch
    {
        "on" => "已开启",
        "off" => "已关闭",
        "open" => "已打开",
        "opening" => "正在打开",
        "closed" => "已关闭",
        "closing" => "正在关闭",
        "heat" => "制热",
        "cool" => "制冷",
        "auto" => "自动",
        "dry" => "除湿",
        "fan_only" => "送风",
        "idle" => "待机",
        _ => state
    };

    private void NotifyAll()
    {
        foreach (var property in new[]
                 {
                     nameof(Name), nameof(AreaName), nameof(AreaId), nameof(EntityId), nameof(Domain), nameof(IsAvailable),
                     nameof(CanInteract), nameof(IsCameraLike), nameof(IsOn), nameof(CanToggle), nameof(SupportsBrightness), nameof(SupportsTargetTemperature),
                     nameof(SupportsCoverPosition), nameof(SupportsFanPercentage), nameof(SupportsHvacMode),
                     nameof(SupportsFanMode), nameof(SupportsSwingMode), nameof(SupportsPreset), nameof(IsClimate),
                     nameof(IsFan), nameof(BrightnessPercent), nameof(CoverPosition), nameof(FanPercentage),
                     nameof(TemperatureUnit), nameof(TargetTemperature), nameof(TargetTemperatureText),
                     nameof(MinimumTemperature), nameof(MaximumTemperature), nameof(TemperatureStep),
                     nameof(HvacModeOptions), nameof(FanModeOptions), nameof(SwingModeOptions),
                     nameof(PresetModeOptions), nameof(CurrentHvacMode), nameof(CurrentFanMode),
                     nameof(CurrentSwingMode), nameof(CurrentPresetMode), nameof(MainState),
                     nameof(SupportingSummary), nameof(StatusText), nameof(CompactState), nameof(DeviceTypeLabel),
                     nameof(ToggleLabel), nameof(IconGlyph), nameof(AccentBrush), nameof(AccentBackground)
                 })
        {
            OnPropertyChanged(property);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static Brush CreateBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private string ResolveIconGlyph()
    {
        var hint = string.Join(' ', new[]
        {
            Primary?.GetText("icon"),
            Primary?.GetText("device_class"),
            Name,
            _device.Model,
            Domain
        }.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();

        // 优先尊重 Home Assistant 暴露的原生图标语义，再按设备名称与领域回退。
        if (ContainsAny(hint, "camera", "摄像", "监控")) return "\uE722";
        if (ContainsAny(hint, "air-conditioner", "thermostat", "空调")) return "\uE9CA";
        if (ContainsAny(hint, "ceiling-fan", "fan", "风扇")) return "\uE9A9";
        if (ContainsAny(hint, "lightbulb", "bulb", "lamp", "灯泡", "灯")) return "\uE706";
        if (ContainsAny(hint, "speaker", "音箱", "音响")) return "\uE8D6";
        if (ContainsAny(hint, "router", "路由")) return "\uE839";
        if (ContainsAny(hint, "temperature", "thermometer", "温度", "湿度")) return "\uE9CA";
        if (ContainsAny(hint, "plug", "outlet", "socket", "插座")) return "\uE7E8";
        if (ContainsAny(hint, "curtain", "blind", "cover", "窗帘")) return "\uE7E7";

        return Domain switch
        {
            "camera" => "\uE722",
            "light" => "\uE706",
            "switch" => "\uE7E8",
            "climate" => "\uE9CA",
            "cover" => "\uE7E7",
            "fan" => "\uE9A9",
            "binary_sensor" => "\uE81E",
            "sensor" => "\uE9D9",
            _ => "\uE772"
        };
    }

    private static bool ContainsAny(string source, params string[] values) =>
        values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));
}
