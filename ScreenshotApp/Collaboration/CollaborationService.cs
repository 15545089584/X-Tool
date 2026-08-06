using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using ScreenshotApp.ClipboardUi;

namespace ScreenshotApp.Collaboration;

/// <summary>协作中心剪贴板条目（内存保留最近 20 条，手机按序号增量拉取）。</summary>
public sealed record CollaborationClipboardEntry(long Seq, string Kind, string Text, string ImagePngBase64, DateTime CreatedAt);

/// <summary>电脑端局域网协作服务：配对、剪贴板桥与文件传输；普通权限监听任意网卡。</summary>
public sealed class CollaborationService
{
    public const int DefaultPort = 18120;
    private const int MaxRequestHeaderBytes = 8192;
    // 请求体上限：剪贴板文本与文件上传共用；单文件上限 512 MB，超出会连接失败。
    private const int MaxRequestBodyBytes = 512 * 1024 * 1024;
    private const int ClipboardHistoryCount = 20;
    private const int SessionLifetimeHours = 24;

    private readonly object _sync = new();
    private readonly List<CollaborationClipboardEntry> _clipboardHistory = new();
    private sealed record SessionInfo(DateTime Expires, DateTime LastSeen);
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _deviceSessions = new(StringComparer.Ordinal);
    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private long _clipboardSeq;
    private string? _pin;
    private string? _pageHtml;
    private static readonly string[] VirtualAdapterKeywords =
    {
        "virtual", "vmware", "virtualbox", "hyper-v", "vethernet", "wsl", "radmin", "inode",
        "tap", "tun", "loopback", "tunnel", "vpn", "bluetooth", "docker"
    };

    public static CollaborationService Instance { get; } = new();

    public bool IsRunning { get; private set; }

    /// <summary>手机文件上传成功后触发，参数为 (文件名, 字节数)。</summary>
    public event Action<string, long>? FileReceived;

    public int Port { get; private set; } = DefaultPort;

    public string Pin => _pin ?? string.Empty;

    public string? LocalIpAddress { get; private set; }

    /// <summary>电脑→手机剪贴板同步开关；手机→电脑不受影响。</summary>
    public bool ClipboardBridgeEnabled { get; set; } = true;

    public long ClipboardSeq
    {
        get { lock (_sync) { return _clipboardSeq; } }
    }

    /// <summary>最近一条进入剪贴板桥的条目，用于页面展示同步内容。</summary>
    public CollaborationClipboardEntry? LastClipboardEntry
    {
        get
        {
            lock (_sync)
            {
                return _clipboardHistory.LastOrDefault();
            }
        }
    }

