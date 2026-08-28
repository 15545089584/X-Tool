using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace ScreenshotApp.SmartHome;

public sealed record SmartModeOption(string Value, string DisplayName, string IconKind = "");

public sealed record SmartFanLevelOption(int Level, string Label, double Percent, bool IsCurrent);

public sealed record SmartFeatureOption(
    string EntityId,
    string DisplayName,
    string IconGlyph,
    bool IsOn,
    bool IsAvailable);

public sealed record SmartSelectControl(
    string EntityId,
    string DisplayName,
    string SelectedValue,
    IReadOnlyList<string> Options,
    bool IsAvailable);

public sealed class SmartDeviceViewModel : INotifyPropertyChanged
{
    private SmartDevice _device;
    private bool _isBusy;
    private string _operationMessage = string.Empty;
    private bool? _optimisticIsOn;
    private double? _optimisticValue;
    private double? _optimisticColorTemperatureKelvin;
    private string? _optimisticHvacMode;
    private string? _optimisticFanMode;
    private string? _optimisticSwingMode;
    private string? _optimisticPresetMode;
    private readonly Dictionary<string, bool> _optimisticEntityStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _optimisticEntitySelections = new(StringComparer.Ordinal);
    private SmartDeviceInsights _insights = new();
    private bool _isInsightsLoading;

    public SmartDeviceViewModel(SmartDevice device)
    {
        _device = device;
    }

    public string Id => _device.Id;

    internal SmartDevice SourceDevice => _device;

    public string Name => _device.Name;

    public string AreaName => _device.AreaName;

    public string? AreaId => _device.AreaId;

    public string EntityId => Primary?.EntityId ?? string.Empty;

    public string Domain => Primary?.Domain ?? string.Empty;

    public bool IsAvailable => _device.IsAvailable;

    public bool CanInteract => IsAvailable && !IsBusy;

    public bool IsOn => _optimisticIsOn ?? GetActualIsOn();

    public bool IsWorking
    {
        get
        {
            if (!IsAvailable) return false;
            if (IsSpeakerLike) return true;
            var state = Primary?.State?.ToLowerInvariant() ?? string.Empty;
            return Domain switch
            {
                "camera" => state is "streaming" or "recording",
                "climate" => state is not ("off" or "idle" or "unavailable" or "unknown" or ""),
                "cover" => state is "opening" or "closing",
                "light" or "switch" or "fan" => IsOn,
                "media_player" => state is "playing",
                "sensor" => state is "playing" or "播放中",
                _ => false
            };
        }
    }

    public bool IsCameraLike => ContainsAny(
        string.Join(' ', new[] { Primary?.GetText("icon"), Name, _device.Model }.Where(value => !string.IsNullOrWhiteSpace(value))),
        "camera", "摄像", "监控");

    /// <summary>音箱类设备没有独立的电源开关，通电即可控即可视为在线运行，与是否在播放无关。</summary>
    public bool IsSpeakerLike => ContainsAny(
        string.Join(' ', new[] { Primary?.GetText("icon"), Name, _device.Model }.Where(value => !string.IsNullOrWhiteSpace(value))),
        "speaker", "音箱", "音响", "小爱");

    public bool CanRename => !_device.Id.StartsWith("entity:", StringComparison.Ordinal);

    public bool CanToggle => !IsCameraLike && (_device.Capabilities & SmartDeviceCapabilities.Toggle) != 0;

    public bool SupportsBrightness => !IsCameraLike && (_device.Capabilities & SmartDeviceCapabilities.Brightness) != 0;

    public bool SupportsColorTemperature => !IsCameraLike && (_device.Capabilities & SmartDeviceCapabilities.ColorTemperature) != 0;

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

    public double ColorTemperaturePercent
    {
        get
        {
            var minimum = MinimumColorTemperatureKelvin;
            var maximum = MaximumColorTemperatureKelvin;
            var current = Math.Clamp(_optimisticColorTemperatureKelvin ?? CurrentColorTemperatureKelvin, minimum, maximum);
            return maximum <= minimum ? 50 : (current - minimum) / (maximum - minimum) * 100;
        }
    }

