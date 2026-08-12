using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ScreenshotApp.NetworkWorkbench;

/// <summary>
/// 管理员 ETW 辅助进程。仅聚合网络事件，不读取数据包正文。
/// </summary>
internal static class NetworkEtwTrafficHelper
{
    public static int Run(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) return 2;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            var connected = false;
            var waitDeadline = DateTime.UtcNow.AddSeconds(60);
            // 计划任务可能在 X-Tool 主程序之前启动；持续等待主程序创建管道，
            // 任务被结束或主程序关闭时，管道断开会让辅助进程自然退出。
            for (var attempt = 0; !connected && DateTime.UtcNow < waitDeadline; attempt++)
            {
                try
                {
                    pipe.Connect(attempt == 0 ? 10000 : 1000);
                    connected = true;
                }
                catch (TimeoutException) { }
                catch (IOException) { }
            }
            if (!connected) return 3;
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            using var session = new TraceEventSession($"XTool.Network.{Environment.ProcessId}");
            session.StopOnDispose = true;
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            var buckets = new ConcurrentDictionary<FlowKey, FlowBucket>();

            void Add(int pid, string protocol, string local, string remote, long sent, long received)
            {
                if (pid <= 0) return;
                var startTicks = GetProcessStartTicks(pid);
                var key = new FlowKey(pid, startTicks, protocol, local, remote);
                var bucket = buckets.GetOrAdd(key, _ => new FlowBucket());
                if (sent > 0) Interlocked.Add(ref bucket.SentBytes, sent);
                if (received > 0) Interlocked.Add(ref bucket.ReceivedBytes, received);
            }

            session.Source.Kernel.TcpIpSend += data => Add(data.ProcessID, "TCP", Endpoint(data.saddr, data.sport), Endpoint(data.daddr, data.dport), data.size, 0);
            session.Source.Kernel.TcpIpRecv += data => Add(data.ProcessID, "TCP", Endpoint(data.daddr, data.dport), Endpoint(data.saddr, data.sport), 0, data.size);
            session.Source.Kernel.TcpIpSendIPV6 += data => Add(data.ProcessID, "TCP", Endpoint(data.saddr, data.sport), Endpoint(data.daddr, data.dport), data.size, 0);
            session.Source.Kernel.TcpIpRecvIPV6 += data => Add(data.ProcessID, "TCP", Endpoint(data.daddr, data.dport), Endpoint(data.saddr, data.sport), 0, data.size);
            session.Source.Kernel.UdpIpSend += data => Add(data.ProcessID, "UDP", Endpoint(data.saddr, data.sport), Endpoint(data.daddr, data.dport), data.size, 0);
            session.Source.Kernel.UdpIpRecv += data => Add(data.ProcessID, "UDP", Endpoint(data.daddr, data.dport), Endpoint(data.saddr, data.sport), 0, data.size);
            session.Source.Kernel.UdpIpSendIPV6 += data => Add(data.ProcessID, "UDP", Endpoint(data.saddr, data.sport), Endpoint(data.daddr, data.dport), data.size, 0);
            session.Source.Kernel.UdpIpRecvIPV6 += data => Add(data.ProcessID, "UDP", Endpoint(data.daddr, data.dport), Endpoint(data.saddr, data.sport), 0, data.size);

            using var timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    var capturedAt = DateTimeOffset.UtcNow;
                    foreach (var (key, bucket) in buckets.ToArray())
                    {
                        if (!buckets.TryRemove(key, out var value)) continue;
                        writer.WriteLine(JsonSerializer.Serialize(new NetworkFlowSample(key.ProcessId, key.ProcessStartTicks, key.Protocol,
                            key.LocalEndpoint, key.RemoteEndpoint, Interlocked.Read(ref value.SentBytes),
                            Interlocked.Read(ref value.ReceivedBytes), capturedAt)));
                    }
                    // 即使这一秒没有网络事件，也发送合法心跳，避免父进程因空对象反序列化而中止读取。
                    writer.WriteLine(JsonSerializer.Serialize(new NetworkFlowSample(0, 0, string.Empty,
                        string.Empty, string.Empty, 0, 0, capturedAt)));
                }
                catch
                {
                    session.Source.StopProcessing();
                }
            }, null, 250, 1000);

            session.Source.Process();
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static long GetProcessStartTicks(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.StartTime.ToUniversalTime().Ticks; }
        catch { return 0; }
    }

    private static string Endpoint(System.Net.IPAddress address, int port) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

    private readonly record struct FlowKey(int ProcessId, long ProcessStartTicks, string Protocol, string LocalEndpoint, string RemoteEndpoint);
    private sealed class FlowBucket { public long SentBytes; public long ReceivedBytes; }
}

