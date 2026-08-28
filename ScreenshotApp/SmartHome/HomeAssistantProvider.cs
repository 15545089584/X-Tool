using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ScreenshotApp.SmartHome;

/// <summary>通过 Home Assistant 公开的 REST 与 WebSocket 接口提供通用设备能力。</summary>
public sealed class HomeAssistantProvider : ISmartHomeProvider
{
    private static readonly HashSet<string> SupportedDomains = new(StringComparer.Ordinal)
    {
        "camera", "light", "switch", "climate", "cover", "fan", "media_player", "button", "select", "number", "sensor", "binary_sensor"
    };

    private readonly Uri _serverUri;
    private readonly string _token;
    private readonly HttpClient _httpClient;
    private readonly object _snapshotSync = new();
    private readonly Dictionary<string, JsonElement> _states = new(StringComparer.Ordinal);
    private JsonElement _areas = JsonDocument.Parse("[]").RootElement.Clone();
    private JsonElement _devices = JsonDocument.Parse("[]").RootElement.Clone();
    private JsonElement _entities = JsonDocument.Parse("[]").RootElement.Clone();
    private string _temperatureUnit = "°C";
    private HomeAssistantWebSocketClient? _webSocket;
    private readonly Dictionary<string, string> _deviceNameOverrides = new(StringComparer.Ordinal);
    private PeriodicTimer? _statePollTimer;
    private CancellationTokenSource? _statePollSource;