    public double CoverPosition => _optimisticValue ?? Math.Clamp(Primary?.GetNumber("current_position") ?? 0, 0, 100);

    public double FanPercentage => _optimisticValue ?? Math.Clamp(Primary?.GetNumber("percentage") ?? 0, 0, 100);

    /// <summary>风机档位数量：优先取实体上报的 percentage_step，缺失时按三挡处理。</summary>
    public int FanLevelCount
    {
        get
        {
            var step = Primary?.GetNumber("percentage_step");
            return step is > 0.5 ? Math.Clamp((int)Math.Round(100d / step.Value), 2, 6) : 3;
        }
    }

    public IReadOnlyList<SmartFanLevelOption> FanLevels
    {
        get
        {
            string[] labels = ["一挡", "二挡", "三挡", "四挡", "五挡", "六挡"];
            var count = FanLevelCount;
            // 档位百分比取整数（33/67/100）：部分集成按整型档位映射，浮点值（66.67）会被拒绝。
            return Enumerable.Range(1, count)
                .Select(level => new SmartFanLevelOption(
                    level,
                    labels[level - 1],
                    Math.Round(level * 100d / count),
                    level == CurrentFanLevel))
                .ToArray();
        }
    }

    public int CurrentFanLevel => Math.Clamp(
        (int)Math.Round(FanPercentage / (100d / FanLevelCount), MidpointRounding.AwayFromZero), 0, FanLevelCount);

    public string TemperatureUnit => "°C";

    public double TargetTemperature => Math.Round(
        ToCelsius(_optimisticValue ?? Primary?.GetNumber("temperature") ?? 24) * 2,
        MidpointRounding.AwayFromZero) / 2;

    public double MinimumTemperature => Math.Ceiling(ToCelsius(Primary?.GetNumber("min_temp") ?? 16) * 2) / 2;

    public double MaximumTemperature => Math.Floor(ToCelsius(Primary?.GetNumber("max_temp") ?? 30) * 2) / 2;

    public double TemperatureStep => 0.5;

    public string TargetTemperatureText => $"{TargetTemperature:0.#}{TemperatureUnit}";

    public string CurrentTemperatureText => Primary?.GetNumber("current_temperature") is { } value
        ? $"{ToCelsius(value):0.#}°C"
        : "--";

    public string LiveTemperatureText => ResolveLiveTemperatureText();

    public string LiveHumidityText => ResolveLiveHumidityText();

    public string? DeviceArtworkUri => ResolveDeviceArtworkUri();

    public bool HasDeviceArtwork => DeviceArtworkUri is not null;

    public bool IsLightArtwork => DeviceArtworkUri?.EndsWith("/bulb.png", StringComparison.Ordinal) == true;

    public bool IsClimateArtwork => DeviceArtworkUri?.EndsWith("/air-conditioner.png", StringComparison.Ordinal) == true;

    public bool IsThermoHygrometerArtwork => DeviceArtworkUri?.EndsWith("/thermo-hygrometer.png", StringComparison.Ordinal) == true;

    public double ArtworkOpacity => IsLightArtwork
        ? IsOn ? 0.58 + BrightnessPercent / 100d * 0.42 : 0.38
        : IsAvailable ? 1 : 0.62;

    public double ArtworkScale => IsLightArtwork
        ? IsOn ? 0.88 + BrightnessPercent / 100d * 0.12 : 0.84
        : 1;

    public IReadOnlyList<SmartFeatureOption> FeatureControls => BuildFeatureControls();

    public IReadOnlyList<SmartSelectControl> AuxiliarySelects => BuildAuxiliarySelects();

    public bool HasFeatureControls => FeatureControls.Count > 0;

    public bool HasAuxiliarySelects => AuxiliarySelects.Count > 0;

    public IReadOnlyList<SmartHistoryPoint> TemperaturePoints => _insights.TemperaturePoints;

    public bool HasTemperatureHistory => TemperaturePoints.Count > 1;

    public bool HasEnergyData => _insights.TodayEnergyKwh is not null || _insights.MonthEnergyKwh is not null;

    public string IndoorTemperatureText => _insights.IndoorTemperatureCelsius is { } value
        ? $"{value:0.#}°C"
        : ResolveLiveTemperatureText();