internal sealed class NetworkEtwTrafficClient : IDisposable
{
    private static readonly TimeSpan InactiveFlowRetention = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<FlowKey, FlowTotal> _flows = new();
    private readonly ConcurrentDictionary<ProcessIdentity, ArchivedProcessTotal> _archivedProcesses = new();
    private readonly object _measurementSync = new();
    private CancellationTokenSource? _cancellation;
    private Process? _helperProcess;
    private DateTimeOffset _lastSampleAt;
    private int _firstSampleRaised;
    private bool _persistentTaskConnection;
    private NamedPipeServerStream? _server;
    public bool IsRunning => _cancellation is not null &&
        (_persistentTaskConnection || _helperProcess is { HasExited: false });
    public string StatusText { get; private set; } = "精确监测未启用";
    public string SessionId { get; private set; } = string.Empty;
    public event EventHandler? FirstSampleAvailable;

    public async Task<(bool Success, string Message)> StartAsync()
    {
        if (IsRunning) return (true, "逐连接精确监测正在运行");
        BeginSession();
        var pipeName = $"XTool.NetworkEtw.{Environment.ProcessId}.{Guid.NewGuid():N}";
        return await StartWithPipeAsync(pipeName, launchPersistentTask: false);
    }

    public async Task<(bool Success, string Message)> StartPersistentAsync()
    {
        if (IsRunning) return (true, "独立 ETW 辅助进程正在运行");
        BeginSession();
        return await StartWithPipeAsync(NetworkEtwAutoStartService.PersistentPipeName, launchPersistentTask: true);
    }

    private void BeginSession()
    {
        _flows.Clear();
        _archivedProcesses.Clear();
        _processInfoCache.Clear();
        _lastSampleAt = default;
        SessionId = Guid.NewGuid().ToString("N");
        Volatile.Write(ref _firstSampleRaised, 0);
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        try { if (!_persistentTaskConnection && _helperProcess is { HasExited: false }) _helperProcess.Kill(); } catch { }
        try { _server?.Dispose(); } catch { }
        _helperProcess?.Dispose();
        _cancellation?.Dispose();
        _cancellation = null;
        _server = null;
        _persistentTaskConnection = false;
        StatusText = "精确监测已停止";
    }

