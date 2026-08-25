using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using ScreenshotApp.ClipboardUi;

namespace ScreenshotApp.Collaboration;

/// <summary>协作中心剪贴板条目（内存保留最近 20 条，手机按序号增量拉取）。</summary>
public sealed record CollaborationClipboardEntry(long Seq, string Kind, string Text, string ImagePngBase64, DateTime CreatedAt);

/// <summary>桌面视角下的单次文件传输状态。</summary>
public sealed record CollaborationTransferProgress(
    string TransferId,
    string FileName,
    string Direction,
    long TransferredBytes,
    long TotalBytes,
    string State,
    string Message,
    DateTime UpdatedAt)
{
    public double Percentage => TotalBytes <= 0 ? 0 : Math.Clamp(TransferredBytes * 100d / TotalBytes, 0, 100);
}

/// <summary>已加入电脑发送队列的文件及其传输标识。</summary>
public sealed record CollaborationQueuedTransfer(string TransferId, string QueuePath, string FileName);

/// <summary>已信任设备的持久会话。令牌只保存在本机与对应手机，不向局域网广播。</summary>
internal sealed record CollaborationTrustedDevice(string DeviceId, string Token, DateTime ExpiresAt, string DeviceName);
internal sealed record CollaborationConnectedDevice(string DeviceName, string IpAddress);

/// <summary>电脑端一次性发送队列清单；只有明确加入清单的文件才会被手机拉取。</summary>
internal sealed record CollaborationQueuedFile(string FileName, string TransferId, DateTime QueuedAt);

/// <summary>电脑端局域网协作服务：可信配对、自动发现与文件传输；普通权限监听任意网卡。</summary>
public sealed class CollaborationService
{
    public const int DefaultPort = 18120;
    public const long MaxTransferFileBytes = 5L * 1024 * 1024 * 1024;
    private const int MaxRequestHeaderBytes = 8192;
    // 文件上传采用流式落盘；手机上传单文件上限 5 GB，剪贴板仍使用更严格的独立上限。
    private const long MaxRequestBodyBytes = MaxTransferFileBytes;
    private const int MaxClipboardBodyBytes = 16 * 1024 * 1024;
    private const int MaxClipboardTextBodyBytes = 1024 * 1024;
    private const long MaxClipboardImagePixels = 16_000_000;
    private const int MaxConcurrentClients = 8;
    private const int TransferBufferBytes = 1024 * 1024;
    private const int TransferProgressIntervalMilliseconds = 125;
    private const long MaxIncomingDirectoryBytes = 10L * 1024 * 1024 * 1024;
    private const long MinimumFreeSpaceBytes = 1024L * 1024 * 1024;
    private const int ClipboardHistoryCount = 20;
    private const int SessionLifetimeDays = 180;
    public const int DiscoveryPort = 18121;
    private const string DiscoveryRequest = "XTOOL_DISCOVER_V1";