    public string IndoorHumidityText => _insights.IndoorHumidityPercent is { } value ? $"{value:0.#}%" : ResolveLiveHumidityText();

    // 环境指标只在设备真的上报温湿度时显示，避免音箱等设备借用空调面板出现占位文案。
    public bool HasIndoorTemperature => Primary?.GetNumber("current_temperature") is not null ||
        _device.Entities.Any(entity => entity.Domain == "sensor" &&
            (string.Equals(entity.GetText("device_class"), "temperature", StringComparison.OrdinalIgnoreCase) ||
             entity.GetText("unit_of_measurement") is "℃" or "°C" or "°F"));

    public bool HasIndoorHumidity => Primary?.HasAttribute("humidity") == true ||
        _device.Entities.Any(entity => entity.Domain == "sensor" &&
            (string.Equals(entity.GetText("device_class"), "humidity", StringComparison.OrdinalIgnoreCase) ||
             entity.GetText("unit_of_measurement") == "%"));

    public string TodayEnergyText => _insights.TodayEnergyKwh is { } value ? $"{value:0.##} 度" : "暂无记录";

    public string MonthEnergyText => _insights.MonthEnergyKwh is { } value ? $"{value:0.##} 度" : "暂无记录";

    public bool IsNotClimate => !IsClimate;

    public string EnergyMonthLabel => _insights.EnergyMonthLabel;

    public bool HasDailyEnergy => _insights.DailyEnergy.Count > 0;

    public bool HasMonthlyEnergy => _insights.MonthlyEnergy.Count > 0;

    public IReadOnlyList<SmartMonthEnergyPoint> MonthlyEnergyRows => _insights.MonthlyEnergy;

    /// <summary>用电月历单元格：周日起头、前置占位，每日显示当天用电（度）。</summary>
    public IReadOnlyList<SmartEnergyCalendarCell> EnergyCalendarCells
    {
        get
        {
            var label = _insights.EnergyMonthLabel;
            var separator = label.IndexOf('/');
            if (separator <= 0 ||
                !int.TryParse(label[..separator], out var year) ||
                !int.TryParse(label[(separator + 1)..], out var month))
            {
                return Array.Empty<SmartEnergyCalendarCell>();
            }

            var offset = DateTimeOffset.Now.Offset;
            var firstDay = new DateTimeOffset(year, month, 1, 0, 0, 0, offset);
            var today = DateTimeOffset.Now.Date;
            var usageByDay = _insights.DailyEnergy.ToDictionary(point => point.Date.Date, point => point.Kwh);
            var cells = new List<SmartEnergyCalendarCell>();
            for (var lead = 0; lead < ((int)firstDay.DayOfWeek + 7) % 7; lead++)
            {
                cells.Add(new SmartEnergyCalendarCell(string.Empty, string.Empty, false, false));
            }

            foreach (var day in Enumerable.Range(1, DateTime.DaysInMonth(year, month)))
            {
                var date = new DateTime(year, month, day);
                var hasUsage = usageByDay.TryGetValue(date, out var kwh);
                cells.Add(new SmartEnergyCalendarCell(
                    day.ToString(),
                    hasUsage ? $"{kwh:0.##} 度" : string.Empty,
                    hasUsage,
                    date > today));
            }

            return cells;
        }
    }

    public string InsightsMessage => IsInsightsLoading ? "正在读取 Home Assistant 历史记录…" : _insights.Message;

