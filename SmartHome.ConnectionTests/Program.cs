using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ScreenshotApp.SmartHome;

// 独立测试入口，不启动 XTool、不下发设备控制、不写入设置或凭据。
if (args.Contains("--live-registries"))
{
    var settings = SmartHomeSettingsStore.Load();
    var token = SmartHomeCredentialStore.ReadToken() ?? throw new InvalidOperationException("缺少 HA 凭据");
    await using var socket = new HomeAssistantWebSocketClient();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    await socket.ConnectAsync(SmartHomeSettingsStore.NormalizeServerUri(settings.ServerUrl), token, deadline.Token);
    foreach (var command in new[] { "config/area_registry/list", "config/device_registry/list", "config/entity_registry/list" })
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await socket.SendCommandAsync(command, null, deadline.Token, TimeSpan.FromSeconds(60));
            Console.WriteLine($"{command}: {result.ValueKind}, count={(result.ValueKind == JsonValueKind.Array ? result.GetArrayLength() : -1)}, {watch.Elapsed.TotalSeconds:F1}s");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"{command}: {exception.GetType().Name}, {exception.Message}, {watch.Elapsed.TotalSeconds:F1}s");
            Environment.ExitCode = 1;
        }
    }
    return;
}

if (args.Contains("--live-ha"))
{
    var settings = SmartHomeSettingsStore.Load();
    var token = SmartHomeCredentialStore.ReadToken() ?? throw new InvalidOperationException("缺少 HA 凭据");
    await using var provider = new HomeAssistantProvider(SmartHomeSettingsStore.NormalizeServerUri(settings.ServerUrl), token);
    using var deadline = new CancellationTokenSource(HomeAssistantConnectionPolicy.InitializationTimeout);
    var liveWatch = Stopwatch.StartNew();
    try
    {
        await provider.ConnectAsync(deadline.Token);
        var snapshot = provider.CurrentSnapshot ?? throw new InvalidOperationException("缺少设备快照");
        Console.WriteLine($"设备={snapshot.Devices.Count}，已分配房间设备={snapshot.Devices.Count(device => device.AreaId is not null)}，独立实体卡片={snapshot.Devices.Count(device => device.Id.StartsWith("entity:", StringComparison.Ordinal))}");
        Console.WriteLine($"PASS 真实 HA REST、WebSocket 认证、元数据和状态读取（未下发控制），耗时 {liveWatch.Elapsed.TotalSeconds:F1} 秒");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FAIL 真实 HA 连接：{exception.GetType().Name}，耗时 {liveWatch.Elapsed.TotalSeconds:F1} 秒");
        Environment.ExitCode = 1;
    }
    return;
}

var passed = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    passed++;
    Console.WriteLine($"PASS {message}");
}

foreach (var host in new[] { "xtool-homeassistant.tailc2dbae.ts.net", "HA.TAIL123.TS.NET.", "100.64.0.1", "100.127.255.254", "192.168.1.1", "10.0.0.1", "172.16.0.1", "localhost", "[::1]", "[fd7a:115c:a1e0::1]" })
    Check(HomeAssistantConnectionPolicy.BypassProxy(new Uri($"http://{host}:8123")), $"私网直连 {host}");
