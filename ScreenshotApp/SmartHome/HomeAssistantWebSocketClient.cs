using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;

namespace ScreenshotApp.SmartHome;

internal sealed class HomeAssistantWebSocketClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingCommands = new();
    private CancellationTokenSource? _lifetimeSource;
    private Task? _receiveLoop;
    private int _nextCommandId;
    private bool _intentionalClose;

    public event Action<JsonElement>? EventReceived;

    public event Action<Exception?>? Disconnected;

    public async Task ConnectAsync(Uri serverUri, string token, CancellationToken cancellationToken)
    {
        var webSocketUri = new UriBuilder(serverUri)
        {
            Scheme = serverUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = CombinePath(serverUri.AbsolutePath, "api/websocket"),
            Query = string.Empty
        }.Uri;

        using var connectSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectSource.CancelAfter(TimeSpan.FromSeconds(15));
        await _socket.ConnectAsync(webSocketUri, connectSource.Token).ConfigureAwait(false);

        var authRequired = await ReceiveMessageAsync(connectSource.Token).ConfigureAwait(false);
        var authType = GetString(authRequired, "type");
        if (!string.Equals(authType, "auth_required", StringComparison.Ordinal) &&
            !string.Equals(authType, "auth_ok", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Home Assistant WebSocket 未返回预期的认证握手消息。");
        }

        if (authType == "auth_required")
        {
            await SendPayloadAsync(new Dictionary<string, object?>
            {
                ["type"] = "auth",
                ["access_token"] = token
            }, connectSource.Token).ConfigureAwait(false);

            var authResult = await ReceiveMessageAsync(connectSource.Token).ConfigureAwait(false);
            var resultType = GetString(authResult, "type");
            if (string.Equals(resultType, "auth_invalid", StringComparison.Ordinal))
            {
                throw new HomeAssistantAuthenticationException("Home Assistant 拒绝了 Access Token。");
            }
            if (!string.Equals(resultType, "auth_ok", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Home Assistant WebSocket 认证未完成。");
            }
        }

        _lifetimeSource = new CancellationTokenSource();
        _receiveLoop = ReceiveLoopAsync(_lifetimeSource.Token);
    }

    public async Task<JsonElement> SendCommandAsync(
        string type,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Home Assistant WebSocket 尚未连接。");
        }

        var commandId = Interlocked.Increment(ref _nextCommandId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingCommands.TryAdd(commandId, completion))
        {
            throw new InvalidOperationException("无法登记 Home Assistant WebSocket 请求。");
        }

        try
        {
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = commandId,
                ["type"] = type
            };
            if (arguments is not null)
            {
                foreach (var argument in arguments) payload[argument.Key] = argument.Value;
            }

            await SendPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingCommands.TryRemove(commandId, out _);
        }
    }

    private async Task SendPayloadAsync(object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
                var type = GetString(message, "type");
                if (string.Equals(type, "result", StringComparison.Ordinal) &&
                    message.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var commandId) &&
                    _pendingCommands.TryRemove(commandId, out var completion))
                {
                    var success = !message.TryGetProperty("success", out var successElement) || successElement.GetBoolean();
                    if (success)
                    {
                        completion.TrySetResult(message.TryGetProperty("result", out var result)
                            ? result.Clone()
                            : JsonDocument.Parse("null").RootElement.Clone());
                    }
                    else
                    {
                        var error = message.TryGetProperty("error", out var errorElement)
                            ? errorElement.ToString()
                            : "Home Assistant WebSocket 命令执行失败。";
                        completion.TrySetException(new InvalidOperationException(error));
                    }
                    continue;
                }

                if (string.Equals(type, "event", StringComparison.Ordinal))
                {
                    EventReceived?.Invoke(message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常关闭。
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            var finalException = failure ?? new IOException("Home Assistant WebSocket 连接已关闭。");
            foreach (var completion in _pendingCommands.Values)
            {
                completion.TrySetException(finalException);
            }
            _pendingCommands.Clear();
            if (!_intentionalClose) Disconnected?.Invoke(failure);
        }
    }

    private async Task<JsonElement> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException("Home Assistant WebSocket 主动关闭了连接。");
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidOperationException("收到无法识别的 Home Assistant WebSocket 消息。");
            }
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        _intentionalClose = true;
        _lifetimeSource?.Cancel();
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "X-Tool 智能家居断开连接",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 连接已经中断时无需再次报告关闭错误。
            }
        }

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { }
        }
        _lifetimeSource?.Dispose();
        _sendGate.Dispose();
        _socket.Dispose();
    }

    private static string CombinePath(string basePath, string relativePath) =>
        $"{basePath.TrimEnd('/')}/{relativePath.TrimStart('/')}";

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed class HomeAssistantAuthenticationException(string message) : Exception(message);