    public bool IsInsightsLoading
    {
        get => _isInsightsLoading;
        private set
        {
            if (_isInsightsLoading == value) return;
            _isInsightsLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InsightsMessage));
        }
    }

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

    public string StatusText => !IsAvailable ? (_device.IsTelemetryStale ? "数据过期" : "离线") : IsBusy ? "正在同步" : "在线";

    public string ToggleLabel => IsOn ? "关闭" : "开启";

    public string CompactState => !IsAvailable ? (_device.IsTelemetryStale ? "数据已过期" : "设备离线") : (IsCameraLike || IsSpeakerLike) ? "在线" : MainState;

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

    public PackIconMaterialKind IconKind => ResolveIconKind();

    public Brush AccentBrush => CreateBrush(!IsAvailable
        ? "#9CA6B4"
        : IsOn ? "#4D7CFE" : "#72819A");

    public Brush AccentBackground => CreateBrush(!IsAvailable
        ? "#EEF0F4"
        : IsOn ? "#E4EAF7" : "#EDF1F6");

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
        _optimisticColorTemperatureKelvin = null;
        _optimisticHvacMode = null;
        _optimisticFanMode = null;
        _optimisticSwingMode = null;
        _optimisticPresetMode = null;
        _optimisticEntityStates.Clear();
        _optimisticEntitySelections.Clear();
        IsBusy = false;
        OperationMessage = string.Empty;
        NotifyAll();
    }

    public void ApplyOptimistic(SmartHomeControlRequest request)
    {
        if (!string.Equals(request.EntityId, EntityId, StringComparison.Ordinal))
        {
            if (request.Action is SmartHomeControlAction.TurnOn or SmartHomeControlAction.TurnOff)
            {
                _optimisticEntityStates[request.EntityId] = request.Action == SmartHomeControlAction.TurnOn;
            }
            else if (request.Action == SmartHomeControlAction.SelectOption && request.TextValue is { } selection)
            {
                _optimisticEntitySelections[request.EntityId] = selection;
            }
            IsBusy = true;
            OperationMessage = "正在同步真实状态…";
            NotifyAll();
            return;
        }

        _optimisticIsOn = request.Action switch
        {
            SmartHomeControlAction.TurnOn => true,
            SmartHomeControlAction.TurnOff => false,
            _ => _optimisticIsOn
        };
        if (request.NumericValue is not null && request.Action != SmartHomeControlAction.SetColorTemperature)
        {
            _optimisticValue = request.NumericValue;
        }
        switch (request.Action)
        {
            case SmartHomeControlAction.SetColorTemperature:
                _optimisticColorTemperatureKelvin = request.NumericValue;
                _optimisticIsOn = true;
                break;
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

        // 亮度/色温属于拖动连续调整：只应用乐观值，不进入“正在同步”状态，避免电源键与状态文字闪烁。
        if (request.Action is SmartHomeControlAction.SetBrightness or SmartHomeControlAction.SetColorTemperature)
        {
            IsBusy = false;
            OperationMessage = string.Empty;
        }
        else
        {
            IsBusy = true;
            OperationMessage = "正在同步真实状态…";
        }

        NotifyAll();
    }

    public void Rollback(string message)
    {
        _optimisticIsOn = null;
        _optimisticValue = null;
        _optimisticColorTemperatureKelvin = null;
        _optimisticHvacMode = null;
        _optimisticFanMode = null;
        _optimisticSwingMode = null;
        _optimisticPresetMode = null;
        _optimisticEntityStates.Clear();
        _optimisticEntitySelections.Clear();
        IsBusy = false;
        OperationMessage = message;
        NotifyAll();
    }

    private SmartEntity? Primary => _device.PrimaryEntity;

    public bool ContainsEntity(string entityId) => _device.Entities.Any(entity =>
        string.Equals(entity.EntityId, entityId, StringComparison.Ordinal));

    public double ToSourceTemperature(double celsius) => IsSourceFahrenheit
        ? celsius * 9d / 5d + 32d
        : celsius;

    public string ColorTemperatureKelvinText => $"{ToColorTemperatureKelvin(ColorTemperaturePercent):0}K";

    public double ToColorTemperatureKelvin(double percent)
    {
        var normalized = Math.Clamp(percent, 0, 100) / 100d;
        return MinimumColorTemperatureKelvin + normalized * (MaximumColorTemperatureKelvin - MinimumColorTemperatureKelvin);
    }

    public void BeginInsightsLoad()
    {
        _insights = new SmartDeviceInsights();
        IsInsightsLoading = true;
        NotifyInsights();
    }

    public void ApplyInsights(SmartDeviceInsights insights)
    {
        _insights = insights;
        IsInsightsLoading = false;
        NotifyInsights();
    }

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
        if (!IsAvailable) return _device.IsTelemetryStale ? "数据已过期" : "设备不可用";
        if (IsCameraLike || IsSpeakerLike) return "在线";
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
            "media_player" => entity.State.ToLowerInvariant() switch
            {
                "playing" => "播放中",
                "paused" => "已暂停",
                "buffering" => "缓冲中",
                _ => TranslateState(entity.State)
            },
            "sensor" => BuildSensorState(entity),
            "binary_sensor" => TranslateBinarySensor(entity),
            _ => TranslateState(entity.State)
        };
    }

    private string BuildSupportingSummary()
    {
        // 空调的传感器大多为故障码/功率参数等诊断信息，摘要行只保留型号，避免噪音。
        if (IsClimate) return string.IsNullOrWhiteSpace(_device.Model) ? "空调与恒温设备" : _device.Model;
        var values = _device.Entities
            .Where(entity => !ReferenceEquals(entity, Primary) && entity.Available && entity.Domain == "sensor")
            .Where(entity => !string.IsNullOrWhiteSpace(entity.State))
            .Select(entity => $"{entity.Name.Trim()} {entity.State}{entity.GetText("unit_of_measurement")}")
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
            "media_player" => "音箱与媒体设备",
            "sensor" => "环境传感器",
            "binary_sensor" => "状态传感器",
            _ => "智能家居设备"
        };
    }

    private string BuildClimateState(SmartEntity entity)
    {
        var current = entity.GetNumber("current_temperature");
        var mode = TranslateHvacMode(CurrentHvacMode);
        return current is null ? mode : $"{ToCelsius(current.Value):0.#}°C · {mode}";
    }

    private string BuildSensorState(SmartEntity entity)
    {
        var unit = entity.GetText("unit_of_measurement") ?? string.Empty;
        var isTemperature = string.Equals(entity.GetText("device_class"), "temperature", StringComparison.OrdinalIgnoreCase) ||
                            unit is "°F" or "°C" or "℃";
        if (!isTemperature || !double.TryParse(entity.State, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            var text = $"{entity.State}{unit}";
            // 状态为空的传感器不能渲染成空行；此时设备在线语义优先。
            return string.IsNullOrWhiteSpace(text) ? "在线" : text;
        }

        var celsius = unit.Contains("F", StringComparison.OrdinalIgnoreCase)
            ? (value - 32d) * 5d / 9d
            : value;
        return $"{celsius:0.#}°C";
    }

    private bool IsSourceFahrenheit => _device.TemperatureUnit.Contains("F", StringComparison.OrdinalIgnoreCase);

    private double ToCelsius(double value) => IsSourceFahrenheit ? (value - 32d) * 5d / 9d : value;

    private double MinimumColorTemperatureKelvin =>
        Primary?.GetNumber("min_color_temp_kelvin") ??
        MiredToKelvin(Primary?.GetNumber("max_mireds")) ??
        2200;

    private double MaximumColorTemperatureKelvin =>
        Primary?.GetNumber("max_color_temp_kelvin") ??
        MiredToKelvin(Primary?.GetNumber("min_mireds")) ??
        6500;

    private double CurrentColorTemperatureKelvin =>
        Primary?.GetNumber("color_temp_kelvin") ??
        MiredToKelvin(Primary?.GetNumber("color_temp")) ??
        4000;

    private static double? MiredToKelvin(double? mired) => mired is > 0 ? 1_000_000d / mired.Value : null;

    private string ResolveLiveTemperatureText()
    {
        if (Primary?.GetNumber("current_temperature") is { } climateValue)
        {
            return $"{ToCelsius(climateValue):0.#}°C";
        }

        var sensor = _device.Entities.FirstOrDefault(entity =>
            string.Equals(entity.GetText("device_class"), "temperature", StringComparison.OrdinalIgnoreCase) ||
            entity.GetText("unit_of_measurement") is "℃" or "°C" or "°F");
        if (sensor is null || !double.TryParse(sensor.State, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)) return "--°C";
        var unit = sensor.GetText("unit_of_measurement") ?? string.Empty;
        var celsius = unit.Contains("F", StringComparison.OrdinalIgnoreCase) ? (value - 32d) * 5d / 9d : value;
        return $"{celsius:0.#}°C";
    }

    private string ResolveLiveHumidityText()
    {
        var sensor = _device.Entities.FirstOrDefault(entity =>
            string.Equals(entity.GetText("device_class"), "humidity", StringComparison.OrdinalIgnoreCase) ||
            entity.GetText("unit_of_measurement") == "%");
        return sensor is null ? "--%" : $"{sensor.State}%";
    }

    private string? ResolveDeviceArtworkUri()
    {
        var hint = BuildDeviceHint();
        var file = ContainsAny(hint, "camera", "摄像", "监控") ? "camera.png"
            : ContainsAny(hint, "air-conditioner", "thermostat", "空调") ? "air-conditioner.png"
            : ContainsAny(hint, "ceiling-fan", "fan", "风扇") ? "fan.png"
            : ContainsAny(hint, "lightbulb", "bulb", "lamp", "吸顶", "灯泡", "灯") ? "bulb.png"
            : ContainsAny(hint, "speaker", "音箱", "音响", "小爱") ? "smart-speaker.png"
            : ContainsAny(hint, "router", "路由") ? "router.png"
            : ContainsAny(hint, "temperature", "thermometer", "humidity", "温度", "湿度") ? "thermo-hygrometer.png"
            : null;
        return file is null ? null : $"/XTool;component/Assets/SmartHome/Devices/{file}";
    }

    private string BuildDeviceHint() => string.Join(' ', new[]
    {
        Primary?.GetText("icon"),
        Primary?.GetText("device_class"),
        Name,
        _device.Model,
        Domain
    }.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();

    private IReadOnlyList<SmartFeatureOption> BuildFeatureControls()
    {
        var controls = new List<SmartFeatureOption>();
        if (IsClimate)
        {
            foreach (var entity in _device.Entities.Where(entity => entity.Domain is "switch" or "light"))
            {
                var definition = ResolveFeatureDefinition(entity.EntityId);
                if (definition is null) continue;
                var isOn = _optimisticEntityStates.TryGetValue(entity.EntityId, out var optimistic)
                    ? optimistic
                    : string.Equals(entity.State, "on", StringComparison.OrdinalIgnoreCase);
                controls.Add(new SmartFeatureOption(
                    entity.EntityId,
                    definition.Value.Label,
                    definition.Value.Glyph,
                    isOn,
                    entity.Available));
            }
        }

        // button 实体是“按一下执行”的一次性能力（播放、暂停、跳曲等），任何设备都可能暴露。
        foreach (var entity in _device.Entities.Where(entity => entity.Domain == "button"))
        {
            controls.Add(new SmartFeatureOption(
                entity.EntityId,
                BuildButtonLabel(entity),
                ResolveButtonGlyph(entity.EntityId),
                false,
                true));
        }

        return controls.Take(8).ToArray();
    }

    private static string BuildButtonLabel(SmartEntity entity)
    {
        var name = entity.Name.Trim();
        return string.IsNullOrWhiteSpace(name) ? entity.EntityId[(entity.EntityId.IndexOf('.') + 1)..] : name;
    }

    private static string ResolveButtonGlyph(string entityId)
    {
        if (entityId.Contains("pause", StringComparison.OrdinalIgnoreCase)) return "\uE769";
        if (entityId.Contains("previous", StringComparison.OrdinalIgnoreCase) || entityId.Contains("prev", StringComparison.OrdinalIgnoreCase)) return "\uE892";
        if (entityId.Contains("next", StringComparison.OrdinalIgnoreCase)) return "\uE893";
        if (entityId.Contains("alarm", StringComparison.OrdinalIgnoreCase)) return "\uE7A7";
        if (entityId.Contains("radio", StringComparison.OrdinalIgnoreCase) || entityId.Contains("music", StringComparison.OrdinalIgnoreCase)) return "\uE8D6";
        if (entityId.Contains("play", StringComparison.OrdinalIgnoreCase)) return "\uE768";
        return "\uE8D6";
    }

    private IReadOnlyList<SmartSelectControl> BuildAuxiliarySelects()
    {
        if (!IsClimate) return Array.Empty<SmartSelectControl>();
        return _device.Entities
            .Where(entity => entity.Domain == "select" && entity.EntityId.Contains("vertical_angle", StringComparison.OrdinalIgnoreCase))
            .Select(entity => new SmartSelectControl(
                entity.EntityId,
                "风感方向",
                _optimisticEntitySelections.TryGetValue(entity.EntityId, out var optimistic) ? optimistic : entity.State,
                entity.GetTextList("options"),
                entity.Available))
            .Where(control => control.Options.Count > 0)
            .ToArray();
    }

    private static (string Label, string Glyph)? ResolveFeatureDefinition(string entityId)
    {
        if (entityId.Contains("eco_", StringComparison.OrdinalIgnoreCase)) return ("节能", "\uE8BE");
        if (entityId.Contains("heater_", StringComparison.OrdinalIgnoreCase)) return ("辅热", "\uE9CA");
        if (entityId.Contains("dryer_", StringComparison.OrdinalIgnoreCase)) return ("干燥", "\uE9CE");
        if (entityId.Contains("sleep_mode", StringComparison.OrdinalIgnoreCase)) return ("睡眠", "\uE708");
        if (entityId.Contains("un_straight_blowing", StringComparison.OrdinalIgnoreCase)) return ("防直吹", "\uE9A9");
        if (entityId.Contains("favorite_on", StringComparison.OrdinalIgnoreCase)) return ("喜好", "\uE734");
        if (entityId.Contains("indicator_light", StringComparison.OrdinalIgnoreCase)) return ("灯光", "\uE706");
        if (entityId.Contains("alarm_", StringComparison.OrdinalIgnoreCase)) return ("提示音", "\uE995");
        return null;
    }

    private void NotifyInsights()
    {
        foreach (var property in new[]
                 {
                     nameof(TemperaturePoints), nameof(HasTemperatureHistory), nameof(HasEnergyData),
                     nameof(IndoorTemperatureText), nameof(IndoorHumidityText), nameof(TodayEnergyText),
                     nameof(MonthEnergyText), nameof(EnergyMonthLabel), nameof(EnergyCalendarCells),
                     nameof(HasDailyEnergy), nameof(MonthlyEnergyRows), nameof(HasMonthlyEnergy), nameof(IsNotClimate),
                     nameof(InsightsMessage), nameof(IsInsightsLoading)
                 })
        {
            OnPropertyChanged(property);
        }
    }

    private static IReadOnlyList<SmartModeOption> BuildModeOptions(
        IReadOnlyList<string>? values,
        Func<string, string> translate)
    {
        if (values is null || values.Count == 0) return Array.Empty<SmartModeOption>();
        return values.Select(value => new SmartModeOption(value, translate(value), ResolveModeIcon(value))).ToArray();
    }

    /// <summary>按模式语义选择 Material 图标，供详情页圆形模式按钮使用。</summary>
    private static string ResolveModeIcon(string value)
    {
        var text = value.ToLowerInvariant();
        if (text.Contains("睡眠") || text.Contains("sleep")) return "WeatherNight";
        if (text.Contains("自然") || text.Contains("natural")) return "Leaf";
        if (text.Contains("直吹") || text.Contains("direct")) return "WeatherWindy";
        if (text.Contains("摆风") || text.Contains("左右") || text.Contains("swing")) return "ArrowLeftRight";
        if (text.Contains("制冷") || text.Contains("cool")) return "Snowflake";
        if (text.Contains("制热") || text.Contains("heat")) return "Fire";
        if (text.Contains("除湿") || text.Contains("dry")) return "Water";
        if (text.Contains("送风") || text.Contains("fan_only")) return "Fan";
        if (text.Contains("自动") || text.Contains("auto")) return "Autorenew";
        if (text.Contains("情绪") || text.Contains("音乐") || text.Contains("music")) return "MusicNote";
        return "Fan";
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
                     nameof(CanInteract), nameof(IsCameraLike), nameof(IsSpeakerLike), nameof(IsOn), nameof(IsWorking), nameof(CanToggle), nameof(SupportsBrightness), nameof(SupportsTargetTemperature),
                     nameof(SupportsColorTemperature), nameof(ColorTemperaturePercent), nameof(ColorTemperatureKelvinText), nameof(LiveTemperatureText), nameof(LiveHumidityText),
                     nameof(DeviceArtworkUri), nameof(HasDeviceArtwork), nameof(IsLightArtwork), nameof(IsClimateArtwork), nameof(IsThermoHygrometerArtwork),
                     nameof(ArtworkOpacity), nameof(ArtworkScale),
                     nameof(SupportsCoverPosition), nameof(SupportsFanPercentage), nameof(SupportsHvacMode),
                     nameof(SupportsFanMode), nameof(SupportsSwingMode), nameof(SupportsPreset), nameof(IsClimate),
                     nameof(IsFan), nameof(BrightnessPercent), nameof(CoverPosition), nameof(FanPercentage),
                     nameof(TemperatureUnit), nameof(TargetTemperature), nameof(TargetTemperatureText),
                     nameof(MinimumTemperature), nameof(MaximumTemperature), nameof(TemperatureStep),
                     nameof(CurrentTemperatureText), nameof(FeatureControls), nameof(AuxiliarySelects),
                     nameof(HasFeatureControls), nameof(HasAuxiliarySelects),
                     nameof(HvacModeOptions), nameof(FanModeOptions), nameof(SwingModeOptions),
                     nameof(PresetModeOptions), nameof(CurrentHvacMode), nameof(CurrentFanMode),
                     nameof(FanLevels), nameof(CurrentFanLevel),
                     nameof(CurrentSwingMode), nameof(CurrentPresetMode), nameof(MainState),
                     nameof(SupportingSummary), nameof(StatusText), nameof(CompactState), nameof(DeviceTypeLabel),
                     nameof(HasIndoorTemperature), nameof(HasIndoorHumidity), nameof(CanRename),
                     nameof(ToggleLabel), nameof(IconKind), nameof(AccentBrush), nameof(AccentBackground)
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

    private PackIconMaterialKind ResolveIconKind()
    {
        var hint = BuildDeviceHint();

        // Home Assistant 同样使用 Material Design Icons；优先按设备语义选择具体家电图标。
        if (ContainsAny(hint, "air-purifier", "purifier", "净化")) return PackIconMaterialKind.AirPurifier;
        if (ContainsAny(hint, "camera", "摄像", "监控")) return PackIconMaterialKind.CameraOutline;
        if (ContainsAny(hint, "air-conditioner", "thermostat", "空调")) return PackIconMaterialKind.AirConditioner;
        if (ContainsAny(hint, "ceiling-fan", "fan", "风扇")) return PackIconMaterialKind.CeilingFan;
        if (ContainsAny(hint, "ceiling-light", "吸顶")) return PackIconMaterialKind.CeilingLight;
        if (ContainsAny(hint, "lightbulb", "bulb", "lamp", "灯泡", "灯")) return PackIconMaterialKind.LightbulbOutline;
        if (ContainsAny(hint, "speaker", "音箱", "音响")) return PackIconMaterialKind.SpeakerWireless;
        if (ContainsAny(hint, "router", "路由")) return PackIconMaterialKind.RouterWireless;
        if (ContainsAny(hint, "temperature", "thermometer", "温度", "湿度")) return PackIconMaterialKind.HomeThermometerOutline;
        if (ContainsAny(hint, "plug", "outlet", "socket", "插座")) return PackIconMaterialKind.PowerSocket;
        if (ContainsAny(hint, "curtain", "blind", "cover", "窗帘")) return PackIconMaterialKind.Curtains;
        if (ContainsAny(hint, "door", "window", "门窗")) return IsOn ? PackIconMaterialKind.DoorOpen : PackIconMaterialKind.DoorClosed;

        return Domain switch
        {
            "camera" => PackIconMaterialKind.CameraOutline,
            "light" => PackIconMaterialKind.LightbulbOutline,
            "switch" => PackIconMaterialKind.ToggleSwitchOutline,
            "climate" => PackIconMaterialKind.AirConditioner,
            "cover" => PackIconMaterialKind.Curtains,
            "fan" => PackIconMaterialKind.CeilingFan,
            "binary_sensor" => PackIconMaterialKind.CheckCircleOutline,
            "sensor" => PackIconMaterialKind.Gauge,
            _ => PackIconMaterialKind.HomeAutomation
        };
    }

    private static bool ContainsAny(string source, params string[] values) =>
        values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));
}