    private readonly object _sync = new();
    private readonly List<CollaborationClipboardEntry> _clipboardHistory = new();
    private sealed record SessionInfo(
        DateTime Expires,
        DateTime LastSeen,
        string DeviceId,
        string DeviceName,
        string RemoteIpAddress);
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _deviceSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _outgoingTransferIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _transferOpenPaths = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ParallelDownloadProgress> _parallelDownloadProgress = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PairAttemptInfo> _pairAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _clientGate = new(MaxConcurrentClients, MaxConcurrentClients);
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private readonly object _pairAttemptSync = new();
    private readonly object _outgoingQueueSync = new();
    private sealed record PairAttemptInfo(int Failures, DateTime BlockedUntil, DateTime LastAttempt);
    private sealed class ParallelDownloadProgress(long totalBytes)
    {
        internal object SyncRoot { get; } = new();
        internal Dictionary<long, long> SegmentBytes { get; } = new();
        internal long TotalBytes { get; } = totalBytes;
        internal long LastReportAtMilliseconds { get; set; }
    }
    private TcpListener? _listener;
    private UdpClient? _discoveryListener;
    private CancellationTokenSource? _acceptCts;
    private long _clipboardSeq;
    private string? _pin;
    private string? _pageHtml;
    private readonly string _serverId = LoadOrCreateServerId();
    private static readonly string TrustStorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "X-Tool", "Collaboration", "trusted-devices.json");
    private static readonly string OutgoingQueuePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "X-Tool", "Collaboration", "outgoing-queue.json");
    private static readonly string[] VirtualAdapterKeywords =
    {
        "virtual", "vmware", "virtualbox", "hyper-v", "vethernet", "wsl", "radmin", "inode",
        "tap", "tun", "loopback", "tunnel", "vpn", "bluetooth", "docker"
    };

    public static CollaborationService Instance { get; } = new();

    public bool IsRunning { get; private set; }

    /// <summary>手机文件上传成功后触发，参数为 (文件名, 字节数, 本机完整路径)。</summary>
    public event Action<string, long, string>? FileReceived;

    /// <summary>连接设备数、配对或掉线状态变化时触发。</summary>
    public event Action? DeviceStateChanged;

    /// <summary>上传、下载和队列准备阶段的进度通知。</summary>
    public event Action<CollaborationTransferProgress>? TransferProgressChanged;

    public int Port { get; private set; } = DefaultPort;

    public string Pin => _pin ?? string.Empty;

    public string? LocalIpAddress { get; private set; }

    public string ServerId => _serverId;

    /// <summary>电脑端是否允许已信任设备通过局域网发现自动恢复会话。</summary>
    public bool AutoReconnectAllowed { get; set; } = true;

    /// <summary>电脑→手机剪贴板同步开关；手机→电脑不受影响。</summary>
    public bool ClipboardBridgeEnabled { get; set; }

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
        _pin = CreatePairingPin();
        LocalIpAddress = ResolveLanIpAddress();
        _pageHtml = LoadEmbeddedPage();
        CleanupStaleUploads();
        LoadTrustedDevices();
        LoadOutgoingQueue();

        _acceptCts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        _discoveryListener = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _discoveryListener.EnableBroadcast = true;
        IsRunning = true;
        _ = AcceptLoopAsync(_acceptCts.Token);
        _ = DiscoveryLoopAsync(_acceptCts.Token);
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
        _discoveryListener?.Dispose();
        _discoveryListener = null;
        _acceptCts?.Dispose();
        _acceptCts = null;
        _sessions.Clear();
        _deviceSessions.Clear();
        _pairAttempts.Clear();
        _pin = null;
        IsRunning = false;
        DeviceStateChanged?.Invoke();
    }

    /// <summary>重新生成配对 PIN；旧 PIN 立即失效，已配对的会话不受影响。</summary>
    public void RegeneratePin()
    {
        _pin = CreatePairingPin();
        _pairAttempts.Clear();
    }

    /// <summary>当前活跃设备数：同一设备重复配对只计一台，网页会话按匿名计数。</summary>
    public int SessionCount
    {
        get
        {
            var now = DateTime.UtcNow;
            var cutoff = now.AddSeconds(-30);
            var stale = _sessions
                .Where(pair => pair.Value.Expires <= now)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in stale)
            {
                _sessions.TryRemove(key, out _);
            }
            var activeTokens = _sessions.Where(pair => pair.Value.LastSeen >= cutoff).Select(pair => pair.Key).ToHashSet();
            var deviceCount = _deviceSessions.Values.Count(token => activeTokens.Contains(token));
            var anonymousCount = activeTokens.Count(token => !_deviceSessions.Values.Contains(token));
            return deviceCount + anonymousCount;
        }
    }

    public IReadOnlyList<string> ConnectedDeviceNames
    {
        get
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-30);
            return _sessions.Values
                .Where(session => session.LastSeen >= cutoff && !string.IsNullOrWhiteSpace(session.DeviceId))
                .Select(session => string.IsNullOrWhiteSpace(session.DeviceName) ? "已配对手机" : session.DeviceName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>最近 30 秒内保持会话的已配对设备，用于在连接示意图中展示名称和局域网地址。</summary>
    internal IReadOnlyList<CollaborationConnectedDevice> ConnectedDevices
    {
        get
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-30);
            return _sessions.Values
                .Where(session => session.LastSeen >= cutoff && !string.IsNullOrWhiteSpace(session.DeviceId))
                .Select(session => new CollaborationConnectedDevice(
                    NormalizeDeviceDisplayName(session.DeviceName),
                    string.IsNullOrWhiteSpace(session.RemoteIpAddress) ? "地址待确认" : session.RemoteIpAddress))
                .Distinct()
                .ToArray();
        }
    }

    public int PendingOutgoingCount => _outgoingTransferIds.Keys.Count(name =>
        File.Exists(ResolveSafeChildPath(OutgoingDirectory, name)));

    /// <summary>把电脑文件复制到一次性发送队列；手机拉取完成后队列副本会自动删除。</summary>
    public async Task<string> QueueOutgoingFileAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var queued = await QueueOutgoingTransferAsync(sourcePath, transferId: null, cancellationToken);
        return queued.QueuePath;
    }

    /// <summary>把电脑文件加入发送队列，并返回可供调用方追踪的传输标识。</summary>
    public async Task<CollaborationQueuedTransfer> QueueOutgoingTransferAsync(
        string sourcePath,
        string? transferId,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("待发送文件不存在。", sourcePath);
        Directory.CreateDirectory(OutgoingDirectory);
        var info = new FileInfo(sourcePath);
        if (info.Length > MaxTransferFileBytes)
        {
            throw new InvalidOperationException("单文件不能超过 5 GB。");
        }
        var target = CreateUniqueTarget(OutgoingDirectory, SanitizeFileName(info.Name));
        var normalizedTransferId = NormalizeTransferId(transferId);
        _transferOpenPaths[normalizedTransferId] = Path.GetFullPath(sourcePath);
        ReportTransfer(normalizedTransferId, info.Name, "Send", 0, info.Length, "Preparing", "正在加入发送队列");
        try
        {
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[TransferBufferBytes];
            var progressClock = Stopwatch.StartNew();
            long lastProgressReportAt = 0;
            long copied = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                if (ShouldReportTransferProgress(progressClock, ref lastProgressReportAt, copied, info.Length))
                {
                    ReportTransfer(normalizedTransferId, info.Name, "Send", copied, info.Length, "Preparing", "正在加入发送队列");
                }
            }
            await output.FlushAsync(cancellationToken);
        }
        catch
        {
            _transferOpenPaths.TryRemove(normalizedTransferId, out _);
            try { if (File.Exists(target)) File.Delete(target); } catch { }
            ReportTransfer(normalizedTransferId, info.Name, "Send", 0, info.Length, "Failed", "加入发送队列失败");
            throw;
        }
        var queuedName = Path.GetFileName(target);
        _outgoingTransferIds[queuedName] = normalizedTransferId;
        SaveOutgoingQueue();
        ReportTransfer(normalizedTransferId, queuedName, "Send", info.Length, info.Length, "Queued", "等待手机接收");
        return new CollaborationQueuedTransfer(normalizedTransferId, target, queuedName);
    }

    /// <summary>获取完成项对应的本机文件；发送项指向原文件，接收项指向接收目录中的文件。</summary>
    public bool TryGetTransferOpenPath(string transferId, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(transferId) ||
            !_transferOpenPaths.TryGetValue(transferId, out var candidate) ||
            string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
        {
            return false;
        }
        path = candidate;
        return true;
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

    private async Task DiscoveryLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _discoveryListener != null)
        {
            try
            {
                var result = await _discoveryListener.ReceiveAsync(cancellationToken);
                var request = Encoding.UTF8.GetString(result.Buffer);
                if (!string.Equals(request.Trim(), DiscoveryRequest, StringComparison.Ordinal)) continue;
                LocalIpAddress = ResolveLanIpAddress();
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    service = "xtool-collaboration-v1",
                    serverId = _serverId,
                    host = $"{LocalIpAddress}:{Port}",
                    serverName = Environment.MachineName,
                    autoReconnectAllowed = AutoReconnectAllowed
                });
                await _discoveryListener.SendAsync(payload, result.RemoteEndPoint, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void ReportTransfer(string transferId, string fileName, string direction, long transferred, long total,
        string state, string message) =>
        TransferProgressChanged?.Invoke(new CollaborationTransferProgress(
            transferId, fileName, direction, transferred, total, state, message, DateTime.Now));

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
            try
            {
                client.NoDelay = true;
                client.SendBufferSize = TransferBufferBytes;
                client.ReceiveBufferSize = TransferBufferBytes;
            }
            catch (SocketException)
            {
                // 个别系统不允许放大套接字缓冲时继续使用系统默认值。
            }
            if (!_clientGate.Wait(0))
            {
                client.Dispose();
                continue;
            }
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serviceToken)
    {
        string? temporaryBodyPath = null;
        var uploadSlotOwned = false;
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = 15000;
                stream.WriteTimeout = 60000;
                var request = await ReadRequestAsync(stream, serviceToken);
                if (request == null)
                {
                    return;
                }
                temporaryBodyPath = request.TemporaryBodyPath;
                uploadSlotOwned = request.UploadSlotOwned;
                var consumed = await DispatchAsync(stream, request, client.Client.RemoteEndPoint as IPEndPoint, serviceToken);
                if (consumed) temporaryBodyPath = null;
            }
        }
        catch
        {
            // 单个客户端异常不影响服务。
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryBodyPath))
            {
                try { File.Delete(temporaryBodyPath); } catch { }
            }
            if (uploadSlotOwned) _uploadGate.Release();
            _clientGate.Release();
        }
    }

    private sealed record HttpRequest(
        string Method,
        string Path,
        string Query,
        Dictionary<string, string> Headers,
        byte[]? Body,
        string? TemporaryBodyPath,
        long BodyLength,
        bool UploadSlotOwned);

    private async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken serviceToken)
    {
        var headerBytes = new MemoryStream();
        var buffer = new byte[1024];
        var headerEnd = -1;
        var headerDeadline = DateTime.UtcNow.AddSeconds(15);
        while (headerBytes.Length < MaxRequestHeaderBytes)
        {
            var headerTimeLeft = headerDeadline - DateTime.UtcNow;
            if (headerTimeLeft <= TimeSpan.Zero) return null;
            var remainingHeader = MaxRequestHeaderBytes - (int)headerBytes.Length;
            var read = await ReadWithIdleTimeoutAsync(stream, buffer.AsMemory(0, Math.Min(buffer.Length, remainingHeader)),
                headerTimeLeft, serviceToken);
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
        if (headers.TryGetValue("Content-Length", out var lengthText))
        {
            if (!long.TryParse(lengthText, out var length) || length < 0 || length > MaxRequestBodyBytes)
            {
                return null;
            }
            if (length == 0) return new HttpRequest(method, path, query, headers, null, null, 0, false);
            var normalizedMethod = method.ToUpperInvariant();
            var normalizedPath = path.ToLowerInvariant();
            var parsedQuery = ParseQuery(query);
            var isPairRequest = normalizedMethod == "GET" && normalizedPath is "/pair" or "/api/pair";
            if (isPairRequest) return null;
            var token = parsedQuery.GetValueOrDefault("t");
            if (!IsSessionTokenFormatValid(token) || !IsValidSession(token)) return null;
            var isFileUpload = normalizedMethod == "PUT" && normalizedPath == "/api/files/upload";
            var isClipboardPush = ClipboardBridgeEnabled && normalizedMethod == "POST" && normalizedPath == "/api/clipboard/push";
            if (!isFileUpload && !isClipboardPush) return null;
            var contentType = headers.GetValueOrDefault("Content-Type") ?? string.Empty;
            var maximumForEndpoint = isFileUpload
                ? MaxRequestBodyBytes
                : contentType.Contains("image/png", StringComparison.OrdinalIgnoreCase)
                    ? MaxClipboardBodyBytes
                    : MaxClipboardTextBodyBytes;
            if (length > maximumForEndpoint) return null;
            var total = (int)headerBytes.Length - headerEnd - 4;
            var bodyStart = headerEnd + 4;
            var existing = headerBytes.ToArray();
            var copied = (int)Math.Min(Math.Max(0, total), length);
            if (isFileUpload)
            {
                await _uploadGate.WaitAsync(serviceToken);
                var uploadSlotOwned = true;
                try
                {
                    Directory.CreateDirectory(IncomingDirectory);
                    if (!HasUploadCapacity(length))
                    {
                        _uploadGate.Release();
                        return null;
                    }
                }
                catch
                {
                    _uploadGate.Release();
                    return null;
                }
                var temporaryPath = Path.Combine(IncomingDirectory, $".xtool-upload-{Guid.NewGuid():N}.tmp");
                var transferName = SanitizeFileName(parsedQuery.GetValueOrDefault("name"));
                var transferId = NormalizeTransferId(parsedQuery.GetValueOrDefault("id"));
                try
                {
                    await using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (copied > 0) await output.WriteAsync(existing.AsMemory(bodyStart, copied));
                    var transferBuffer = new byte[TransferBufferBytes];
                    var progressClock = Stopwatch.StartNew();
                    long lastProgressReportAt = 0;
                    long received = copied;
                    ReportTransfer(transferId, transferName, "Receive", received, length, "Transferring", "正在接收手机文件");
                    var bodyDeadline = CreateBodyDeadline(length, isFileUpload: true);
                    while (received < length)
                    {
                        var timeLeft = bodyDeadline - DateTime.UtcNow;
                        if (timeLeft <= TimeSpan.Zero) throw new TimeoutException("文件上传超过允许时限。");
                        var nextReadLength = (int)Math.Min(transferBuffer.Length, length - received);
                        var chunk = await ReadWithIdleTimeoutAsync(stream,
                            transferBuffer.AsMemory(0, nextReadLength),
                            MinTimeout(TimeSpan.FromSeconds(30), timeLeft), serviceToken);
                        if (chunk <= 0) throw new EndOfStreamException("文件上传在完成前中断。");
                        await output.WriteAsync(transferBuffer.AsMemory(0, chunk));
                        received += chunk;
                        if (ShouldReportTransferProgress(progressClock, ref lastProgressReportAt, received, length))
                        {
                            ReportTransfer(transferId, transferName, "Receive", received, length, "Transferring", "正在接收手机文件");
                        }
                    }
                    await output.FlushAsync();
                    return new HttpRequest(method, path, query, headers, null, temporaryPath, length, uploadSlotOwned);
                }
                catch
                {
                    ReportTransfer(transferId, transferName, "Receive", 0, length, "Failed", "手机文件接收中断");
                    try { File.Delete(temporaryPath); } catch { }
                    _uploadGate.Release();
                    return null;
                }
            }

            var bufferedLength = checked((int)length);
            body = new byte[bufferedLength];
            if (copied > 0) Array.Copy(existing, bodyStart, body, 0, copied);
            var clipboardDeadline = CreateBodyDeadline(length, isFileUpload: false);
            while (copied < length)
            {
                var timeLeft = clipboardDeadline - DateTime.UtcNow;
                if (timeLeft <= TimeSpan.Zero) return null;
                var chunk = await ReadWithIdleTimeoutAsync(stream, body.AsMemory(copied, bufferedLength - copied),
                    MinTimeout(TimeSpan.FromSeconds(15), timeLeft), serviceToken);
                if (chunk <= 0) return null;
                copied += chunk;
            }
        }

        return new HttpRequest(method, path, query, headers, body, null, body?.LongLength ?? 0, false);
    }

    private async Task<bool> DispatchAsync(NetworkStream stream, HttpRequest request, IPEndPoint? remoteEndpoint,
        CancellationToken serviceToken)
    {
        try
        {
            var path = request.Path.ToLowerInvariant();
            var query = ParseQuery(request.Query);

            if (path == "/pair" && request.Method == "GET")
            {
                if (!TryValidatePairingPin(remoteEndpoint, query.GetValueOrDefault("pin")))
                {
                    await WriteTextAsync(stream, 401, "text/plain; charset=utf-8", "配对 PIN 不正确", serviceToken);
                    return false;
                }
                var token = Guid.NewGuid().ToString("N");
                RegisterSession(token, query.GetValueOrDefault("device"), query.GetValueOrDefault("name"), remoteEndpoint);
                var html = (_pageHtml ?? string.Empty)
                    .Replace("__TOKEN__", token)
                    .Replace("__HOST__", $"http://{LocalIpAddress}:{Port}");
                await WriteBytesAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), serviceToken);
                return false;
            }

            // 手机原生 App 使用的 JSON 配对端点；网页端仍走 /pair 返回页面。
            if (path == "/api/pair" && request.Method == "GET")
            {
                if (!TryValidatePairingPin(remoteEndpoint, query.GetValueOrDefault("pin")))
                {
                    await WriteJsonAsync(stream, 401, new { ok = false, error = "配对 PIN 不正确" }, serviceToken);
                    return false;
                }
                var token = Guid.NewGuid().ToString("N");
                RegisterSession(token, query.GetValueOrDefault("device"), query.GetValueOrDefault("name"), remoteEndpoint);
                await WriteJsonAsync(stream, 200, new
                {
                    ok = true,
                    token,
                    host = $"{LocalIpAddress}:{Port}",
                    serverId = _serverId,
                    serverName = Environment.MachineName
                }, serviceToken);
                return false;
            }

            // 自动恢复被电脑端关闭时先拒绝，避免仅凭一次探测就把设备标记为在线。
            if (path == "/api/status" && query.GetValueOrDefault("auto") == "1" && !AutoReconnectAllowed)
            {
                await WriteJsonAsync(stream, 403, new { error = "电脑端已关闭自动连接" }, serviceToken);
                return false;
            }

            if (!IsValidSession(query.GetValueOrDefault("t"), remoteEndpoint))
            {
                await WriteJsonAsync(stream, 401, new { error = "未授权或会话已过期" }, serviceToken);
                return false;
            }

            if (!ClipboardBridgeEnabled && path.StartsWith("/api/clipboard/", StringComparison.Ordinal))
            {
                await WriteJsonAsync(stream, 404, new { error = "当前版本未开放剪贴板协作" }, serviceToken);
                return false;
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
                    SaveTrustedDevices();
                    DeviceStateChanged?.Invoke();
                }
                await WriteJsonAsync(stream, 200, new { ok = true }, serviceToken);
                return false;
            }

            // 手动断开只结束当前在线状态，保留可信令牌，便于手机再次直接连接。
            if (path == "/api/disconnect")
            {
                var token = query.GetValueOrDefault("t");
                if (!string.IsNullOrEmpty(token) && _sessions.TryGetValue(token, out var info))
                {
                    _sessions[token] = info with { LastSeen = DateTime.MinValue };
                    DeviceStateChanged?.Invoke();
                }
                await WriteJsonAsync(stream, 200, new { ok = true }, serviceToken);
                return false;
            }

            if (path == "/")
            {
                await WriteTextAsync(stream, 200, "text/plain; charset=utf-8", "X-Tool 协作中心：请使用手机扫描电脑端二维码完成配对。", serviceToken);
                return false;
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
                        latestAt = latest?.CreatedAt.ToString("O"),
                        serverId = _serverId,
                        serverName = Environment.MachineName,
                        autoReconnectAllowed = AutoReconnectAllowed
                    };
                }
                await WriteJsonAsync(stream, 200, statusPayload, serviceToken);
                return false;
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
                await WriteJsonAsync(stream, 200, pullPayload, serviceToken);
                return false;
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
                        await WriteJsonAsync(stream, 200, new { ok = true }, serviceToken);
                        return false;
                    }
                }
                else if (contentType.Contains("image/png", StringComparison.OrdinalIgnoreCase) && request.Body is { Length: > 0 })
                {
                    if (!IsSafePng(request.Body))
                    {
                        await WriteJsonAsync(stream, 400, new { error = "图片尺寸过大或 PNG 格式无效" }, serviceToken);
                        return false;
                    }
                    await WriteClipboardFromMobileAsync(null, request.Body);
                    await WriteJsonAsync(stream, 200, new { ok = true }, serviceToken);
                    return false;
                }
                await WriteJsonAsync(stream, 400, new { error = "不支持的剪贴板内容" }, serviceToken);
                return false;
            }

            if (path == "/api/files/upload" && request.Method == "PUT")
            {
                var name = SanitizeFileName(query.GetValueOrDefault("name"));
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(request.TemporaryBodyPath) ||
                    !File.Exists(request.TemporaryBodyPath))
                {
                    await WriteJsonAsync(stream, 400, new { error = "缺少文件名或内容" }, serviceToken);
                    return false;
                }
                var target = MoveUploadToUniqueTarget(request.TemporaryBodyPath, IncomingDirectory, name);
                var transferId = NormalizeTransferId(query.GetValueOrDefault("id"));
                _transferOpenPaths[transferId] = target;
                ReportTransfer(transferId, Path.GetFileName(target), "Receive", request.BodyLength, request.BodyLength,
                    "Completed", "已收到手机文件");
                FileReceived?.Invoke(Path.GetFileName(target), request.BodyLength, target);
                await WriteJsonAsync(stream, 200, new { ok = true, name = Path.GetFileName(target) }, serviceToken);
                return true;
            }

            if (path == "/api/files/list" && request.Method == "GET")
            {
                var dir = query.GetValueOrDefault("dir") == "outgoing" ? OutgoingDirectory : IncomingDirectory;
                var files = Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir)
                        .Where(file => !Path.GetFileName(file).StartsWith(".xtool-upload-", StringComparison.OrdinalIgnoreCase))
                        .Where(file => dir != OutgoingDirectory || _outgoingTransferIds.ContainsKey(Path.GetFileName(file)))
                        .Take(2000)
                        .Select(file => new FileInfo(file))
                        .OrderByDescending(info => info.LastWriteTime)
                        .Take(500)
                        .Select(info => new
                        {
                            id = _outgoingTransferIds.GetOrAdd(info.Name, _ => Guid.NewGuid().ToString("N")),
                            name = info.Name,
                            size = info.Length,
                            at = info.LastWriteTime.ToString("O")
                        })
                        .ToArray()
                    : Array.Empty<object>();
                await WriteJsonAsync(stream, 200, new { ok = true, files }, serviceToken);
                return false;
            }

            if (path == "/api/files/complete" && request.Method == "GET")
            {
                var name = SanitizeFileName(query.GetValueOrDefault("name"));
                var transferId = query.GetValueOrDefault("id") ?? string.Empty;
                var filePath = ResolveSafeChildPath(OutgoingDirectory, name);
                if (filePath is null || !File.Exists(filePath) ||
                    !_outgoingTransferIds.TryGetValue(name, out var queuedTransferId) ||
                    !string.Equals(queuedTransferId, transferId, StringComparison.Ordinal))
                {
                    await WriteJsonAsync(stream, 404, new { error = "待完成文件不存在或传输标识不匹配" }, serviceToken);
                    return false;
                }

                var fileInfo = new FileInfo(filePath);
                _parallelDownloadProgress.TryRemove(transferId, out _);
                ReportTransfer(transferId, fileInfo.Name, "Send", fileInfo.Length, fileInfo.Length, "Completed", "手机已接收");
                try { File.Delete(filePath); } catch { }
                _outgoingTransferIds.TryRemove(fileInfo.Name, out _);
                SaveOutgoingQueue();
                await WriteJsonAsync(stream, 200, new { ok = true }, serviceToken);
                return false;
            }

            if (path == "/api/files/cancel" && request.Method == "GET")
            {
                var name = SanitizeFileName(query.GetValueOrDefault("name"));
                var transferId = query.GetValueOrDefault("id") ?? string.Empty;
                var discard = query.GetValueOrDefault("discard") == "1";
                if (_outgoingTransferIds.TryGetValue(name, out var queuedTransferId) &&
                    string.Equals(queuedTransferId, transferId, StringComparison.Ordinal))
                {
                    var filePath = ResolveSafeChildPath(OutgoingDirectory, name);
                    var total = filePath is not null && File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
                    _parallelDownloadProgress.TryRemove(transferId, out _);
                    ReportTransfer(transferId, name, "Send", 0, total, "Failed",
                        discard ? "已取消发送" : "手机接收中断，可重新发送");
                    if (discard)
                    {
                        if (filePath is not null)
                        {
                            try { File.Delete(filePath); } catch { }
                        }
                        _outgoingTransferIds.TryRemove(name, out _);
                        SaveOutgoingQueue();
                    }
                }
                await WriteJsonAsync(stream, 200, new { ok = true }, serviceToken);
                return false;
            }

            if (path == "/api/files/download" && request.Method == "GET")
            {
                var dir = query.GetValueOrDefault("dir") == "outgoing" ? OutgoingDirectory : IncomingDirectory;
                var name = SanitizeFileName(query.GetValueOrDefault("name"));
                var filePath = ResolveSafeChildPath(dir, name);
                if (filePath is null || !File.Exists(filePath) ||
                    File.GetAttributes(filePath).HasFlag(FileAttributes.ReparsePoint))
                {
                    await WriteJsonAsync(stream, 404, new { error = "文件不存在" }, serviceToken);
                    return false;
                }
                var requestedTransferId = query.GetValueOrDefault("id") ?? string.Empty;
                var transferId = NormalizeTransferId(requestedTransferId);
                var fileInfo = new FileInfo(filePath);
                var isParallelSegment = query.GetValueOrDefault("parallel") == "1";
                if (isParallelSegment)
                {
                    if (dir != OutgoingDirectory ||
                        !_outgoingTransferIds.TryGetValue(fileInfo.Name, out var queuedTransferId) ||
                        !string.Equals(queuedTransferId, requestedTransferId, StringComparison.Ordinal) ||
                        !long.TryParse(query.GetValueOrDefault("offset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) ||
                        !long.TryParse(query.GetValueOrDefault("length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var segmentLength) ||
                        offset < 0 || segmentLength <= 0 || offset >= fileInfo.Length)
                    {
                        await WriteJsonAsync(stream, 400, new { error = "分段参数无效" }, serviceToken);
                        return false;
                    }

                    await WriteFileRangeAsync(stream, filePath, transferId, offset, segmentLength, serviceToken);
                    return false;
                }

                try
                {
                    await WriteFileAsync(stream, filePath, transferId, serviceToken);
                    ReportTransfer(transferId, fileInfo.Name, "Send", fileInfo.Length, fileInfo.Length, "Completed", "手机已接收");
                }
                catch
                {
                    ReportTransfer(transferId, fileInfo.Name, "Send", 0, fileInfo.Length, "Failed", "发送到手机失败");
                    throw;
                }
                try { File.Delete(filePath); } catch { }
                _outgoingTransferIds.TryRemove(fileInfo.Name, out _);
                SaveOutgoingQueue();
                return false;
            }

            await WriteJsonAsync(stream, 404, new { error = "接口不存在" }, serviceToken);
            return false;
        }
        catch (Exception)
        {
            try
            {
                await WriteJsonAsync(stream, 500, new { error = "服务器处理请求失败" }, serviceToken);
            }
            catch
            {
                // 响应写入失败时连接已不可用。
            }
            return false;
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

    private bool IsValidSession(string? token, IPEndPoint? remoteEndpoint = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }
        var now = DateTime.UtcNow;
        if (_sessions.TryGetValue(token, out var info) && info.Expires > now)
        {
            var becameActive = info.LastSeen < now.AddSeconds(-30);
            _sessions[token] = info with
            {
                LastSeen = now,
                Expires = now.AddDays(SessionLifetimeDays),
                RemoteIpAddress = NormalizeRemoteIp(remoteEndpoint) ?? info.RemoteIpAddress
            };
            if (becameActive)
            {
                DeviceStateChanged?.Invoke();
            }
            return true;
        }
        _sessions.TryRemove(token, out _);
        return false;
    }

    private static bool IsSessionTokenFormatValid(string? token) =>
        token is { Length: 32 } && token.All(Uri.IsHexDigit);

    /// <summary>注册配对会话；带设备 ID 时同一设备重复配对替换旧会话，不重复计数。</summary>
    private void RegisterSession(string token, string? deviceId, string? deviceName, IPEndPoint? remoteEndpoint)
    {
        var normalizedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? string.Empty : deviceId.Trim();
        var normalizedDeviceName = NormalizeDeviceDisplayName(deviceName);
        _sessions[token] = new SessionInfo(
            DateTime.UtcNow.AddDays(SessionLifetimeDays),
            DateTime.UtcNow,
            normalizedDeviceId,
            normalizedDeviceName,
            NormalizeRemoteIp(remoteEndpoint) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }
        if (_deviceSessions.TryGetValue(deviceId, out var oldToken))
        {
            _sessions.TryRemove(oldToken, out _);
        }
        _deviceSessions[deviceId] = token;
        SaveTrustedDevices();
        DeviceStateChanged?.Invoke();
    }

    private static string NormalizeTransferId(string? value) =>
        value is { Length: >= 8 and <= 64 } && value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            ? value
            : Guid.NewGuid().ToString("N");

    private static string NormalizeDeviceDisplayName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "已配对手机" : value.Replace('+', ' ').Trim();
        if (name.Contains("V2502A", StringComparison.OrdinalIgnoreCase)) return "Vivo X300 PRO";
        if (name.StartsWith("vivo ", StringComparison.OrdinalIgnoreCase))
        {
            return "Vivo " + name[5..].Trim();
        }
        return name;
    }

    private static string? NormalizeRemoteIp(IPEndPoint? endpoint)
    {
        if (endpoint == null) return null;
        var address = endpoint.Address;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            if (index > 0)
            {
                // application/x-www-form-urlencoded 语义中“+”表示空格；Android URLEncoder 会采用该形式。
                var key = Uri.UnescapeDataString(part.Substring(0, index).Replace('+', ' '));
                var value = Uri.UnescapeDataString(part.Substring(index + 1).Replace('+', ' '));
                result[key] = value;
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
        var safe = Path.GetFileName(builder.ToString()).Trim().TrimEnd('.', ' ');
        if (safe is "." or ".." || safe.Length is 0 or > 200 ||
            safe.StartsWith(".xtool-upload-", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        var stem = Path.GetFileNameWithoutExtension(safe);
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        return reserved.Contains(stem, StringComparer.OrdinalIgnoreCase) ? string.Empty : safe;
    }

    private static string CreateUniqueTarget(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) throw new InvalidDataException("文件名无效。");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 0; index < 10_000; index++)
        {
            var candidateName = index == 0 ? fileName : $"{stem}_{index}{extension}";
            var candidate = Path.GetFullPath(Path.Combine(root, candidateName));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件名超出发送目录边界。");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("同名文件过多，无法加入发送队列。");
    }

    private static bool IsSafePng(byte[] bytes)
    {
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            ReadBigEndianUInt32(bytes.AsSpan(8, 4)) != 13 ||
            !bytes.AsSpan(12, 4).SequenceEqual(new byte[] { 73, 72, 68, 82 }))
            return false;
        var width = ReadBigEndianUInt32(bytes.AsSpan(16, 4));
        var height = ReadBigEndianUInt32(bytes.AsSpan(20, 4));
        return width is > 0 and <= 16384 && height is > 0 and <= 16384 && (long)width * height <= MaxClipboardImagePixels;
    }

    private static uint ReadBigEndianUInt32(ReadOnlySpan<byte> value) =>
        ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];

    private static async Task<int> ReadWithIdleTimeoutAsync(
        NetworkStream stream, Memory<byte> buffer, TimeSpan timeout, CancellationToken serviceToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        timeoutSource.CancelAfter(timeout);
        return await stream.ReadAsync(buffer, timeoutSource.Token);
    }

    private static string CreatePairingPin() =>
        RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);

    private bool TryValidatePairingPin(IPEndPoint? remoteEndpoint, string? suppliedPin)
    {
        if (!IsRunning) return false;
        var address = remoteEndpoint?.Address.ToString() ?? "unknown";
        var now = DateTime.UtcNow;
        lock (_pairAttemptSync)
        {
            foreach (var stale in _pairAttempts.Where(pair => pair.Value.LastAttempt < now.AddMinutes(-10))
                         .Select(pair => pair.Key).Take(32).ToArray())
                _pairAttempts.TryRemove(stale, out _);
            if (_pairAttempts.TryGetValue(address, out var attempt) && attempt.BlockedUntil > now) return false;
            if (string.Equals(suppliedPin, _pin, StringComparison.Ordinal))
            {
                _pairAttempts.TryRemove(address, out _);
                return true;
            }
            var failures = (attempt?.Failures ?? 0) + 1;
            _pairAttempts[address] = failures >= 8
                ? new PairAttemptInfo(0, now.AddMinutes(2), now)
                : new PairAttemptInfo(failures, DateTime.MinValue, now);
            return false;
        }
    }

    private static DateTime CreateBodyDeadline(long length, bool isFileUpload)
    {
        // 上传按最低 256 KiB/s 计算总时限；短剪贴板请求最多允许 30 秒。
        var seconds = isFileUpload
            ? Math.Clamp(60d + length / (256d * 1024d), 60d, 36 * 60d)
            : 30d;
        return DateTime.UtcNow.AddSeconds(seconds);
    }

    private static TimeSpan MinTimeout(TimeSpan first, TimeSpan second) => first <= second ? first : second;

    private void CleanupStaleUploads()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(IncomingDirectory, ".xtool-upload-*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddHours(-1)) File.Delete(path);
                }
                catch { }
            }
        }
        catch { }
    }

    private static string MoveUploadToUniqueTarget(string temporaryPath, string directory, string fileName)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 0; index < 10_000; index++)
        {
            var candidateName = index == 0 ? fileName : $"{name}_{index}{extension}";
            var candidate = Path.GetFullPath(Path.Combine(root, candidateName));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件名超出接收目录边界。");
            try
            {
                File.Move(temporaryPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate)) { }
        }
        throw new IOException("同名文件过多，无法生成安全的接收文件名。");
    }

    private static string? ResolveSafeChildPath(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        try
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, fileName));
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? candidate : null;
        }
        catch { return null; }
    }

    private bool HasUploadCapacity(long incomingBytes)
    {
        try
        {
            var used = Directory.EnumerateFiles(IncomingDirectory)
                .Select(path => new FileInfo(path).Length)
                .Aggregate(0L, (total, length) => total > MaxIncomingDirectoryBytes - length ? MaxIncomingDirectoryBytes : total + length);
            if (used > MaxIncomingDirectoryBytes - incomingBytes) return false;
            var root = Path.GetPathRoot(Path.GetFullPath(IncomingDirectory));
            if (string.IsNullOrWhiteSpace(root)) return false;
            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace - incomingBytes >= MinimumFreeSpaceBytes;
        }
        catch { return false; }
    }

    private void LoadTrustedDevices()
    {
        try
        {
            if (!File.Exists(TrustStorePath)) return;
            var records = JsonSerializer.Deserialize<List<CollaborationTrustedDevice>>(
                File.ReadAllText(TrustStorePath, Encoding.UTF8)) ?? [];
            var now = DateTime.UtcNow;
            foreach (var record in records.Where(record =>
                         record.ExpiresAt > now &&
                         !string.IsNullOrWhiteSpace(record.DeviceId) &&
                         IsSessionTokenFormatValid(record.Token)))
            {
                _sessions[record.Token] = new SessionInfo(
                    record.ExpiresAt,
                    DateTime.MinValue,
                    record.DeviceId,
                    NormalizeDeviceDisplayName(record.DeviceName),
                    string.Empty);
                _deviceSessions[record.DeviceId] = record.Token;
            }
        }
        catch
        {
            // 信任记录损坏时忽略，用户仍可重新扫码配对。
        }
    }

    private void SaveTrustedDevices()
    {
        try
        {
            var records = _deviceSessions
                .Select(pair => _sessions.TryGetValue(pair.Value, out var session)
                    ? new CollaborationTrustedDevice(pair.Key, pair.Value, session.Expires, session.DeviceName)
                    : null)
                .Where(record => record is not null)
                .Cast<CollaborationTrustedDevice>()
                .ToArray();
            var directory = Path.GetDirectoryName(TrustStorePath)!;
            Directory.CreateDirectory(directory);
            var temporary = TrustStorePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            File.Move(temporary, TrustStorePath, overwrite: true);
        }
        catch
        {
            // 保存失败不会中断当前连接，只会导致下次需要重新配对。
        }
    }

    private void LoadOutgoingQueue()
    {
        lock (_outgoingQueueSync)
        {
            _outgoingTransferIds.Clear();
            try
            {
                if (!File.Exists(OutgoingQueuePath)) return;
                var records = JsonSerializer.Deserialize<List<CollaborationQueuedFile>>(
                    File.ReadAllText(OutgoingQueuePath, Encoding.UTF8)) ?? [];
                foreach (var record in records)
                {
                    var safeName = SanitizeFileName(record.FileName);
                    var path = ResolveSafeChildPath(OutgoingDirectory, safeName);
                    if (safeName != record.FileName || path is null || !File.Exists(path) ||
                        NormalizeTransferId(record.TransferId) != record.TransferId) continue;
                    _outgoingTransferIds[safeName] = record.TransferId;
                }
            }
            catch
            {
                // 清单损坏时不猜测目录中的文件，避免把历史残留意外发送到手机。
                _outgoingTransferIds.Clear();
            }
        }
    }

    private void SaveOutgoingQueue()
    {
        lock (_outgoingQueueSync)
        {
            try
            {
                var records = _outgoingTransferIds
                    .Where(pair => File.Exists(ResolveSafeChildPath(OutgoingDirectory, pair.Key)))
                    .Select(pair => new CollaborationQueuedFile(pair.Key, pair.Value, DateTime.Now))
                    .ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(OutgoingQueuePath)!);
                var temporary = OutgoingQueuePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                File.Move(temporary, OutgoingQueuePath, overwrite: true);
            }
            catch
            {
                // 清单保存失败不删除任何原文件；本次运行仍可继续发送。
            }
        }
    }

    private static string LoadOrCreateServerId()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "X-Tool", "Collaboration", "server-id.txt");
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (existing is { Length: 32 } && existing.All(Uri.IsHexDigit)) return existing;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var generated = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, generated, Encoding.UTF8);
            return generated;
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
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

    private static async Task WriteTextAsync(NetworkStream stream, int status, string contentType, string text,
        CancellationToken cancellationToken) =>
        await WriteBytesAsync(stream, status, contentType, Encoding.UTF8.GetBytes(text), cancellationToken);

    private static async Task WriteJsonAsync(NetworkStream stream, int status, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await WriteBytesAsync(stream, status, "application/json; charset=utf-8", bytes, cancellationToken);
    }

    private static async Task WriteBytesAsync(NetworkStream stream, int status, string contentType, byte[] body,
        CancellationToken cancellationToken)
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
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        var headBytes = Encoding.UTF8.GetBytes(head);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(30));
        await stream.WriteAsync(headBytes.AsMemory(), timeoutSource.Token);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body.AsMemory(), timeoutSource.Token);
        }
        await stream.FlushAsync(timeoutSource.Token);
    }

    private async Task WriteFileAsync(NetworkStream stream, string filePath, string transferId, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {file.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        var headBytes = Encoding.UTF8.GetBytes(head);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMinutes(30));
        await stream.WriteAsync(headBytes.AsMemory(), timeoutSource.Token);
        var buffer = new byte[TransferBufferBytes];
        var progressClock = Stopwatch.StartNew();
        long lastProgressReportAt = 0;
        long sent = 0;
        ReportTransfer(transferId, Path.GetFileName(filePath), "Send", 0, file.Length, "Transferring", "正在发送到手机");
        while (true)
        {
            var read = await file.ReadAsync(buffer.AsMemory(), timeoutSource.Token);
            if (read == 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read), timeoutSource.Token);
            sent += read;
            if (ShouldReportTransferProgress(progressClock, ref lastProgressReportAt, sent, file.Length))
            {
                ReportTransfer(transferId, Path.GetFileName(filePath), "Send", sent, file.Length, "Transferring", "正在发送到手机");
            }
        }
        await stream.FlushAsync(timeoutSource.Token);
    }

    private async Task WriteFileRangeAsync(
        NetworkStream stream,
        string filePath,
        string transferId,
        long offset,
        long requestedLength,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            TransferBufferBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var segmentLength = Math.Min(requestedLength, file.Length - offset);
        file.Position = offset;
        var head = $"HTTP/1.1 206 Partial Content\r\nContent-Type: application/octet-stream\r\nContent-Length: {segmentLength}\r\nContent-Range: bytes {offset}-{offset + segmentLength - 1}/{file.Length}\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        var headBytes = Encoding.ASCII.GetBytes(head);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMinutes(30));
        await stream.WriteAsync(headBytes.AsMemory(), timeoutSource.Token);

        var buffer = new byte[TransferBufferBytes];
        long sent = 0;
        ReportParallelDownloadProgress(transferId, Path.GetFileName(filePath), offset, sent, file.Length);
        while (sent < segmentLength)
        {
            var readLength = (int)Math.Min(buffer.Length, segmentLength - sent);
            var read = await file.ReadAsync(buffer.AsMemory(0, readLength), timeoutSource.Token);
            if (read == 0) throw new EndOfStreamException("分段文件在发送完成前结束。");
            await stream.WriteAsync(buffer.AsMemory(0, read), timeoutSource.Token);
            sent += read;
            ReportParallelDownloadProgress(transferId, Path.GetFileName(filePath), offset, sent, file.Length);
        }
        await stream.FlushAsync(timeoutSource.Token);
    }

    private void ReportParallelDownloadProgress(
        string transferId,
        string fileName,
        long segmentOffset,
        long segmentTransferred,
        long total)
    {
        var state = _parallelDownloadProgress.GetOrAdd(transferId, _ => new ParallelDownloadProgress(total));
        long aggregate;
        lock (state.SyncRoot)
        {
            state.SegmentBytes[segmentOffset] = segmentTransferred;
            aggregate = Math.Min(state.TotalBytes, state.SegmentBytes.Values.Sum());
            var now = Environment.TickCount64;
            if (aggregate < state.TotalBytes &&
                now - state.LastReportAtMilliseconds < TransferProgressIntervalMilliseconds)
            {
                return;
            }
            state.LastReportAtMilliseconds = now;
        }

        ReportTransfer(transferId, fileName, "Send", aggregate, total, "Transferring", "正在并行发送到手机");
    }

    private static bool ShouldReportTransferProgress(
        Stopwatch clock,
        ref long lastReportAtMilliseconds,
        long transferred,
        long total)
    {
        var elapsed = clock.ElapsedMilliseconds;
        if (transferred < total && elapsed - lastReportAtMilliseconds < TransferProgressIntervalMilliseconds)
        {
            return false;
        }

        lastReportAtMilliseconds = elapsed;
        return true;
    }
}