    private async Task<(bool Success, string Message)> StartWithPipeAsync(string pipeName, bool launchPersistentTask)
    {
        var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        _server = server;
        try
        {
            if (launchPersistentTask)
            {
                var taskResult = await NetworkEtwAutoStartService.RunTaskAsync();
                if (!taskResult.Success)
                {
                    server.Dispose();
                    _server = null;
                    return taskResult;
                }
                _persistentTaskConnection = true;
            }
            else
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位 X-Tool 可执行文件。");
                _helperProcess = Process.Start(new ProcessStartInfo(executable, $"--network-etw-helper {pipeName}")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (_helperProcess is null)
                {
                    server.Dispose();
                    _server = null;
                    return (false, "未能启动精确监测辅助进程。");
                }
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await server.WaitForConnectionAsync(timeout.Token);
            _cancellation = new CancellationTokenSource();
            _ = ReadLoopAsync(server, _cancellation.Token);
            StatusText = "ETW 精确监测中";
            return (true, "已启用逐连接精确流量监测；不会读取数据包正文。");
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            server.Dispose();
            _server = null;
            try { if (!_persistentTaskConnection && _helperProcess is { HasExited: false }) _helperProcess.Kill(); } catch { }
            StatusText = "用户取消管理员授权";
            return (false, StatusText);
        }
        catch (Exception exception)
        {
            server.Dispose();
            _server = null;
            try { if (!_persistentTaskConnection && _helperProcess is { HasExited: false }) _helperProcess.Kill(); } catch { }
            _persistentTaskConnection = false;
            StatusText = $"精确监测启动失败：{exception.Message}";
            return (false, StatusText);
        }
    }

    private async Task ReadLoopAsync(NamedPipeServerStream server, CancellationToken token)
    {
        using (server)
        using (var reader = new StreamReader(server, Encoding.UTF8))
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().WaitAsync(token);
                    if (line is null) break;
                    var sample = JsonSerializer.Deserialize<NetworkFlowSample>(line);
                    if (sample is null || sample.ProcessId <= 0) continue;
                    var key = new FlowKey(sample.ProcessId, sample.ProcessStartTicks, sample.Protocol,
                        Normalize(sample.LocalEndpoint), Normalize(sample.RemoteEndpoint));
                    RecordSample(key, sample);
                    _lastSampleAt = sample.CapturedAt;
                    if (Interlocked.Exchange(ref _firstSampleRaised, 1) == 0) FirstSampleAvailable?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally
            {
                StatusText = "精确监测已停止";
                if (ReferenceEquals(_server, server))
                {
                    _server = null;
                    _persistentTaskConnection = false;
                    var cancellation = _cancellation;
                    _cancellation = null;
                    cancellation?.Dispose();
                }
            }
        }
    }

    public NetworkFlowMeasurement GetMeasurement(int processId, string protocol, string local, string remote)
    {
        long currentStartTicks;
        try { using var process = Process.GetProcessById(processId); currentStartTicks = process.StartTime.ToUniversalTime().Ticks; }
        catch { currentStartTicks = 0; }
        var normalizedLocal = Normalize(local);
        var normalizedRemote = Normalize(remote);
        var matches = _flows.Where(pair => pair.Key.ProcessId == processId &&
            (currentStartTicks == 0 || pair.Key.ProcessStartTicks == 0 || pair.Key.ProcessStartTicks == currentStartTicks) &&
            pair.Key.Protocol.Equals(protocol, StringComparison.OrdinalIgnoreCase) &&
            pair.Key.LocalEndpoint == normalizedLocal &&
            (pair.Key.RemoteEndpoint == normalizedRemote || IsWildcardRemote(normalizedRemote)))
            .Select(pair => pair.Value).ToArray();
        // 闲置连接会压缩进进程累计，逐连接视图只展示仍活跃连接的会话累计。
        var sentTotal = matches.Sum(value => Interlocked.Read(ref value.SentBytes));
        var receivedTotal = matches.Sum(value => Interlocked.Read(ref value.ReceivedBytes));
        var sentRate = matches.Sum(value => Interlocked.Exchange(ref value.LastSentBytes, 0)) * 8d;
        var receivedRate = matches.Sum(value => Interlocked.Exchange(ref value.LastReceivedBytes, 0)) * 8d;
        return new NetworkFlowMeasurement(sentRate + receivedRate, sentTotal + receivedTotal, sentRate, receivedRate);
    }