foreach (var host in new[] { "ha.example.com", "100.63.255.255", "100.128.0.1", "172.32.0.1", "example.ts.net.evil.test" })
    Check(!HomeAssistantConnectionPolicy.BypassProxy(new Uri($"https://{host}")), $"保留公网代理 {host}");

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
app.UseWebSockets();
var mode = "ok";
var failedRegistry = "config/entity_registry/list";
var activeSockets = 0;
app.MapGet("/api/", () => Results.Json(new { message = "API running." }));
app.MapGet("/api/config", () => Results.Json(new { unit_system = new { temperature = "°C" } }));
app.MapGet("/api/states", () => Results.Json(Array.Empty<object>()));
app.Map("/api/websocket", async context =>
{
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    Interlocked.Increment(ref activeSockets);
    try
    {
        if (mode == "stall")
        {
            var buffer = new byte[1024];
            await socket.ReceiveAsync(buffer, context.RequestAborted);
            return;
        }
        await Send(socket, new { type = "auth_required" }, context.RequestAborted);
        await Read(socket, context.RequestAborted);
        if (mode == "reject")
        {
            await Send(socket, new { type = "auth_invalid" }, context.RequestAborted);
            await Read(socket, context.RequestAborted);
            return;
        }
        await Send(socket, new { type = "auth_ok" }, context.RequestAborted);
        while (socket.State == WebSocketState.Open)
        {
            var command = await Read(socket, context.RequestAborted);
            if (mode == "no-reply") continue;
            var type = command.GetProperty("type").GetString()!;
            var id = command.GetProperty("id").GetInt32();
            if (type == failedRegistry && mode == "registry-stall") continue;
            if (type == failedRegistry && mode == "registry-error")
            {
                await Send(socket, new { id, type = "result", success = false, error = new { code = "test_error" } }, context.RequestAborted);
                continue;
            }
            var result = type == failedRegistry && mode == "registry-null" ? JsonSerializer.SerializeToElement<object?>(null) : Fixture(type);
            await Send(socket, new { id, type = "result", success = true, result }, context.RequestAborted);
        }
    }
    catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException) { }
    finally { Interlocked.Decrement(ref activeSockets); }
});
await app.StartAsync();
var server = new Uri(app.Urls.Single());
var originalProxy = HttpClient.DefaultProxy;
HttpClient.DefaultProxy = new WebProxy("http://127.0.0.1:1");
try
{
    await using (var provider = new HomeAssistantProvider(server, "test-token"))
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await provider.ConnectAsync(timeout.Token);
        Check(provider.CurrentSnapshot is not null, "错误系统代理下 REST 与 WebSocket 仍可私网连接");
    }
    await WaitFor(() => Volatile.Read(ref activeSockets) == 0);
    Check(activeSockets == 0, "正常释放连接后服务端无残留套接字");

    await using (var provider = new HomeAssistantProvider(server, "test-token"))
    {
        await provider.ConnectAsync(CancellationToken.None);
        var snapshot = provider.CurrentSnapshot!;
        Check(snapshot.Devices.Count == 1 && snapshot.Devices[0].Entities.Count == 3,
            "同一风扇的电源、提示音和指示灯聚合为一台设备");
        Check(snapshot.Devices[0].Id == "fan-device" && snapshot.Devices[0].AreaName == "卧室",
            "设备保留真实归属与房间，不回退为独立实体");
        var oldCache = JsonSerializer.Serialize(new SmartHomeCacheEnvelope(server.ToString(), snapshot));
        var verifiedCache = JsonSerializer.Serialize(new SmartHomeCacheEnvelope(server.ToString(), snapshot, 1));
        Check(SmartHomeCacheStore.ReadVerifiedSnapshot(oldCache, server.ToString()) is null,
            "忽略旧版未经归属完整性校验的缓存");
        Check(SmartHomeCacheStore.ReadVerifiedSnapshot(verifiedCache, server.ToString())?.Devices.Count == 1,
            "新缓存保留验证后的设备聚合");
    }
    foreach (var registry in new[] { "config/area_registry/list", "config/device_registry/list", "config/entity_registry/list" })
    foreach (var failureMode in new[] { "registry-error", "registry-null", "registry-stall" })
    {
        failedRegistry = registry;
        mode = failureMode;
        await using var provider = new HomeAssistantProvider(server, "test-token");
        var published = 0;
        provider.SnapshotChanged += _ => published++;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try { await provider.ConnectAsync(deadline.Token); throw new Exception("应拒绝不完整归属"); }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException) { }
        Check(provider.CurrentSnapshot is null && published == 0, $"{registry}/{failureMode} 不发布错误设备列表");
    }
    mode = "ok";
    await using (var recovered = new HomeAssistantProvider(server, "test-token"))
    {
        await recovered.ConnectAsync(CancellationToken.None);
        Check(recovered.CurrentSnapshot?.Devices.Count == 1, "故障恢复后重新加载完整归属并恢复正确列表");
    }

    mode = "reject";
    for (var i = 0; i < 20; i++)
    {
        await using var provider = new HomeAssistantProvider(server, "invalid-test-token");
        try { await provider.ConnectAsync(CancellationToken.None); throw new Exception("应拒绝认证"); }
        catch (HomeAssistantAuthenticationException) { }
    }
    await WaitFor(() => Volatile.Read(ref activeSockets) == 0);
    Check(activeSockets == 0, "连续 20 次握手失败均释放套接字");

    mode = "stall";
    var watch = Stopwatch.StartNew();
    await using (var provider = new HomeAssistantProvider(server, "test-token"))
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await provider.ConnectAsync(timeout.Token); throw new Exception("应超时"); }
        catch (OperationCanceledException) { }
    }
    Check(watch.Elapsed < TimeSpan.FromSeconds(2), "握手不应答时取消和释放有界");
    await WaitFor(() => Volatile.Read(ref activeSockets) == 0);

    mode = "no-reply";
    await using (var socket = new HomeAssistantWebSocketClient())
    {
        await socket.ConnectAsync(server, "test-token", CancellationToken.None);
        var command = socket.SendCommandAsync("get_states", null, CancellationToken.None);
        await socket.DisposeAsync();
        try { await command.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("应取消命令"); }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or WebSocketException) { }
        Check(true, "释放连接会结束未应答命令，重复释放安全");
    }

    var attempts = 0;
    var disposed = 0;
    var behavior = "ok";
    FakeProvider? latestProvider = null;
    var service = new SmartHomeService((_, _) =>
    {
        var thisAttempt = Interlocked.Increment(ref attempts);
        return latestProvider = new FakeProvider(async ct =>
        {
            if (behavior == "timeout") await Task.Delay(Timeout.Infinite, ct);
            if (behavior == "auth") throw new HomeAssistantAuthenticationException("测试认证失败");
            if (thisAttempt == 2) throw new HttpRequestException("测试瞬断");
        }, () => Interlocked.Increment(ref disposed));
    }, () => "test-token", TimeSpan.FromMilliseconds(150), [TimeSpan.FromMilliseconds(50)]);
    try
    {
        await service.ConnectAsync(server.ToString(), "test-token", persist: false);
        try { await service.RefreshAsync(); throw new Exception("应瞬断"); }
        catch (HttpRequestException) { }
        await WaitFor(() => attempts >= 3 && service.ConnectionState == SmartHomeConnectionState.Connected);
        Check(attempts == 3 && disposed == 2, "刷新失败恢复自动重试，旧连接和失败连接均释放");

        behavior = "timeout";
        try { await service.RefreshAsync(); throw new Exception("应超时"); }
        catch (TimeoutException) { }
        await WaitFor(() => attempts >= 5);
        behavior = "ok";
        await service.ConnectAsync(server.ToString(), "test-token", persist: false);
        var afterManual = attempts;
        await Task.Delay(250);
        Check(attempts == afterManual && service.ConnectionState == SmartHomeConnectionState.Connected,
            "单次连接总超时可恢复，手动连接取消并等待旧重试退出");

        behavior = "timeout";
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
        {
            try { await service.RefreshAsync(cancelled.Token); throw new Exception("应取消"); }
            catch (OperationCanceledException) { }
        }
        var afterCancel = attempts;
        await Task.Delay(150);
        Check(attempts == afterCancel && service.ConnectionState == SmartHomeConnectionState.Disconnected,
            "用户取消后不启动自动重试");
        behavior = "ok";
        await service.ConnectAsync(server.ToString(), "test-token", persist: false);
        behavior = "auth";
        latestProvider!.Disconnect();
        await WaitFor(() => service.ConnectionState == SmartHomeConnectionState.AuthenticationFailed);
        var afterReconnectAuth = attempts;
        await Task.Delay(150);
        Check(attempts == afterReconnectAuth, "自动重连遇到认证失败后停止，不循环提交无效令牌");

        behavior = "auth";
        try { await service.RefreshAsync(); throw new Exception("应认证失败"); }
        catch (HomeAssistantAuthenticationException) { }
        var afterAuth = attempts;
        await Task.Delay(200);
        Check(attempts == afterAuth && service.ConnectionState == SmartHomeConnectionState.AuthenticationFailed,
            "无效令牌停止自动重试");
    }
    finally
    {
        // 仅终止测试实例的后台任务，不使用会删除真实配置的 ForgetAsync。
        await (Task)typeof(SmartHomeService).GetMethod("CancelReconnectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, null)!;
    }
}
finally
{
    HttpClient.DefaultProxy = originalProxy;
    await app.StopAsync();
}
Console.WriteLine($"全部通过：{passed} 项");