    public string IncomingDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "X-Tool",
        "Transfer",
        "Incoming");

    public string OutgoingDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "X-Tool",
        "Transfer",
        "Outgoing");

    /// <summary>启动服务：生成配对 PIN 与配对 URL，开始接受局域网连接。</summary>
    public string Start(int port = DefaultPort)
    {
        if (IsRunning)
        {
            return $"http://{LocalIpAddress}:{Port}";
        }

        Port = port;
        Directory.CreateDirectory(IncomingDirectory);
        Directory.CreateDirectory(OutgoingDirectory);
        _pin = Random.Shared.Next(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        LocalIpAddress = ResolveLanIpAddress();
        _pageHtml = LoadEmbeddedPage();

        _acceptCts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        IsRunning = true;
        _ = AcceptLoopAsync(_acceptCts.Token);
        return $"http://{LocalIpAddress}:{Port}";
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }
        _acceptCts?.Cancel();
        _listener?.Stop();
        _listener = null;
        IsRunning = false;
    }

    /// <summary>重新生成配对 PIN；旧 PIN 立即失效，已配对的会话不受影响。</summary>
    public void RegeneratePin() => _pin = Random.Shared.Next(100000, 1000000).ToString(CultureInfo.InvariantCulture);

    /// <summary>当前活跃设备数：同一设备重复配对只计一台，网页会话按匿名计数。</summary>
    public int SessionCount
    {
        get
        {
            var now = DateTime.UtcNow;
            var cutoff = now.AddSeconds(-30);
            var stale = _sessions
                .Where(pair => pair.Value.Expires <= now || pair.Value.LastSeen < cutoff)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in stale)
            {
                _sessions.TryRemove(key, out _);
            }
            foreach (var pair in _deviceSessions.Where(pair => !_sessions.ContainsKey(pair.Value)).Select(pair => pair.Key).ToArray())
            {
                _deviceSessions.TryRemove(pair, out _);
            }
            var activeTokens = _sessions.Where(pair => pair.Value.LastSeen >= cutoff).Select(pair => pair.Key).ToHashSet();
            var deviceCount = _deviceSessions.Values.Count(token => activeTokens.Contains(token));
            var anonymousCount = activeTokens.Count(token => !_deviceSessions.Values.Contains(token));
            return deviceCount + anonymousCount;
        }
    }

    /// <summary>电脑端捕获到外部复制后调用：文本入桥，手机轮询即可拉取。</summary>
    public void PushClipboardText(string text)
    {
        if (!IsRunning || !ClipboardBridgeEnabled || string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        lock (_sync)
        {
            _clipboardSeq++;
            _clipboardHistory.Add(new CollaborationClipboardEntry(_clipboardSeq, "text", text, string.Empty, DateTime.Now));
            TrimClipboardHistory();
        }
    }

    /// <summary>电脑端捕获到外部复制后调用：图片转 PNG 入桥。</summary>
    public void PushClipboardImage(BitmapSource image)
    {
        if (!IsRunning || !ClipboardBridgeEnabled || image == null)
        {
            return;
        }
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            lock (_sync)
            {
                _clipboardSeq++;
                _clipboardHistory.Add(new CollaborationClipboardEntry(
                    _clipboardSeq,
                    "image",
                    string.Empty,
                    Convert.ToBase64String(stream.ToArray()),
                    DateTime.Now));
                TrimClipboardHistory();
            }
        }
        catch
        {
            // 图片编码失败不阻断其它协作能力。
        }
    }

    private void TrimClipboardHistory()
    {
        while (_clipboardHistory.Count > ClipboardHistoryCount)
        {
            _clipboardHistory.RemoveAt(0);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                continue;
            }
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = 15000;
                stream.WriteTimeout = 60000;
                var request = await ReadRequestAsync(stream);
                if (request == null)
                {
                    return;
                }
                await DispatchAsync(stream, request);
            }
        }
        catch
        {
            // 单个客户端异常不影响服务。
        }
    }

    private sealed record HttpRequest(string Method, string Path, string Query, Dictionary<string, string> Headers, byte[]? Body);

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var headerBytes = new MemoryStream();
        var buffer = new byte[1024];
        var headerEnd = -1;
        while (headerBytes.Length < MaxRequestHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return null;
            }
            headerBytes.Write(buffer, 0, read);
            var raw = headerBytes.ToArray();
            for (var i = 0; i < raw.Length - 3; i++)
            {
                if (raw[i] == 13 && raw[i + 1] == 10 && raw[i + 2] == 13 && raw[i + 3] == 10)
                {
                    headerEnd = i;
                    break;
                }
            }
            if (headerEnd >= 0)
            {
                break;
            }
        }
        if (headerEnd < 0)
        {
            return null;
        }

        var headerText = Encoding.UTF8.GetString(headerBytes.ToArray(), 0, headerEnd);
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }
        var requestParts = lines[0].Split(' ');
        if (requestParts.Length < 2)
        {
            return null;
        }
        var method = requestParts[0];
        var target = requestParts[1];
        var queryIndex = target.IndexOf('?');
        var path = queryIndex >= 0 ? target.Substring(0, queryIndex) : target;
        var query = queryIndex >= 0 ? target.Substring(queryIndex + 1) : string.Empty;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
        }

        byte[]? body = null;
        if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length) && length > 0)
        {
            var total = (int)headerBytes.Length - headerEnd - 4;
            var bodyStart = headerEnd + 4;
            var existing = headerBytes.ToArray();
            var remaining = length - total;
            if (remaining > 0)
            {
                if (remaining > MaxRequestBodyBytes)
                {
                    return null;
                }
                var rest = new byte[remaining];
                var read = 0;
                while (read < remaining)
                {
                    var chunk = await stream.ReadAsync(rest, read, remaining - read);
                    if (chunk <= 0)
                    {
                        return null;
                    }
                    read += chunk;
                }
                body = new byte[length];
                Array.Copy(existing, bodyStart, body, 0, total);
                Array.Copy(rest, 0, body, total, remaining);
            }
            else
            {
                body = new byte[length];
                Array.Copy(existing, bodyStart, body, 0, length);
            }
        }

        return new HttpRequest(method, path, query, headers, body);
    }

    private async Task DispatchAsync(NetworkStream stream, HttpRequest request)
    {
        try
        {
            var path = request.Path.ToLowerInvariant();
            var query = ParseQuery(request.Query);

            if (path == "/pair" && request.Method == "GET")
            {
                if (!IsRunning || query.GetValueOrDefault("pin") != _pin)
                {
                    await WriteTextAsync(stream, 401, "text/plain; charset=utf-8", "配对 PIN 不正确");
                    return;
                }
                var token = Guid.NewGuid().ToString("N");
                RegisterSession(token, query.GetValueOrDefault("device"));
                var html = (_pageHtml ?? string.Empty)
                    .Replace("__TOKEN__", token)
                    .Replace("__HOST__", $"http://{LocalIpAddress}:{Port}");
                await WriteBytesAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
                return;
            }

            // 手机原生 App 使用的 JSON 配对端点；网页端仍走 /pair 返回页面。
            if (path == "/api/pair" && request.Method == "GET")
            {
                if (!IsRunning || query.GetValueOrDefault("pin") != _pin)
                {
                    await WriteJsonAsync(stream, 401, new { ok = false, error = "配对 PIN 不正确" });
                    return;
                }
                var token = Guid.NewGuid().ToString("N");
                RegisterSession(token, query.GetValueOrDefault("device"));
                await WriteJsonAsync(stream, 200, new { ok = true, token, host = $"{LocalIpAddress}:{Port}" });
                return;
            }

            if (!IsValidSession(query.GetValueOrDefault("t")))
            {
                await WriteJsonAsync(stream, 401, new { error = "未授权或会话已过期" });
                return;
            }

            if (path == "/api/logout")
            {
                var token = query.GetValueOrDefault("t");
                if (!string.IsNullOrEmpty(token))
                {
                    _sessions.TryRemove(token, out _);
                    foreach (var pair in _deviceSessions.Where(pair => pair.Value == token).Select(pair => pair.Key).ToArray())
                    {
                        _deviceSessions.TryRemove(pair, out _);
                    }
                }
                await WriteJsonAsync(stream, 200, new { ok = true });
                return;
            }

            if (path == "/")
            {
                await WriteTextAsync(stream, 200, "text/plain; charset=utf-8", "X-Tool 协作中心：请使用手机扫描电脑端二维码完成配对。");
                return;
            }

            if (path == "/api/status")
            {
                object statusPayload;
                lock (_sync)
                {
                    var latest = _clipboardHistory.LastOrDefault();
                    statusPayload = new
                    {
                        ok = true,
                        serverTime = DateTime.Now.ToString("O"),
                        clipboardSeq = _clipboardSeq,
                        latestKind = latest?.Kind,
                        latestText = latest?.Kind == "text" ? latest.Text : string.Empty,
                        latestImage = latest?.Kind == "image" ? latest.ImagePngBase64 : string.Empty,
                        latestAt = latest?.CreatedAt.ToString("O")
                    };
                }
                await WriteJsonAsync(stream, 200, statusPayload);
                return;
            }

            if (path == "/api/clipboard/pull" && request.Method == "GET")
            {
                var since = long.TryParse(query.GetValueOrDefault("since"), out var sinceValue) ? sinceValue : 0;
                object pullPayload;
                lock (_sync)
                {
                    var entries = _clipboardHistory
                        .Where(entry => entry.Seq > since)
                        .Select(entry => new
                        {
                            seq = entry.Seq,
                            kind = entry.Kind,
                            text = entry.Text,
                            image = entry.ImagePngBase64,
                            at = entry.CreatedAt.ToString("O")
                        })
                        .ToArray();
                    pullPayload = new { ok = true, currentSeq = _clipboardSeq, entries };
                }
                await WriteJsonAsync(stream, 200, pullPayload);
                return;
            }

            if (path == "/api/clipboard/push" && request.Method == "POST")
            {
                var contentType = request.Headers.GetValueOrDefault("Content-Type") ?? string.Empty;
                if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase) && request.Body != null)
                {
                    var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(request.Body);
                    var text = payload != null && payload.TryGetValue("text", out var value) ? value : string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        await WriteClipboardFromMobileAsync(text, imageBytes: null);
                        await WriteJsonAsync(stream, 200, new { ok = true });
                        return;
                    }
                }
                else if (contentType.Contains("image/png", StringComparison.OrdinalIgnoreCase) && request.Body is { Length: > 0 })
                {
                    await WriteClipboardFromMobileAsync(null, request.Body);
                    await WriteJsonAsync(stream, 200, new { ok = true });
                    return;
                }
                await WriteJsonAsync(stream, 400, new { error = "不支持的剪贴板内容" });
                return;
            }

            if (path == "/api/files/upload" && request.Method == "PUT")
            {
                var name = SanitizeFileName(query.GetValueOrDefault("name"));
                if (string.IsNullOrWhiteSpace(name) || request.Body == null)
                {
                    await WriteJsonAsync(stream, 400, new { error = "缺少文件名或内容" });
                    return;
                }
                var target = UniquePath(Path.Combine(IncomingDirectory, name));
                await File.WriteAllBytesAsync(target, request.Body);
                FileReceived?.Invoke(Path.GetFileName(target), request.Body.LongLength);
                await WriteJsonAsync(stream, 200, new { ok = true, name = Path.GetFileName(target) });
                return;
            }

            if (path == "/api/files/list" && request.Method == "GET")
            {
                var dir = query.GetValueOrDefault("dir") == "outgoing" ? OutgoingDirectory : IncomingDirectory;
                var files = Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir)
                        .Select(file => new FileInfo(file))
                        .OrderByDescending(info => info.LastWriteTime)
                        .Select(info => new { name = info.Name, size = info.Length, at = info.LastWriteTime.ToString("O") })
                        .ToArray()
                    : Array.Empty<object>();
                await WriteJsonAsync(stream, 200, new { ok = true, files });
                return;
            }

            if (path == "/api/files/download" && request.Method == "GET")
            {
                var dir = query.GetValueOrDefault("dir") == "outgoing" ? OutgoingDirectory : IncomingDirectory;
                var name = SanitizeFileName(query.GetValueOrDefault("name"));
                var filePath = Path.Combine(dir, name);
                if (!File.Exists(filePath))
                {
                    await WriteJsonAsync(stream, 404, new { error = "文件不存在" });
                    return;
                }
                var bytes = await File.ReadAllBytesAsync(filePath);
                await WriteBytesAsync(stream, 200, "application/octet-stream", bytes);
                return;
            }

            await WriteJsonAsync(stream, 404, new { error = "接口不存在" });
        }
        catch (Exception exception)
        {
            try
            {
                await WriteJsonAsync(stream, 500, new { error = exception.Message });
            }
            catch
            {
                // 响应写入失败时连接已不可用。
            }
        }
    }

    private async Task WriteClipboardFromMobileAsync(string? text, byte[]? imageBytes)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }
        await dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (text != null)
                {
                    ClipboardService.SetText(text, recordToHistory: true);
                }
                else if (imageBytes is { Length: > 0 })
                {
                    using var stream = new MemoryStream(imageBytes);
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    ClipboardService.SetImage(image, recordToHistory: true);
                }
            }
            catch
            {
                // 剪贴板可能被占用，手机端下次可重试。
            }
        });
    }

    private bool IsValidSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }
        var now = DateTime.UtcNow;
        if (_sessions.TryGetValue(token, out var info) && info.Expires > now)
        {
            _sessions[token] = info with { LastSeen = now };
            return true;
        }
        _sessions.TryRemove(token, out _);
        return false;
    }

    /// <summary>注册配对会话；带设备 ID 时同一设备重复配对替换旧会话，不重复计数。</summary>
    private void RegisterSession(string token, string? deviceId)
    {
        _sessions[token] = new SessionInfo(DateTime.UtcNow.AddHours(SessionLifetimeHours), DateTime.UtcNow);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }
        if (_deviceSessions.TryGetValue(deviceId, out var oldToken))
        {
            _sessions.TryRemove(oldToken, out _);
        }
        _deviceSessions[deviceId] = token;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            if (index > 0)
            {
                result[Uri.UnescapeDataString(part.Substring(0, index))] = Uri.UnescapeDataString(part.Substring(index + 1));
            }
        }
        return result;
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }
        return builder.ToString();
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name}_{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string? ResolveLanIpAddress()
    {
        try
        {
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    continue;
                }
                var type = adapter.NetworkInterfaceType;
                if (type is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                {
                    continue;
                }
                if (VirtualAdapterKeywords.Any(keyword => adapter.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    // 虚拟网卡、VPN、Hyper-V 等地址手机不可达，跳过。
                    continue;
                }
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = unicast.Address.ToString();
                        if (!ip.StartsWith("169.254", StringComparison.Ordinal))
                        {
                            return ip;
                        }
                    }
                }
            }
        }
        catch
        {
            // 找不到时由调用方降级显示。
        }
        return "127.0.0.1";
    }

    private static string? LoadEmbeddedPage()
    {
        try
        {
            var assembly = typeof(CollaborationService).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("CollaborationPage.html", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
            {
                return null;
            }
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return null;
            }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteTextAsync(NetworkStream stream, int status, string contentType, string text) =>
        await WriteBytesAsync(stream, status, contentType, Encoding.UTF8.GetBytes(text));

    private static async Task WriteJsonAsync(NetworkStream stream, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await WriteBytesAsync(stream, status, "application/json; charset=utf-8", bytes);
    }

    private static async Task WriteBytesAsync(NetworkStream stream, int status, string contentType, byte[] body)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            500 => "Internal Server Error",
            _ => "OK"
        };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\nAccess-Control-Allow-Origin: *\r\n\r\n";
        var headBytes = Encoding.UTF8.GetBytes(head);
        await stream.WriteAsync(headBytes, 0, headBytes.Length);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, 0, body.Length);
        }
        await stream.FlushAsync();
    }
}