    /// <summary>
    /// 返回当前 ETW 会话按进程聚合的流量快照。只读取字节计数，不保存数据包正文。
    /// </summary>
    public IReadOnlyList<NetworkTrafficProcessMeasurement> GetProcessMeasurements(bool consumeRates = true)
    {
        ActiveFlowSnapshot[] active;
        Dictionary<ProcessIdentity, ArchivedProcessTotal> archived;
        lock (_measurementSync)
        {
            ArchiveInactiveFlows();
            active = _flows.Select(pair =>
            {
                lock (pair.Value.Sync)
                {
                    var sentRate = consumeRates ? pair.Value.LastSentBytes : 0;
                    var receivedRate = consumeRates ? pair.Value.LastReceivedBytes : 0;
                    if (consumeRates)
                    {
                        pair.Value.LastSentBytes = 0;
                        pair.Value.LastReceivedBytes = 0;
                    }
                    return new ActiveFlowSnapshot(pair.Key, pair.Value.SentBytes, pair.Value.ReceivedBytes,
                        sentRate, receivedRate);
                }
            }).ToArray();
            archived = _archivedProcesses.ToDictionary(pair => pair.Key, pair => pair.Value);
        }
        var identities = active.Select(flow => new ProcessIdentity(flow.Key.ProcessId, flow.Key.ProcessStartTicks))
            .Concat(archived.Keys)
            .Distinct().ToArray();
        var result = new List<NetworkTrafficProcessMeasurement>();
        foreach (var identity in identities)
        {
            var sent = 0L;
            var received = 0L;
            var sentRate = 0L;
            var receivedRate = 0L;
            var flows = new List<NetworkTrafficFlowMeasurement>();
            foreach (var flow in active.Where(flow => flow.Key.ProcessId == identity.ProcessId && flow.Key.ProcessStartTicks == identity.ProcessStartTicks))
            {
                sent += flow.SentBytes;
                received += flow.ReceivedBytes;
                sentRate += flow.SentRateBytes;
                receivedRate += flow.ReceivedRateBytes;
                flows.Add(new NetworkTrafficFlowMeasurement(flow.Key.Protocol, flow.Key.LocalEndpoint,
                    flow.Key.RemoteEndpoint, flow.SentBytes, flow.ReceivedBytes, flow.SentRateBytes, flow.ReceivedRateBytes));
            }
            if (archived.TryGetValue(identity, out var archivedTotal))
            {
                sent += archivedTotal.SentBytes;
                received += archivedTotal.ReceivedBytes;
                if (archivedTotal.ExternalSentBytes > 0 || archivedTotal.ExternalReceivedBytes > 0)
                    flows.Add(new NetworkTrafficFlowMeasurement("历史累计", "0.0.0.0:0", "0.0.0.1:0",
                        archivedTotal.ExternalSentBytes, archivedTotal.ExternalReceivedBytes, 0, 0));
                if (archivedTotal.LoopbackSentBytes > 0 || archivedTotal.LoopbackReceivedBytes > 0)
                    flows.Add(new NetworkTrafficFlowMeasurement("历史累计", "127.0.0.1:0", "127.0.0.1:0",
                        archivedTotal.LoopbackSentBytes, archivedTotal.LoopbackReceivedBytes, 0, 0));
            }

            var processInfo = ResolveProcessInfo(identity);

            result.Add(new NetworkTrafficProcessMeasurement(identity.ProcessId, identity.ProcessStartTicks,
                processInfo.Name, processInfo.Path, sent, received, sentRate * 8d, receivedRate * 8d, flows));
        }
        return result;
    }

    public void Dispose()
    {
        Stop();
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private readonly ConcurrentDictionary<ProcessIdentity, ProcessInfo> _processInfoCache = new();

    private ProcessInfo ResolveProcessInfo(ProcessIdentity identity)
    {
        if (_processInfoCache.TryGetValue(identity, out var cached)) return cached;
        var processName = $"PID {identity.ProcessId}";
        var processPath = string.Empty;
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            processName = process.ProcessName;
            processPath = process.MainModule?.FileName ?? string.Empty;
        }
        catch { }
        var info = new ProcessInfo(processName, processPath);
        _processInfoCache[identity] = info;
        return info;
    }

    private void ArchiveInactiveFlows()
    {
        var cutoff = DateTimeOffset.Now - InactiveFlowRetention;
        foreach (var pair in _flows.ToArray())
        {
            lock (pair.Value.Sync)
            {
                if (pair.Value.Archived || pair.Value.LastSampleAt >= cutoff ||
                    !_flows.TryRemove(new KeyValuePair<FlowKey, FlowTotal>(pair.Key, pair.Value))) continue;
                pair.Value.Archived = true;
                var sent = pair.Value.SentBytes;
                var received = pair.Value.ReceivedBytes;
                var loopback = IsLoopbackEndpoint(pair.Key.LocalEndpoint) || IsLoopbackEndpoint(pair.Key.RemoteEndpoint);
                var identity = new ProcessIdentity(pair.Key.ProcessId, pair.Key.ProcessStartTicks);
                _archivedProcesses.AddOrUpdate(identity,
                    _ => new ArchivedProcessTotal(sent, received,
                        loopback ? 0 : sent, loopback ? 0 : received,
                        loopback ? sent : 0, loopback ? received : 0),
                    (_, current) => current.Add(sent, received, loopback));
            }
        }
    }

