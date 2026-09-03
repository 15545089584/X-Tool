using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ScreenshotApp.NetworkWorkbench;

/// <summary>
/// 管理员 ETW 辅助进程。仅聚合网络事件，不读取数据包正文。
/// </summary>
internal static class NetworkEtwTrafficHelper
{
    private const string SessionNamePrefix = "XTool.Network.";
    private const string SessionName = "XTool.Network.Persistent";
    private const int MaxPendingFlowBuckets = 8192;
    private const int MaxFlowSamplesPerFlush = 1024;
    private const int MaxProcessIdentityCacheEntries = 4096;
    private const int MaxHelperThreadCount = 256;
    private const int MaxHelperHandleCount = 4096;
    private const long MaxHelperPrivateMemoryBytes = 768L * 1024 * 1024;
    private static readonly TimeSpan ProcessIdentityCacheLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PipeWriteTimeout = TimeSpan.FromSeconds(5);

    public static int Run(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) return 2;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
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
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
            CleanupOrphanedSessions();
            using var session = new TraceEventSession(SessionName);
            session.StopOnDispose = true;
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            var buckets = new ConcurrentDictionary<FlowKey, FlowBucket>();
            var processIdentityCache = new ConcurrentDictionary<int, ProcessIdentityCacheEntry>();
            using var writerCancellation = new CancellationTokenSource();
            var pendingBucketCount = 0;
            var droppedEventCount = 0L;

            void Add(int pid, string protocol, string local, string remote, long sent, long received)
            {
                if (pid <= 0) return;
                var startTicks = GetCachedProcessStartTicks(pid, processIdentityCache);
                var key = new FlowKey(pid, startTicks, protocol, local, remote);
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    if (Interlocked.Increment(ref pendingBucketCount) > MaxPendingFlowBuckets)
                    {
                        Interlocked.Decrement(ref pendingBucketCount);
                        Interlocked.Increment(ref droppedEventCount);
                        return;
                    }

                    var candidate = new FlowBucket();
                    if (buckets.TryAdd(key, candidate))
                    {
                        bucket = candidate;
                    }
                    else
                    {
                        Interlocked.Decrement(ref pendingBucketCount);
                        if (!buckets.TryGetValue(key, out bucket))
                        {
                            Interlocked.Increment(ref droppedEventCount);
                            return;
                        }
                    }
                }
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

            // 单一异步写循环确保管道写入绝不重入。旧实现的周期 Timer 会在写端阻塞时
            // 不断叠加线程池回调，最终耗尽线程、句柄和虚拟内存。
            var writerTask = RunWriterLoopAsync(
                writer,
                buckets,
                processIdentityCache,
                () => Volatile.Read(ref pendingBucketCount),
                () => Interlocked.Exchange(ref droppedEventCount, 0),
                () => session.Source.StopProcessing(),
                writerCancellation.Token,
                () => Interlocked.Decrement(ref pendingBucketCount));

            try
            {
                session.Source.Process();
            }
            finally
            {
                writerCancellation.Cancel();
            }