    public HomeAssistantProvider(Uri serverUri, string token)
    {
        _serverUri = serverUri;
        _token = token;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(serverUri.ToString().TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public event Action<SmartHomeSnapshot>? SnapshotChanged;

    public event Action<Exception?>? Disconnected;

    public SmartHomeSnapshot? CurrentSnapshot { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using (var response = await _httpClient.GetAsync("api/", cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new HomeAssistantAuthenticationException("Home Assistant 拒绝了 Access Token。");
            }
            response.EnsureSuccessStatusCode();
        }

        _temperatureUnit = await LoadTemperatureUnitAsync(cancellationToken).ConfigureAwait(false);

        var socket = new HomeAssistantWebSocketClient();
        socket.EventReceived += WebSocket_EventReceived;
        socket.Disconnected += exception => Disconnected?.Invoke(exception);
        await socket.ConnectAsync(_serverUri, _token, cancellationToken).ConfigureAwait(false);
        _webSocket = socket;

        _areas = await TryLoadRegistryAsync(socket, "config/area_registry/list", cancellationToken).ConfigureAwait(false);
        _devices = await TryLoadRegistryAsync(socket, "config/device_registry/list", cancellationToken).ConfigureAwait(false);
        _entities = await TryLoadRegistryAsync(socket, "config/entity_registry/list", cancellationToken).ConfigureAwait(false);
        var stateArray = await socket.SendCommandAsync("get_states", null, cancellationToken).ConfigureAwait(false);

        lock (_snapshotSync)
        {
            _states.Clear();
            if (stateArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var state in stateArray.EnumerateArray())
                {
                    var entityId = GetString(state, "entity_id");
                    if (!string.IsNullOrWhiteSpace(entityId)) _states[entityId] = state.Clone();
                }
            }
            CurrentSnapshot = BuildSnapshot();
        }

        await socket.SendCommandAsync("subscribe_events", new Dictionary<string, object?>
        {
            ["event_type"] = "state_changed"
        }, cancellationToken).ConfigureAwait(false);

        // WebSocket 事件之外再加 20 秒 REST 轮询兜底：WS 事件中断时设备状态仍能自动刷新。
        StartStatePolling();

        SnapshotChanged?.Invoke(CurrentSnapshot);
    }

    private void StartStatePolling()
    {
        StopStatePollingCore();
        _statePollSource = new CancellationTokenSource();
        _statePollTimer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        _ = PollStatesAsync(_statePollTimer, _statePollSource.Token);
    }

    private async Task PollStatesAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    using var response = await _httpClient.GetAsync("api/states", cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    SmartHomeSnapshot? snapshot = null;
                    lock (_snapshotSync)
                    {
                        _states.Clear();
                        if (document.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var state in document.RootElement.EnumerateArray())
                            {
                                var entityId = GetString(state, "entity_id");
                                if (!string.IsNullOrWhiteSpace(entityId)) _states[entityId] = state.Clone();
                            }
                        }

                        CurrentSnapshot = snapshot = BuildSnapshot();
                    }

                    SnapshotChanged?.Invoke(snapshot);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // 单次轮询失败不影响后续轮询。
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止轮询属于正常释放流程。
        }
    }

    private void StopStatePollingCore()
    {
        _statePollSource?.Cancel();
        _statePollSource?.Dispose();
        _statePollSource = null;
        _statePollTimer?.Dispose();
        _statePollTimer = null;
    }

    public async Task ExecuteAsync(SmartHomeControlRequest request, CancellationToken cancellationToken)
    {
        var domain = GetDomain(request.EntityId);
        var service = request.Action switch
        {
            SmartHomeControlAction.TurnOn => "turn_on",
            SmartHomeControlAction.TurnOff => "turn_off",
            SmartHomeControlAction.SetBrightness => "turn_on",
            SmartHomeControlAction.SetColorTemperature => "turn_on",
            SmartHomeControlAction.SetTargetTemperature => "set_temperature",
            SmartHomeControlAction.OpenCover => "open_cover",
            SmartHomeControlAction.CloseCover => "close_cover",
            SmartHomeControlAction.StopCover => "stop_cover",
            SmartHomeControlAction.SetCoverPosition => "set_cover_position",
            SmartHomeControlAction.SetFanPercentage => "set_percentage",
            SmartHomeControlAction.SetPreset => "set_preset_mode",
            SmartHomeControlAction.SetHvacMode => "set_hvac_mode",
            SmartHomeControlAction.SetFanMode => "set_fan_mode",
            SmartHomeControlAction.SetSwingMode => "set_swing_mode",
            SmartHomeControlAction.SelectOption => "select_option",
            _ => throw new ArgumentOutOfRangeException(nameof(request), "不支持的智能家居控制操作。")
        };
        // button 实体只提供“按下”语义：界面发出的开关动作统一映射为 press。
        if (domain == "button") service = "press";

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entity_id"] = request.EntityId
        };
        switch (request.Action)
        {
            case SmartHomeControlAction.SetBrightness:
                body["brightness_pct"] = Math.Clamp((int)Math.Round(request.NumericValue ?? 0), 0, 100);
                break;
            case SmartHomeControlAction.SetColorTemperature:
                body["color_temp_kelvin"] = Math.Clamp((int)Math.Round(request.NumericValue ?? 2700), 1000, 12000);
                break;
            case SmartHomeControlAction.SetTargetTemperature:
                body["temperature"] = request.NumericValue;
                break;
            case SmartHomeControlAction.SetCoverPosition:
                body["position"] = Math.Clamp((int)Math.Round(request.NumericValue ?? 0), 0, 100);
                break;
            case SmartHomeControlAction.SetFanPercentage:
                body["percentage"] = Math.Clamp((int)Math.Round(request.NumericValue ?? 0), 0, 100);
                break;
            case SmartHomeControlAction.SetPreset:
                body["preset_mode"] = request.TextValue;
                break;
            case SmartHomeControlAction.SetHvacMode:
                body["hvac_mode"] = request.TextValue;
                break;
            case SmartHomeControlAction.SetFanMode:
                body["fan_mode"] = request.TextValue;
                break;
            case SmartHomeControlAction.SetSwingMode:
                body["swing_mode"] = request.TextValue;
                break;
            case SmartHomeControlAction.SelectOption:
                body["option"] = request.TextValue;
                break;
        }

        if (_webSocket is not null)
        {
            body.Remove("entity_id");
            await _webSocket.SendCommandAsync("call_service", new Dictionary<string, object?>
            {
                ["domain"] = domain,
                ["service"] = service,
                ["service_data"] = body,
                ["target"] = new Dictionary<string, object?> { ["entity_id"] = request.EntityId }
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"api/services/{domain}/{service}", body, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new HomeAssistantAuthenticationException("Home Assistant Access Token 已失效。");
        }
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"Home Assistant 控制失败（HTTP {(int)response.StatusCode}）。"
                : $"Home Assistant 控制失败：{detail}");
        }
    }

    public async Task RenameDeviceAsync(string deviceId, string name, CancellationToken cancellationToken)
    {
        if (_webSocket is null)
        {
            throw new InvalidOperationException("当前连接不支持重命名设备（需要 WebSocket 连接）。");
        }

        // name_by_user 就是用户在 Home Assistant 里设置的设备名，写入后随下一次快照自然生效。
        var result = await _webSocket.SendCommandAsync("config/device_registry/update", new Dictionary<string, object?>
        {
            ["device_id"] = deviceId,
            ["name_by_user"] = name
        }, cancellationToken).ConfigureAwait(false);

        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("success", out var success) &&
            success.ValueKind == JsonValueKind.False)
        {
            var detail = result.TryGetProperty("error", out var error) ? error.ToString() : "未知错误";
            throw new InvalidOperationException($"Home Assistant 拒绝了设备重命名：{detail}");
        }

        // 立即用新名称重建并发布快照：外部卡片不必等待重连或下一次轮询。
        _deviceNameOverrides[deviceId] = name;
        SmartHomeSnapshot? renamedSnapshot;
        lock (_snapshotSync)
        {
            CurrentSnapshot = renamedSnapshot = BuildSnapshot();
        }
        SnapshotChanged?.Invoke(renamedSnapshot);
    }

    public async Task<SmartDeviceInsights> GetInsightsAsync(
        SmartDevice device,
        int monthOffset,
        int yearOffset,
        CancellationToken cancellationToken)
    {
        var climate = device.Entities.FirstOrDefault(entity => entity.Domain == "climate" && entity.Available);
        if (climate is null)
        {
            return new SmartDeviceInsights { Message = "当前设备没有可读取的环境历史。" };
        }

        try
        {
            var now = DateTimeOffset.Now;
            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset).AddMonths(-Math.Max(0, monthOffset));
            var monthEnd = monthStart.AddMonths(1) > now ? now : monthStart.AddMonths(1);
            var historyStart = now.AddHours(-24);
            var energyEntity = device.Entities.FirstOrDefault(IsEnergyEntity);

            var temperatureTask = LoadHistoryAsync(climate.EntityId, historyStart, cancellationToken);
            var energyTask = energyEntity is null || monthStart > now
                ? Task.FromResult<IReadOnlyList<HistoryState>>(Array.Empty<HistoryState>())
                : LoadHistoryAsync(energyEntity.EntityId, monthStart, cancellationToken);
            var energyStatisticsTask = energyEntity is null || monthStart > now
                ? Task.FromResult<IReadOnlyList<EnergyStatistic>>(Array.Empty<EnergyStatistic>())
                : LoadEnergyStatisticsAsync(energyEntity.EntityId, monthStart, monthEnd, cancellationToken);
            var yearlyStatisticsTask = energyEntity is not null
                ? LoadEnergyStatisticsAsync(
                    energyEntity.EntityId,
                    new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset).AddYears(-Math.Max(0, yearOffset)),
                    now,
                    cancellationToken)
                : Task.FromResult<IReadOnlyList<EnergyStatistic>>(Array.Empty<EnergyStatistic>());
            await Task.WhenAll(temperatureTask, energyTask, energyStatisticsTask, yearlyStatisticsTask).ConfigureAwait(false);

            // 每小时聚合一个采样点：24 小时曲线固定 24 个点，按最接近整点的记录取值。
            var temperaturePoints = temperatureTask.Result
                .Where(item => item.CurrentTemperature is not null)
                .GroupBy(item => new DateTimeOffset(
                    item.Timestamp.LocalDateTime.Year, item.Timestamp.LocalDateTime.Month,
                    item.Timestamp.LocalDateTime.Day, item.Timestamp.LocalDateTime.Hour, 0, 0, item.Timestamp.Offset))
                .Select(group => group
                    .OrderBy(item => Math.Abs((item.Timestamp - group.Key).TotalMinutes))
                    .First())
                .Select(item => new SmartHistoryPoint(item.Timestamp, ToCelsius(item.CurrentTemperature!.Value)))
                .OrderBy(item => item.Timestamp)
                .ToArray();

            var energyHistory = energyTask.Result
                .Where(item => item.NumericState is not null)
                .OrderBy(item => item.Timestamp)
                .ToArray();
            var energyStatistics = energyStatisticsTask.Result;
            var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
            var currentTemperature = climate.GetNumber("current_temperature");
            var humidityEntity = device.Entities.FirstOrDefault(IsHumidityEntity);
            var humidity = climate.GetNumber("current_humidity") ?? climate.GetNumber("humidity") ??
                           GetEntityStateNumber(humidityEntity);

            var dailyEnergy = energyStatistics
                .GroupBy(item => item.Timestamp.Date)
                .Select(group => new SmartEnergyDayPoint(group.Key, Math.Round(group.Sum(item => item.ChangeKwh), 2)))
                .OrderBy(item => item.Date)
                .ToArray();
            var monthlyEnergy = yearlyStatisticsTask.Result
                .GroupBy(item => new { item.Timestamp.Year, item.Timestamp.Month })
                .Select(group => new SmartMonthEnergyPoint($"{group.Key.Year}/{group.Key.Month}", Math.Round(group.Sum(item => item.ChangeKwh), 2)))
                .OrderBy(item => item.Label)
                .ToArray();

            return new SmartDeviceInsights
            {
                TemperaturePoints = temperaturePoints,
                IndoorTemperatureCelsius = currentTemperature is null ? null : ToCelsius(currentTemperature.Value),
                IndoorHumidityPercent = humidity,
                TodayEnergyKwh = CalculateStatisticsUsage(energyStatistics, todayStart) ??
                                 CalculateEnergyUsage(energyHistory, todayStart),
                MonthEnergyKwh = CalculateStatisticsUsage(energyStatistics, monthStart) ??
                                 CalculateEnergyUsage(energyHistory, monthStart),
                EnergyMonthLabel = $"{monthStart.Year}/{monthStart.Month}",
                EnergyYearLabel = $"{now.Year - Math.Max(0, yearOffset)}",
                DailyEnergy = dailyEnergy,
                MonthlyEnergy = monthlyEnergy,
                Message = temperaturePoints.Length == 0 && energyHistory.Length == 0 && energyStatistics.Count == 0
                    ? "Home Assistant 暂无可用的历史记录。"
                    : string.Empty
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new SmartDeviceInsights { Message = "历史数据暂时不可用，设备控制不受影响。" };
        }
    }

    private void WebSocket_EventReceived(JsonElement message)
    {
        try
        {
            if (!message.TryGetProperty("event", out var eventElement) ||
                !eventElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("entity_id", out var entityIdElement)) return;

            var entityId = entityIdElement.GetString();
            if (string.IsNullOrWhiteSpace(entityId)) return;

            SmartHomeSnapshot snapshot;
            lock (_snapshotSync)
            {
                if (!data.TryGetProperty("new_state", out var newState) || newState.ValueKind == JsonValueKind.Null)
                {
                    _states.Remove(entityId);
                }
                else
                {
                    _states[entityId] = newState.Clone();
                }
                CurrentSnapshot = snapshot = BuildSnapshot();
            }
            SnapshotChanged?.Invoke(snapshot);
        }
        catch
        {
            // 单条异常事件不能中断后续实时状态同步。
        }
    }

    private SmartHomeSnapshot BuildSnapshot()
    {
        var areaNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_areas.ValueKind == JsonValueKind.Array)
        {
            foreach (var area in _areas.EnumerateArray())
            {
                var id = GetString(area, "area_id") ?? GetString(area, "id");
                var name = GetString(area, "name");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name)) areaNames[id] = name;
            }
        }

        var deviceRegistry = new Dictionary<string, DeviceRegistryItem>(StringComparer.Ordinal);
        if (_devices.ValueKind == JsonValueKind.Array)
        {
            foreach (var device in _devices.EnumerateArray())
            {
                var id = GetString(device, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                deviceRegistry[id] = new DeviceRegistryItem(
                    id,
                    GetString(device, "name_by_user") ?? GetString(device, "name") ?? string.Empty,
                    GetString(device, "manufacturer") ?? string.Empty,
                    GetString(device, "model") ?? string.Empty,
                    GetString(device, "area_id"));
            }
        }

        var entityRegistry = new Dictionary<string, EntityRegistryItem>(StringComparer.Ordinal);
        if (_entities.ValueKind == JsonValueKind.Array)
        {
            foreach (var entity in _entities.EnumerateArray())
            {
                var entityId = GetString(entity, "entity_id");
                if (string.IsNullOrWhiteSpace(entityId) || HasNonNullProperty(entity, "disabled_by") ||
                    HasNonNullProperty(entity, "hidden_by")) continue;
                entityRegistry[entityId] = new EntityRegistryItem(
                    entityId,
                    GetString(entity, "device_id"),
                    GetString(entity, "area_id"),
                    GetString(entity, "name") ?? GetString(entity, "original_name"));
            }
        }

        var groupedEntities = new Dictionary<string, List<SmartEntity>>(StringComparer.Ordinal);
        foreach (var pair in _states)
        {
            var domain = GetDomain(pair.Key);
            if (!SupportedDomains.Contains(domain)) continue;

            entityRegistry.TryGetValue(pair.Key, out var registryItem);
            var attributes = ReadAttributes(pair.Value);
            var friendlyName = registryItem?.Name ?? GetAttributeText(attributes, "friendly_name") ?? pair.Key;
            var smartEntity = new SmartEntity
            {
                EntityId = pair.Key,
                Name = friendlyName,
                State = GetString(pair.Value, "state") ?? "unknown",
                LastUpdated = GetTimestamp(pair.Value, "last_updated") ?? GetTimestamp(pair.Value, "last_changed"),
                DeviceId = registryItem?.DeviceId,
                AreaId = registryItem?.AreaId,
                Attributes = attributes
            };
            var groupId = registryItem?.DeviceId ?? $"entity:{pair.Key}";
            if (!groupedEntities.TryGetValue(groupId, out var list))
            {
                list = [];
                groupedEntities[groupId] = list;
            }
            list.Add(smartEntity);
        }

        var smartDevices = new List<SmartDevice>();
        foreach (var pair in groupedEntities)
        {
            deviceRegistry.TryGetValue(pair.Key, out var device);
            var primary = pair.Value
                .OrderBy(EntityPriority)
                .ThenBy(entity => entity.EntityId, StringComparer.Ordinal)
                .First();
            if (!ShouldIncludeDevice(device, primary)) continue;
            var areaId = primary.AreaId ?? device?.AreaId;
            var deviceName = _deviceNameOverrides.TryGetValue(pair.Key, out var nameOverride) &&
                             !string.IsNullOrWhiteSpace(nameOverride)
                ? nameOverride
                : string.IsNullOrWhiteSpace(device?.Name) ? primary.Name : device.Name;
            smartDevices.Add(new SmartDevice
            {
                Id = pair.Key,
                Name = deviceName,
                Manufacturer = device?.Manufacturer ?? string.Empty,
                Model = device?.Model ?? string.Empty,
                AreaId = areaId,
                AreaName = areaId is not null && areaNames.TryGetValue(areaId, out var areaName)
                    ? areaName
                    : "未分配房间",
                TemperatureUnit = _temperatureUnit,
                Entities = pair.Value
            });
        }

        var usedAreaIds = smartDevices.Where(device => device.AreaId is not null)
            .Select(device => device.AreaId!)
            .ToHashSet(StringComparer.Ordinal);
        var areas = areaNames.Where(pair => usedAreaIds.Contains(pair.Key))
            .Select(pair => new SmartArea(pair.Key, pair.Value))
            .OrderBy(area => area.Name, StringComparer.CurrentCulture)
            .ToList();
        if (smartDevices.Any(device => device.AreaId is null)) areas.Add(new SmartArea("__unassigned", "未分配房间"));

        return new SmartHomeSnapshot
        {
            HomeName = "Home Assistant",
            Areas = areas,
            Devices = smartDevices
                .OrderBy(device => device.AreaName, StringComparer.CurrentCulture)
                .ThenBy(device => device.Name, StringComparer.CurrentCulture)
                .ToList(),
            UpdatedAt = DateTimeOffset.Now
        };
    }

    private static async Task<JsonElement> TryLoadRegistryAsync(
        HomeAssistantWebSocketClient socket,
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await socket.SendCommandAsync(command, null, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return JsonDocument.Parse("[]").RootElement.Clone();
        }
    }

    private async Task<string> LoadTemperatureUnitAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("api/config", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            if (document.RootElement.TryGetProperty("unit_system", out var unitSystem) &&
                unitSystem.TryGetProperty("temperature", out var temperature) &&
                temperature.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(temperature.GetString()))
            {
                return temperature.GetString()!;
            }
        }
        catch
        {
            // 旧版本或受限账户无法读取配置时继续使用摄氏度兜底。
        }

        return "°C";
    }

    private async Task<IReadOnlyList<HistoryState>> LoadHistoryAsync(
        string entityId,
        DateTimeOffset start,
        CancellationToken cancellationToken)
    {
        var timestamp = Uri.EscapeDataString(start.ToString("O"));
        var filter = Uri.EscapeDataString(entityId);
        using var response = await _httpClient.GetAsync(
            $"api/history/period/{timestamp}?filter_entity_id={filter}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0 ||
            document.RootElement[0].ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<HistoryState>();
        }

        var items = new List<HistoryState>();
        foreach (var state in document.RootElement[0].EnumerateArray())
        {
            var timestampText = GetString(state, "last_updated") ?? GetString(state, "last_changed");
            if (!DateTimeOffset.TryParse(timestampText, out var stateTimestamp)) continue;

            double? numericState = null;
            var stateText = GetString(state, "state");
            if (double.TryParse(stateText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedState))
            {
                numericState = parsedState;
            }

            double? currentTemperature = null;
            if (state.TryGetProperty("attributes", out var attributes) &&
                attributes.ValueKind == JsonValueKind.Object)
            {
                if (attributes.TryGetProperty("current_temperature", out var currentElement) &&
                    currentElement.ValueKind == JsonValueKind.Number && currentElement.TryGetDouble(out var parsedTemperature))
                {
                    currentTemperature = parsedTemperature;
                }
                if (numericState is not null && attributes.TryGetProperty("unit_of_measurement", out var unitElement) &&
                    unitElement.ValueKind == JsonValueKind.String && unitElement.GetString() is { } unit &&
                    string.Equals(unit, "Wh", StringComparison.OrdinalIgnoreCase))
                {
                    numericState /= 1000d;
                }
            }

            items.Add(new HistoryState(stateTimestamp, numericState, currentTemperature));
        }
        return items;
    }

    private async Task<IReadOnlyList<EnergyStatistic>> LoadEnergyStatisticsAsync(
        string entityId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        if (_webSocket is null) return Array.Empty<EnergyStatistic>();
        try
        {
            var result = await _webSocket.SendCommandAsync(
                "recorder/statistics_during_period",
                new Dictionary<string, object?>
                {
                    ["start_time"] = start.ToUniversalTime().ToString("O"),
                    ["end_time"] = end.ToUniversalTime().ToString("O"),
                    ["statistic_ids"] = new[] { entityId },
                    ["period"] = "hour",
                    ["types"] = new[] { "change" },
                    ["units"] = new Dictionary<string, object?> { ["energy"] = "kWh" }
                },
                cancellationToken).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(entityId, out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<EnergyStatistic>();
            }

            var statistics = new List<EnergyStatistic>();
            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("change", out var changeElement) ||
                    changeElement.ValueKind != JsonValueKind.Number || !changeElement.TryGetDouble(out var change) ||
                    !row.TryGetProperty("start", out var startElement)) continue;

                DateTimeOffset timestamp;
                if (startElement.ValueKind == JsonValueKind.Number && startElement.TryGetInt64(out var milliseconds))
                {
                    timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime();
                }
                else if (startElement.ValueKind == JsonValueKind.String &&
                         DateTimeOffset.TryParse(startElement.GetString(), out var parsedTimestamp))
                {
                    timestamp = parsedTimestamp.ToLocalTime();
                }
                else
                {
                    continue;
                }
                if (change >= 0) statistics.Add(new EnergyStatistic(timestamp, change));
            }
            return statistics;
        }
        catch
        {
            // 部分实体没有长期统计；调用方继续回退到普通历史记录。
            return Array.Empty<EnergyStatistic>();
        }
    }

    private double ToCelsius(double value) => _temperatureUnit.Contains("F", StringComparison.OrdinalIgnoreCase)
        ? (value - 32d) * 5d / 9d
        : value;

    private static IReadOnlyList<SmartHistoryPoint> Downsample(
        IReadOnlyList<SmartHistoryPoint> points,
        int maximumCount)
    {
        if (points.Count <= maximumCount) return points;
        var result = new List<SmartHistoryPoint>(maximumCount);
        for (var index = 0; index < maximumCount; index++)
        {
            var sourceIndex = (int)Math.Round(index * (points.Count - 1d) / (maximumCount - 1d));
            result.Add(points[sourceIndex]);
        }
        return result;
    }

    private static double? CalculateEnergyUsage(
        IReadOnlyList<HistoryState> history,
        DateTimeOffset start)
    {
        var values = history
            .Where(item => item.Timestamp >= start && item.NumericState is not null)
            .Select(item => item.NumericState!.Value)
            .ToArray();
        if (values.Length < 2) return null;

        var total = 0d;
        for (var index = 1; index < values.Length; index++)
        {
            var delta = values[index] - values[index - 1];
            if (delta >= 0) total += delta;
        }
        return Math.Round(total, 2);
    }

    private static double? CalculateStatisticsUsage(
        IReadOnlyList<EnergyStatistic> statistics,
        DateTimeOffset start)
    {
        var values = statistics.Where(item => item.Timestamp >= start).Select(item => item.ChangeKwh).ToArray();
        return values.Length == 0 ? null : Math.Round(values.Sum(), 2);
    }

    private static bool IsEnergyEntity(SmartEntity entity) => entity.Domain == "sensor" &&
        (string.Equals(entity.GetText("device_class"), "energy", StringComparison.OrdinalIgnoreCase) ||
         entity.GetText("unit_of_measurement") is "kWh" or "Wh");

    private static bool IsHumidityEntity(SmartEntity entity) => entity.Domain == "sensor" &&
        (string.Equals(entity.GetText("device_class"), "humidity", StringComparison.OrdinalIgnoreCase) ||
         entity.GetText("unit_of_measurement") == "%" && entity.Name.Contains("湿度", StringComparison.Ordinal));

    private static double? GetEntityStateNumber(SmartEntity? entity) => entity is not null &&
        double.TryParse(entity.State, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
        ? value
        : null;

    private static Dictionary<string, JsonElement> ReadAttributes(JsonElement state)
    {
        var attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!state.TryGetProperty("attributes", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return attributes;
        }
        foreach (var property in element.EnumerateObject()) attributes[property.Name] = property.Value.Clone();
        return attributes;
    }

    private static string? GetAttributeText(Dictionary<string, JsonElement> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string GetDomain(string entityId)
    {
        var separator = entityId.IndexOf('.');
        return separator <= 0 ? string.Empty : entityId[..separator];
    }

    private static int DomainPriority(string domain) => domain switch
    {
        "camera" => 0,
        "climate" => 1,
        "cover" => 2,
        "fan" => 3,
        "light" => 4,
        // 音箱等设备优先 media_player，避免麦克风静音开关被当作主控制实体。
        "media_player" => 5,
        "switch" => 6,
        "binary_sensor" => 7,
        "select" => 8,
        "number" => 9,
        "sensor" => 10,
        _ => 100
    };

    private static bool ShouldIncludeDevice(DeviceRegistryItem? device, SmartEntity primary)
    {
        if (primary.AreaId is not null || device?.AreaId is not null) return true;
        if (primary.Domain is "camera" or "light" or "switch" or "climate" or "cover" or "fan") return true;

        // Backup、Sun 等内建系统实体没有物理设备、房间、设备类别或单位，不作为家电卡片展示。
        var hasPhysicalMetadata = !string.IsNullOrWhiteSpace(device?.Manufacturer) &&
                                  !string.Equals(device.Manufacturer, "Home Assistant", StringComparison.OrdinalIgnoreCase);
        var deviceClass = primary.GetText("device_class");
        var isSystemOnlyClass = deviceClass is "enum" or "timestamp" or "date";
        return hasPhysicalMetadata || (!isSystemOnlyClass &&
               (!string.IsNullOrWhiteSpace(deviceClass) || !string.IsNullOrWhiteSpace(primary.GetText("unit_of_measurement"))));
    }

    private static int EntityPriority(SmartEntity entity)
    {
        var priority = DomainPriority(entity.Domain) * 10;
        // 与 SmartHomeModels 保持一致：辅助开关不能成为快照侧的设备主实体。
        if (entity.Domain is "switch" or "binary_sensor" && IsAuxiliaryControl(entity)) priority += 1000;
        if (entity.Domain != "sensor") return priority;
        var deviceClass = entity.GetText("device_class");
        var unit = entity.GetText("unit_of_measurement");
        if (deviceClass == "temperature" || unit is "℃" or "°C") return priority;
        if (deviceClass == "humidity" || unit == "%") return priority + 1;
        return priority + 2;
    }

    private static bool IsAuxiliaryControl(SmartEntity entity)
    {
        var text = $"{entity.EntityId} {entity.Name}".ToLowerInvariant();
        return text.Contains("mute", StringComparison.Ordinal) || text.Contains("microphone", StringComparison.Ordinal) ||
               text.Contains("_mic", StringComparison.Ordinal) || text.Contains("sleep", StringComparison.Ordinal) ||
               text.Contains("indicator", StringComparison.Ordinal) || text.Contains("night_light", StringComparison.Ordinal) ||
               text.Contains("child_lock", StringComparison.Ordinal) || text.Contains("physical_controls_locked", StringComparison.Ordinal) ||
               text.Contains("alarm", StringComparison.Ordinal) || text.Contains("buzzer", StringComparison.Ordinal) ||
               text.Contains("beep", StringComparison.Ordinal) || text.Contains("task_switch", StringComparison.Ordinal) ||
               text.Contains("power_enable", StringComparison.Ordinal);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetTimestamp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp)
            ? timestamp
            : null;

    private static bool HasNonNullProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    public async ValueTask DisposeAsync()
    {
        StopStatePollingCore();
        if (_webSocket is not null)
        {
            _webSocket.EventReceived -= WebSocket_EventReceived;
            await _webSocket.DisposeAsync().ConfigureAwait(false);
        }
        _httpClient.Dispose();
    }

    private sealed record DeviceRegistryItem(
        string Id,
        string Name,
        string Manufacturer,
        string Model,
        string? AreaId);

    private sealed record EntityRegistryItem(
        string EntityId,
        string? DeviceId,
        string? AreaId,
        string? Name);

    private sealed record HistoryState(
        DateTimeOffset Timestamp,
        double? NumericState,
        double? CurrentTemperature);

    private sealed record EnergyStatistic(
        DateTimeOffset Timestamp,
        double ChangeKwh);
}