static async Task WaitFor(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition()) await Task.Delay(20, timeout.Token);
}

static async Task Send(WebSocket socket, object payload, CancellationToken ct) =>
    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, ct);

static JsonElement Fixture(string command) => JsonSerializer.Deserialize<JsonElement>(command switch
{
    "config/area_registry/list" => """[{"area_id":"bedroom","name":"卧室"}]""",
    "config/device_registry/list" => """[{"id":"fan-device","name":"风扇","area_id":"bedroom","manufacturer":"测试厂商"}]""",
    "config/entity_registry/list" => """[{"entity_id":"fan.main","device_id":"fan-device"},{"entity_id":"switch.beep","device_id":"fan-device"},{"entity_id":"switch.indicator","device_id":"fan-device"}]""",
    "get_states" => """[{"entity_id":"fan.main","state":"on","attributes":{"friendly_name":"风扇"}},{"entity_id":"switch.beep","state":"on","attributes":{"friendly_name":"风扇 提示音"}},{"entity_id":"switch.indicator","state":"on","attributes":{"friendly_name":"风扇 指示灯"}}]""",
    _ => "[]"
});

static async Task<JsonElement> Read(WebSocket socket, CancellationToken ct)
{
    var buffer = new byte[16384];
    var result = await socket.ReceiveAsync(buffer, ct);
    if (result.MessageType == WebSocketMessageType.Close) throw new IOException("关闭");
    using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
    return document.RootElement.Clone();
}

sealed class FakeProvider(Func<CancellationToken, Task> connect, Action dispose) : ISmartHomeProvider
{
    public event Action<SmartHomeSnapshot>? SnapshotChanged { add { } remove { } }
    public event Action<Exception?>? Disconnected;
    public void Disconnect() => Disconnected?.Invoke(new IOException("测试断线"));
    public SmartHomeSnapshot? CurrentSnapshot => null;
    public Task ConnectAsync(CancellationToken ct) => connect(ct);
    public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
    public Task ExecuteAsync(SmartHomeControlRequest request, CancellationToken ct) => throw new NotSupportedException();
    public Task RenameDeviceAsync(string id, string name, CancellationToken ct) => throw new NotSupportedException();
    public Task<SmartDeviceInsights> GetInsightsAsync(SmartDevice device, int month, int year, CancellationToken ct) => throw new NotSupportedException();
}