            return writerTask.GetAwaiter().GetResult() ? 0 : 4;
        }
        catch
        {
            return 1;
        }
    }

    internal static async Task<bool> RunWriterLoopAsync(
        StreamWriter writer,
        ConcurrentDictionary<FlowKey, FlowBucket> buckets,
        ConcurrentDictionary<int, ProcessIdentityCacheEntry> processIdentityCache,
        Func<int> getPendingBucketCount,
        Func<long> takeDroppedEventCount,
        Action stopProcessing,
        CancellationToken cancellationToken,
        Action bucketRemoved)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!HelperResourcesAreWithinLimits())
                {
                    stopProcessing();
                    return false;
                }

                if (processIdentityCache.Count > MaxProcessIdentityCacheEntries)
                {
                    TrimProcessIdentityCache(processIdentityCache);
                }

                var capturedAt = DateTimeOffset.UtcNow;
                var samples = new List<NetworkFlowSample>(Math.Min(getPendingBucketCount(), MaxFlowSamplesPerFlush) + 1);
                foreach (var pair in buckets)
                {
                    if (samples.Count >= MaxFlowSamplesPerFlush) break;
                    if (!buckets.TryRemove(pair.Key, out var value)) continue;
                    bucketRemoved();
                    samples.Add(new NetworkFlowSample(
                        pair.Key.ProcessId,
                        pair.Key.ProcessStartTicks,
                        pair.Key.Protocol,
                        pair.Key.LocalEndpoint,
                        pair.Key.RemoteEndpoint,
                        Interlocked.Read(ref value.SentBytes),
                        Interlocked.Read(ref value.ReceivedBytes),
                        capturedAt));
                }

                // PID 0 是协议心跳，主进程会忽略；SentBytes 同时携带本周期被限流的事件数，
                // 为后续诊断保留信息但不会污染任何进程的流量统计。
                samples.Add(new NetworkFlowSample(
                    0, 0, string.Empty, string.Empty, string.Empty,
                    takeDroppedEventCount(), 0, capturedAt));

                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                writeTimeout.CancelAfter(PipeWriteTimeout);
                foreach (var sample in samples)
                {
                    var json = JsonSerializer.Serialize(sample);
                    await writer.WriteLineAsync(json.AsMemory(), writeTimeout.Token).ConfigureAwait(false);
                }
                await writer.FlushAsync(writeTimeout.Token).ConfigureAwait(false);

                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) break;
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return true;
        }
        catch
        {
            // 写入超时、管道断开或资源越界时立即停止 ETW 处理，让辅助进程自行退出。
            try { stopProcessing(); } catch { }
            return false;
        }
    }

    private static long GetCachedProcessStartTicks(
        int processId,
        ConcurrentDictionary<int, ProcessIdentityCacheEntry> cache)
    {
        var now = DateTimeOffset.UtcNow;
        if (cache.TryGetValue(processId, out var cached) && cached.ExpiresAt > now)
        {
            return cached.StartTicks;
        }

        if (cache.Count >= MaxProcessIdentityCacheEntries && !cache.ContainsKey(processId))
        {
            // 极端进程风暴下宁可暂时只按 PID 归类，也不继续扩大辅助进程缓存。
            return 0;
        }

        var startTicks = GetProcessStartTicks(processId);
        cache[processId] = new ProcessIdentityCacheEntry(
            startTicks,
            now.Add(startTicks == 0 ? TimeSpan.FromSeconds(5) : ProcessIdentityCacheLifetime));
        return startTicks;
    }

    private static void TrimProcessIdentityCache(ConcurrentDictionary<int, ProcessIdentityCacheEntry> cache)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in cache)
        {
            if (pair.Value.ExpiresAt <= now) cache.TryRemove(pair.Key, out _);
        }

        // 异常的进程创建风暴下也必须保持硬上限；PID 复用最多造成短时间归属误差，
        // 不能允许诊断功能本身再次成为系统资源耗尽源。
        if (cache.Count > MaxProcessIdentityCacheEntries) cache.Clear();
    }

    private static bool HelperResourcesAreWithinLimits()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.PrivateMemorySize64 <= MaxHelperPrivateMemoryBytes &&
                   process.Threads.Count <= MaxHelperThreadCount &&
                   process.HandleCount <= MaxHelperHandleCount;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupOrphanedSessions()
    {
        // 异常关机可能绕过 Dispose，下一次启动只清理由 X-Tool 自己命名的遗留会话。
        foreach (var activeSessionName in TraceEventSession.GetActiveSessionNames())
        {
            if (!activeSessionName.StartsWith(SessionNamePrefix, StringComparison.Ordinal)) continue;
            try
            {
                using var orphanedSession = new TraceEventSession(activeSessionName);
                orphanedSession.Stop();
            }
            catch
            {
                // 单个遗留会话清理失败不应阻止其余会话或本次采集继续尝试。
            }
        }
    }

    private static long GetProcessStartTicks(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.StartTime.ToUniversalTime().Ticks; }
        catch { return 0; }
    }

    private static string Endpoint(System.Net.IPAddress address, int port) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

    internal readonly record struct ProcessIdentityCacheEntry(long StartTicks, DateTimeOffset ExpiresAt);
    internal readonly record struct FlowKey(int ProcessId, long ProcessStartTicks, string Protocol, string LocalEndpoint, string RemoteEndpoint);
    internal sealed class FlowBucket { public long SentBytes; public long ReceivedBytes; }
}

