using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenshotApp.SystemTools;
using Microsoft.Win32.SafeHandles;

namespace ScreenshotApp.HardwareMonitoring;

/// <summary>在普通权限主程序中管理高权限只读传感器代理的连接与生命周期。</summary>
public sealed class HardwareSensorMonitorCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private NamedPipeServerStream? _pipe;
    private Task? _readerTask;
    private long _lastSequence;

    public event Action<HardwareMonitorSnapshot>? SnapshotAvailable;

    public bool IsRunning => _readerTask is { IsCompleted: false };

    public async Task StartAsync(bool silent = false, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            if (_readerTask is not null || _pipe is not null || _lifetime is not null)
            {
                await CleanupConnectionAsync(endTask: true).ConfigureAwait(false);
            }

            HardwareSensorAuthorizationStatus authorization = await HardwareSensorAuthorizationService.GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            if (authorization.State != HardwareSensorAuthorizationState.Authorized)
            {
                Publish(HardwareMonitorConnectionState.Disabled, authorization.Message);
                if (!silent && authorization.State is HardwareSensorAuthorizationState.RepairRequired or HardwareSensorAuthorizationState.Unavailable)
                {
                    Publish(HardwareMonitorConnectionState.Unavailable, authorization.Message);
                }
                return;
            }

            Publish(HardwareMonitorConnectionState.Connecting, "正在启动独立管理员传感器代理");
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            string pipeName = $"{HardwareSensorProtocol.PipePrefix}.{HardwareSensorProtocol.GetUserKey(HardwareSensorAuthorizationService.CurrentUserSid)}.{nonce}";
            _pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            Process currentProcess = Process.GetCurrentProcess();
            var request = new HardwareAgentLaunchRequest(
                HardwareSensorProtocol.Version,
                "launch",
                nonce,
                Environment.ProcessId,
                currentProcess.StartTime.ToUniversalTime().Ticks,
                Environment.ProcessPath ?? throw new InvalidOperationException("无法读取主程序路径。"),
                HardwareSensorAuthorizationService.CurrentUserSid,
                pipeName,
                DateTimeOffset.UtcNow);
            await WriteLaunchRequestAsync(request, _lifetime.Token).ConfigureAwait(false);
            await HardwareSensorAuthorizationService.EndTaskAsync(_lifetime.Token).ConfigureAwait(false);
            await HardwareSensorAuthorizationService.RunTaskAsync(_lifetime.Token).ConfigureAwait(false);

            using CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);

            JsonElement helloFrame = await HardwareSensorProtocol.ReadFrameAsync<JsonElement>(_pipe, connectTimeout.Token).ConfigureAwait(false);
            HardwareAgentHelloMessage hello = helloFrame.Deserialize<HardwareAgentHelloMessage>(HardwareSensorProtocol.JsonOptions)
                ?? throw new InvalidDataException("传感器代理握手为空。");
            ValidateHello(hello, nonce);
            ValidateConnectedAgent(_pipe, hello.AgentProcessId);
            await HardwareSensorProtocol.WriteFrameAsync(
                _pipe,
                new HardwareAgentHelloAckMessage(
                    HardwareSensorProtocol.Version,
                    "helloAck",
                    nonce,
                    Environment.ProcessId,
                    currentProcess.StartTime.ToUniversalTime().Ticks,
                    DateTimeOffset.UtcNow),
                connectTimeout.Token).ConfigureAwait(false);

            _lastSequence = 0;
            _readerTask = ReadLoopAsync(_pipe, nonce, _lifetime.Token);
            _ = ObserveReaderAsync(_readerTask, _lifetime);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupConnectionAsync(endTask: true).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await CleanupConnectionAsync(endTask: true).ConfigureAwait(false);
            Publish(HardwareMonitorConnectionState.Error, $"传感器代理启动失败：{DescribeFailure(exception, startup: true)}");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CleanupConnectionAsync(endTask: true).ConfigureAwait(false);
            Publish(HardwareMonitorConnectionState.Disabled, "硬件实时监控已停止");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe, string nonce, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            JsonElement frame = await HardwareSensorProtocol.ReadFrameAsync<JsonElement>(pipe, cancellationToken).ConfigureAwait(false);
            if (!frame.TryGetProperty("type", out JsonElement typeElement))
            {
                throw new InvalidDataException("传感器代理消息缺少类型。");
            }

            switch (typeElement.GetString())
            {
                case "status":
                    HardwareAgentStatusMessage status = Deserialize<HardwareAgentStatusMessage>(frame);
                    ValidateEnvelope(status.ProtocolVersion, status.Nonce, status.TimestampUtc, nonce);
                    Publish(
                        status.State == "monitoring" ? HardwareMonitorConnectionState.Connected : HardwareMonitorConnectionState.Connecting,
                        Limit(status.Message));
                    break;
                case "snapshot":
                    HardwareSensorSnapshotMessage snapshot = Deserialize<HardwareSensorSnapshotMessage>(frame);
                    ValidateSnapshot(snapshot, nonce);
                    HardwareMonitorSensorValue[] mappedSensors = snapshot.Sensors.Select(MapSensor).ToArray();
                    string warningSummary = snapshot.Warnings.Count == 0
                        ? "采样正常，未发现警告"
                        : $"采样警告 {snapshot.Warnings.Count} 项：{Limit(snapshot.Warnings[0])}";
                    SnapshotAvailable?.Invoke(new HardwareMonitorSnapshot(
                        snapshot.TimestampUtc,
                        HardwareMonitorConnectionState.Connected,
                        warningSummary,
                        "独立管理员传感器代理",
                        mappedSensors,
                        snapshot.HardwareCount,
                        snapshot.Warnings.Count,
                        Limit(snapshot.LowLevelAccessSummary)));
                    break;
                case "error":
                    HardwareAgentErrorMessage error = Deserialize<HardwareAgentErrorMessage>(frame);
                    ValidateEnvelope(error.ProtocolVersion, error.Nonce, error.TimestampUtc, nonce);
                    Publish(error.Fatal ? HardwareMonitorConnectionState.Error : HardwareMonitorConnectionState.Unavailable, Limit(error.Message));
                    if (error.Fatal) return;
                    break;
                default:
                    throw new InvalidDataException("传感器代理发送了未允许的消息类型。");
            }
        }
    }

    private async Task ObserveReaderAsync(Task readerTask, CancellationTokenSource owner)
    {
        try
        {
            await readerTask.ConfigureAwait(false);
            if (!owner.IsCancellationRequested)
            {
                Publish(HardwareMonitorConnectionState.Unavailable, "传感器代理连接已结束");
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Publish(HardwareMonitorConnectionState.Error, $"传感器连接异常：{DescribeFailure(exception, startup: false)}");
        }
    }

    private async Task CleanupConnectionAsync(bool endTask)
    {
        CancellationTokenSource? lifetime = _lifetime;
        _lifetime = null;
        lifetime?.Cancel();
        _pipe?.Dispose();
        _pipe = null;
        Task? reader = _readerTask;
        _readerTask = null;
        if (reader is not null)
        {
            try { await reader.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        }
        lifetime?.Dispose();
        DeleteLaunchRequest();
        if (endTask) await HardwareSensorAuthorizationService.EndTaskAsync().ConfigureAwait(false);
    }

    private static async Task WriteLaunchRequestAsync(HardwareAgentLaunchRequest request, CancellationToken cancellationToken)
    {
        string directory = GetLaunchDirectory();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "launch-v2.json");
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, HardwareSensorProtocol.JsonOptions);
        if (payload.Length > 8192) throw new InvalidDataException("硬件传感器启动请求过大。");
        await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, path, true);
    }

    private static void DeleteLaunchRequest()
    {
        try { File.Delete(Path.Combine(GetLaunchDirectory(), "launch-v2.json")); } catch { }
    }

    private static string GetLaunchDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "X-Tool",
        "HardwareSensors");

    private static void ValidateHello(HardwareAgentHelloMessage hello, string nonce)
    {
        ValidateEnvelope(hello.ProtocolVersion, hello.Nonce, hello.TimestampUtc, nonce);
        if (!string.Equals(hello.Type, "hello", StringComparison.Ordinal) || hello.AgentProcessId <= 0)
        {
            throw new InvalidDataException("传感器代理握手内容无效。");
        }
    }

    private void ValidateSnapshot(HardwareSensorSnapshotMessage snapshot, string nonce)
    {
        ValidateEnvelope(snapshot.ProtocolVersion, snapshot.Nonce, snapshot.TimestampUtc, nonce);
        long previous = Interlocked.Read(ref _lastSequence);
        if (snapshot.Sequence <= previous
            || snapshot.HardwareCount < 0
            || snapshot.HardwareCount > HardwareSensorProtocol.MaximumSensorCount
            || snapshot.Sensors.Count > HardwareSensorProtocol.MaximumSensorCount
            || snapshot.Warnings.Count > 20
            || snapshot.LowLevelAccessSummary?.Length > HardwareSensorProtocol.MaximumStringLength)
        {
            throw new InvalidDataException("传感器快照数量或序号无效。");
        }
        Interlocked.Exchange(ref _lastSequence, snapshot.Sequence);
        foreach (HardwareSensorReading sensor in snapshot.Sensors)
        {
            if (!double.IsFinite(sensor.Value)
                || (sensor.Minimum is double minimum && !double.IsFinite(minimum))
                || (sensor.Maximum is double maximum && !double.IsFinite(maximum))
                || new[] { sensor.HardwareId, sensor.HardwareType, sensor.HardwareName, sensor.SensorId, sensor.SensorType, sensor.SensorName, sensor.Unit }
                    .Any(value => value is null || value.Length > HardwareSensorProtocol.MaximumStringLength))
            {
                throw new InvalidDataException("传感器快照包含无效数据。");
            }
        }
    }

    private static void ValidateEnvelope(int version, string nonce, DateTimeOffset timestamp, string expectedNonce)
    {
        if (version != HardwareSensorProtocol.Version || !string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            throw new InvalidDataException("传感器代理消息的协议或随机数无效。");
        TimeSpan age = DateTimeOffset.UtcNow - timestamp;
        if (age < TimeSpan.FromSeconds(-10) || age > TimeSpan.FromMinutes(2))
            throw new InvalidDataException("传感器代理消息时间无效。");
    }

    private static T Deserialize<T>(JsonElement frame) => frame.Deserialize<T>(HardwareSensorProtocol.JsonOptions)
        ?? throw new InvalidDataException("传感器代理消息格式无效。");

    private static HardwareMonitorSensorValue MapSensor(HardwareSensorReading sensor)
    {
        HardwareMonitorMetricKind metric = sensor.SensorType switch
        {
            "Load" => HardwareMonitorMetricKind.Load,
            "Clock" => HardwareMonitorMetricKind.Clock,
            "Voltage" => HardwareMonitorMetricKind.Voltage,
            "Power" => HardwareMonitorMetricKind.Power,
            "Temperature" => HardwareMonitorMetricKind.Temperature,
            "Fan" => HardwareMonitorMetricKind.FanSpeed,
            "Data" when sensor.SensorName.Contains("Used", StringComparison.OrdinalIgnoreCase) => HardwareMonitorMetricKind.MemoryUsed,
            "Data" => HardwareMonitorMetricKind.MemoryTotal,
            _ => HardwareMonitorMetricKind.Load
        };
        HardwareMonitorDeviceKind device = MapDeviceKind(sensor, metric);
        return new(sensor.SensorId, sensor.SensorName, sensor.HardwareId, sensor.HardwareName, device, metric, sensor.Value, sensor.Unit);
    }

    private static HardwareMonitorDeviceKind MapDeviceKind(HardwareSensorReading sensor, HardwareMonitorMetricKind metric)
    {
        HardwareMonitorDeviceKind device = sensor.HardwareType switch
        {
            "Cpu" => HardwareMonitorDeviceKind.Cpu,
            "GpuNvidia" or "GpuAmd" or "GpuIntel" => HardwareMonitorDeviceKind.Gpu,
            "Memory" => HardwareMonitorDeviceKind.Memory,
            "Motherboard" or "SuperIO" => HardwareMonitorDeviceKind.Mainboard,
            "Storage" => HardwareMonitorDeviceKind.Storage,
            "Controller" => HardwareMonitorDeviceKind.Fan,
            _ => HardwareMonitorDeviceKind.Other
        };

        // 笔记本常把 CPU/内存热传感器挂在 SuperIO 或主板节点下；仅对温度
        // 做名称归类，避免把风扇、负载和电压等主板项目误判成 CPU 项目。
        if (metric != HardwareMonitorMetricKind.Temperature
            || device is not (HardwareMonitorDeviceKind.Mainboard or HardwareMonitorDeviceKind.Other))
            return device;

        string name = $"{sensor.HardwareName} {sensor.SensorName}";
        if (ContainsAny(name, "memory", "dram", "dimm", "spd", "pmic", "内存"))
            return HardwareMonitorDeviceKind.Memory;
        if (ContainsAny(name, "gpu", "graphics", "显卡", "video"))
            return HardwareMonitorDeviceKind.Gpu;
        if (ContainsAny(name, "cpu", "package", "core", "die", "tctl", "tdie", "ccd", "peci", "处理器"))
            return HardwareMonitorDeviceKind.Cpu;

        return device;
    }

    private static bool ContainsAny(string value, params string[] terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private void Publish(HardwareMonitorConnectionState state, string detail) => SnapshotAvailable?.Invoke(new(
        DateTimeOffset.Now,
        state,
        Limit(detail),
        "独立管理员传感器代理",
        Array.Empty<HardwareMonitorSensorValue>()));

    private static string Limit(string? value)
    {
        string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 256 ? normalized : normalized[..256];
    }

    private static string DescribeFailure(Exception exception, bool startup)
    {
        Exception failure = exception.GetBaseException();
        return failure switch
        {
            TimeoutException => startup ? "等待代理连接超时" : "读取代理数据超时",
            OperationCanceledException => "操作已取消",
            UnauthorizedAccessException => "代理身份或访问权限校验未通过",
            InvalidDataException => "代理握手或采样数据校验未通过",
            IOException => "安全管道连接已中断",
            System.ComponentModel.Win32Exception => "Windows 任务或进程操作失败",
            _ => startup ? "无法完成代理启动" : "无法继续读取代理数据"
        };
    }

    private static void ValidateConnectedAgent(NamedPipeServerStream pipe, int claimedProcessId)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId) || processId != claimedProcessId)
            throw new UnauthorizedAccessException("无法确认传感器代理进程身份。");
        using SafeProcessHandle process = OpenProcess(0x1000, false, claimedProcessId);
        if (process.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法打开传感器代理进程。");
        string actualPath = QueryProcessPath(process.DangerousGetHandle());
        if (!string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(HardwareSensorAuthorizationService.InstalledAgentPath), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("传感器代理不是已验证的 Program Files 版本。");
    }

    private static string QueryProcessPath(IntPtr processHandle)
    {
        StringBuilder buffer = new(32768);
        int length = buffer.Capacity;
        if (!QueryFullProcessImageName(processHandle, 0, buffer, ref length))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法读取传感器代理路径。");
        return buffer.ToString(0, length);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(int access, bool inheritHandle, int processId);
}
