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
        "light", "switch", "climate", "cover", "fan", "sensor", "binary_sensor"
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

        SnapshotChanged?.Invoke(CurrentSnapshot);
    }

    public async Task ExecuteAsync(SmartHomeControlRequest request, CancellationToken cancellationToken)
    {
        var domain = GetDomain(request.EntityId);
        var service = request.Action switch
        {
            SmartHomeControlAction.TurnOn => "turn_on",
            SmartHomeControlAction.TurnOff => "turn_off",
            SmartHomeControlAction.SetBrightness => "turn_on",
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
            _ => throw new ArgumentOutOfRangeException(nameof(request), "不支持的智能家居控制操作。")
        };

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entity_id"] = request.EntityId
        };
        switch (request.Action)
        {
            case SmartHomeControlAction.SetBrightness:
                body["brightness_pct"] = Math.Clamp((int)Math.Round(request.NumericValue ?? 0), 0, 100);
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
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"api/services/{domain}/{service}", body, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new HomeAssistantAuthenticationException("Home Assistant Access Token 已失效。");
        }
        response.EnsureSuccessStatusCode();
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
            var areaId = primary.AreaId ?? device?.AreaId;
            smartDevices.Add(new SmartDevice
            {
                Id = pair.Key,
                Name = string.IsNullOrWhiteSpace(device?.Name) ? primary.Name : device.Name,
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
        "climate" => 0,
        "cover" => 1,
        "fan" => 2,
        "light" => 3,
        "switch" => 4,
        "binary_sensor" => 5,
        "sensor" => 6,
        _ => 100
    };

    private static int EntityPriority(SmartEntity entity)
    {
        var priority = DomainPriority(entity.Domain) * 10;
        if (entity.Domain != "sensor") return priority;
        var deviceClass = entity.GetText("device_class");
        var unit = entity.GetText("unit_of_measurement");
        if (deviceClass == "temperature" || unit is "℃" or "°C") return priority;
        if (deviceClass == "humidity" || unit == "%") return priority + 1;
        return priority + 2;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool HasNonNullProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    public async ValueTask DisposeAsync()
    {
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
}