internal sealed class NetworkEtwTrafficClient : IDisposable
{
    private static readonly TimeSpan InactiveFlowRetention = TimeSpan.FromMinutes(5);
    private const int MaxActiveFlowCount = 65536;
    private const int MaxArchivedProcessCount = 4096;
    private const int MaxProcessInfoCacheCount = 4096;
    private readonly ConcurrentDictionary<FlowKey, FlowTotal> _flows = new();
    private readonly ConcurrentDictionary<ProcessIdentity, ArchivedProcessTotal> _archivedProcesses = new();
    private readonly object _measurementSync = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
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
        await _startGate.WaitAsync();
        try
        {
            if (IsRunning) return (true, "逐连接精确监测正在运行");
            BeginSession();
            var pipeName = $"XTool.NetworkEtw.{Environment.ProcessId}.{Guid.NewGuid():N}";
            return await StartWithPipeAsync(pipeName, launchPersistentTask: false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<(bool Success, string Message)> StartPersistentAsync()
    {
        await _startGate.WaitAsync();
        try
        {
            if (IsRunning) return (true, "独立 ETW 辅助进程正在运行");
            BeginSession();
            return await StartWithPipeAsync(NetworkEtwAutoStartService.PersistentPipeName, launchPersistentTask: true);
        }
        finally
        {
            _startGate.Release();
        }
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
        NamedPipeServerStream? server = null;
        try
        {
            // 固定管道在旧启动尚未完全退出时可能短暂被占用；必须转为可恢复结果。
            server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            _server = server;
            if (launchPersistentTask)
            {
                var taskResult = await NetworkEtwAutoStartService.RunTaskAsync();
                if (!taskResult.Success)
                {
                    server?.Dispose();
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
                    server?.Dispose();
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
            server?.Dispose();
            _server = null;
            try { if (!_persistentTaskConnection && _helperProcess is { HasExited: false }) _helperProcess.Kill(); } catch { }
            StatusText = "用户取消管理员授权";
            return (false, StatusText);
        }
        catch (Exception exception)
        {
            server?.Dispose();
            _server = null;
            try { if (!_persistentTaskConnection && _helperProcess is { HasExited: false }) _helperProcess.Kill(); } catch { }
            _persistentTaskConnection = false;
            StatusText = exception is IOException
                ? "精确监测启动失败：已有旧连接正在释放，请稍候重试。"
                : $"精确监测启动失败：{exception.Message}";
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
        if (_processInfoCache.TryGetValue(identity, out var cached) && !string.IsNullOrWhiteSpace(cached.Path)) return cached;
        var processName = cached.Name ?? $"PID {identity.ProcessId}";
        var processPath = string.Empty;
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            processName = process.ProcessName;
            processPath = TryGetProcessPath(identity.ProcessId);
        }
        catch { }
        var info = new ProcessInfo(processName, processPath);
        if (_processInfoCache.Count < MaxProcessInfoCacheCount || _processInfoCache.ContainsKey(identity))
            _processInfoCache[identity] = info;
        return info;
    }

    private static string TryGetProcessPath(int processId)
    {
        try
        {
            using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (processHandle.IsInvalid) return string.Empty;
            var capacity = 32768;
            var path = new StringBuilder(capacity);
            return QueryFullProcessImageName(processHandle, 0, path, ref capacity) ? path.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle processHandle,
        int flags,
        StringBuilder executablePath,
        ref int size);

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
                if (_archivedProcesses.Count >= MaxArchivedProcessCount && !_archivedProcesses.ContainsKey(identity)) continue;
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
            if (!_flows.TryGetValue(key, out var total))
            {
                if (_flows.Count >= MaxActiveFlowCount) return;
                total = _flows.GetOrAdd(key, _ => new FlowTotal());
            }
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