    private void RecordSample(FlowKey key, NetworkFlowSample sample)
    {
        while (true)
        {
            var total = _flows.GetOrAdd(key, _ => new FlowTotal());
            lock (total.Sync)
            {
                if (total.Archived) continue;
                total.SentBytes += sample.SentBytes;
                total.ReceivedBytes += sample.ReceivedBytes;
                total.LastSentBytes += sample.SentBytes;
                total.LastReceivedBytes += sample.ReceivedBytes;
                total.LastSampleAt = sample.CapturedAt;
                return;
            }
        }
    }

    private static bool IsLoopbackEndpoint(string endpoint)
    {
        var host = endpoint.StartsWith("[", StringComparison.Ordinal)
            ? endpoint[1..Math.Max(1, endpoint.IndexOf(']'))]
            : endpoint[..Math.Max(0, endpoint.LastIndexOf(':'))];
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsWildcardRemote(string value) => value is "0.0.0.0:0" or "[::]:0" or "*:*" or "*:*";
    private readonly record struct FlowKey(int ProcessId, long ProcessStartTicks, string Protocol, string LocalEndpoint, string RemoteEndpoint);
    private readonly record struct ProcessIdentity(int ProcessId, long ProcessStartTicks);
    private readonly record struct ActiveFlowSnapshot(
        FlowKey Key, long SentBytes, long ReceivedBytes, long SentRateBytes, long ReceivedRateBytes);
    private readonly record struct ProcessInfo(string Name, string Path);
    private readonly record struct ArchivedProcessTotal(
        long SentBytes, long ReceivedBytes,
        long ExternalSentBytes, long ExternalReceivedBytes,
        long LoopbackSentBytes, long LoopbackReceivedBytes)
    {
        public ArchivedProcessTotal Add(long sent, long received, bool loopback) => new(
            SentBytes + sent, ReceivedBytes + received,
            ExternalSentBytes + (loopback ? 0 : sent), ExternalReceivedBytes + (loopback ? 0 : received),
            LoopbackSentBytes + (loopback ? sent : 0), LoopbackReceivedBytes + (loopback ? received : 0));
    }
    private sealed class FlowTotal
    {
        public readonly object Sync = new();
        public long SentBytes;
        public long ReceivedBytes;
        public long LastSentBytes;
        public long LastReceivedBytes;
        public DateTimeOffset LastSampleAt;
        public bool Archived;
    }
}

internal sealed record NetworkFlowSample(int ProcessId, long ProcessStartTicks, string Protocol, string LocalEndpoint,
    string RemoteEndpoint, long SentBytes, long ReceivedBytes, DateTimeOffset CapturedAt);
internal readonly record struct NetworkFlowMeasurement(double BitsPerSecond, double TotalBytes, double UploadBitsPerSecond, double DownloadBitsPerSecond);

internal sealed record NetworkTrafficProcessMeasurement(
    int ProcessId,
    long ProcessStartTicks,
    string ProcessName,
    string ProcessPath,
    long SentBytes,
    long ReceivedBytes,
    double UploadBitsPerSecond,
    double DownloadBitsPerSecond,
    IReadOnlyList<NetworkTrafficFlowMeasurement> Flows)
{
    public long TotalBytes => SentBytes + ReceivedBytes;
    public double TotalBitsPerSecond => UploadBitsPerSecond + DownloadBitsPerSecond;
}

internal sealed record NetworkTrafficFlowMeasurement(
    string Protocol,
    string LocalEndpoint,
    string RemoteEndpoint,
    long SentBytes,
    long ReceivedBytes,
    long SentBytesPerSecond,
    long ReceivedBytesPerSecond)
{
    public long TotalBytes => SentBytes + ReceivedBytes;
}
