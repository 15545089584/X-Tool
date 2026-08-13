using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ScreenshotApp.HardwareMonitoring;

namespace XTool.HardwareSensorAgent;

internal static class Program
{
    private const int MaximumLaunchRequestBytes = 8192;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly string InstallerResultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "X-Tool",
        "HardwareSensors",
        "last-installer-result.json");

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        bool installerMode = args.Length > 0 && (args[0] is "--install" or "--uninstall");
        try
        {
            AgentArguments options = AgentArguments.Parse(args);
            int exitCode = options.Mode switch
            {
                AgentMode.Install => AgentInstallationManager.Install(
                    options.SourceDirectory!,
                    options.ManifestPath!,
                    options.RequestingUserSid!),
                AgentMode.Uninstall => AgentInstallationManager.Uninstall(options.RequestingUserSid!),
                AgentMode.Serve => await ServeAsync(options.UserSid!, options.ProtocolVersion).ConfigureAwait(false),
                _ => 1
            };
            if (installerMode)
            {
                WriteInstallerResult(exitCode, exitCode == 0 ? "管理员代理任务已更新" : "安装器未能完成操作");
            }
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            if (installerMode) WriteInstallerResult(1, "操作已取消");
            return 0;
        }
        catch (ArgumentException exception)
        {
            if (installerMode) WriteInstallerResult(2, exception.Message);
            return 2;
        }
        catch (UnauthorizedAccessException exception)
        {
            if (installerMode) WriteInstallerResult(3, exception.Message);
            return 3;
        }
        catch (InvalidDataException exception)
        {
            if (installerMode) WriteInstallerResult(4, exception.Message);
            return 4;
        }
        catch (Exception exception)
        {
            if (installerMode) WriteInstallerResult(1, exception.GetBaseException().Message);
            return 1;
        }
    }

    private static void WriteInstallerResult(int exitCode, string? message)
    {
        try
        {
            string directory = Path.GetDirectoryName(InstallerResultPath)!;
            Directory.CreateDirectory(directory);
            string normalized = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (normalized.Length > 300) normalized = normalized[..300];
            string temporary = $"{InstallerResultPath}.{Guid.NewGuid():N}.tmp";
            string payload = JsonSerializer.Serialize(new { exitCode, message = normalized });
            File.WriteAllText(temporary, payload, new UTF8Encoding(false));
            File.Move(temporary, InstallerResultPath, true);
        }
        catch
        {
            // 安装结果只是诊断辅助，不能改变代理进程的退出语义。
        }
    }

    private static async Task<int> ServeAsync(string expectedUserSid, int protocolVersion)
    {
        if (protocolVersion != HardwareSensorProtocol.Version)
        {
            throw new InvalidDataException("计划任务使用的传感器协议版本不受支持。");
        }

        if (!string.Equals(expectedUserSid, AgentArguments.GetCurrentUserSid(), StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("计划任务用户与当前 Windows 用户不匹配。");
        }

        HardwareAgentLaunchRequest request = ReadAndConsumeLaunchRequest();
        ValidatedLaunchRequest launch = AgentArguments.ValidateLaunchRequest(request, expectedUserSid);
        using Process parentProcess = ParentProcessValidator.Validate(
            launch.ParentProcessId,
            launch.ParentProcessStartTimeUtcTicks,
            launch.ParentExecutablePath);
        using CancellationTokenSource lifetime = new();
        Task parentExitTask = WatchParentExitAsync(parentProcess, lifetime.Token);

        await using NamedPipeClientStream pipe = new(
            ".",
            launch.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        using CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        connectTimeout.CancelAfter(ConnectTimeout);
        await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        VerifyPipeOwner(pipe, launch.ParentProcessId);

        Task<int> monitorTask = RunMonitorAsync(
            pipe,
            launch.Nonce,
            launch.ParentProcessId,
            launch.ParentProcessStartTimeUtcTicks,
            lifetime.Token);
        Task completed = await Task.WhenAny(monitorTask, parentExitTask).ConfigureAwait(false);
        lifetime.Cancel();
        return completed == parentExitTask ? 0 : await monitorTask.ConfigureAwait(false);
    }

    private static HardwareAgentLaunchRequest ReadAndConsumeLaunchRequest()
    {
        string requestDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "X-Tool",
            "HardwareSensors");
        string requestPath = Path.Combine(requestDirectory, "launch-v2.json");

        FileInfo info = new(requestPath);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("没有找到有效的硬件传感器启动请求。");
        }

        if (info.Length <= 0 || info.Length > MaximumLaunchRequestBytes)
        {
            throw new InvalidDataException("硬件传感器启动请求长度无效。");
        }

        byte[] payload;
        using (FileStream stream = new(requestPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            payload = new byte[checked((int)stream.Length)];
            stream.ReadExactly(payload);
        }

        // 单次启动请求消费后立即删除，防止相同请求被重复使用。
        File.Delete(requestPath);
        return JsonSerializer.Deserialize<HardwareAgentLaunchRequest>(payload, HardwareSensorProtocol.JsonOptions)
            ?? throw new InvalidDataException("硬件传感器启动请求格式无效。");
    }

    private static async Task<int> RunMonitorAsync(
        NamedPipeClientStream pipe,
        string nonce,
        int parentProcessId,
        long parentProcessStartTimeUtcTicks,
        CancellationToken cancellationToken)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        await HardwareSensorProtocol.WriteFrameAsync(
            pipe,
            new HardwareAgentHelloMessage(
                HardwareSensorProtocol.Version,
                "hello",
                nonce,
                Environment.ProcessId,
                version,
                DateTimeOffset.UtcNow,
                ["cpu", "gpu", "memory", "motherboard", "storage", "controller", "psu"]),
            cancellationToken).ConfigureAwait(false);

        using (CancellationTokenSource handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            handshakeTimeout.CancelAfter(HandshakeTimeout);
            HardwareAgentHelloAckMessage acknowledgement = await HardwareSensorProtocol.ReadFrameAsync<HardwareAgentHelloAckMessage>(
                pipe,
                handshakeTimeout.Token).ConfigureAwait(false);
            ValidateHelloAcknowledgement(
                acknowledgement,
                nonce,
                parentProcessId,
                parentProcessStartTimeUtcTicks);
        }

        await WriteStatusAsync(pipe, nonce, "initializing", "正在初始化只读硬件传感器。", 0, cancellationToken)
            .ConfigureAwait(false);

        using HardwareSensorCollector collector = new();
        try
        {
            collector.Open();
        }
        catch (Exception ex)
        {
            await TryWriteErrorAsync(pipe, nonce, "sensor_initialization_failed", ex, true, cancellationToken)
                .ConfigureAwait(false);
            return 2;
        }

        await WriteStatusAsync(
            pipe,
            nonce,
            "monitoring",
            "高级硬件传感器正在运行。",
            collector.HardwareCount,
            cancellationToken).ConfigureAwait(false);

        using PeriodicTimer timer = new(SampleInterval);
        long sequence = 0;
        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            try
            {
                (IReadOnlyList<HardwareSensorReading> sensors, IReadOnlyList<string> warnings) = collector.Capture();
                await HardwareSensorProtocol.WriteFrameAsync(
                    pipe,
                    new HardwareSensorSnapshotMessage(
                        HardwareSensorProtocol.Version,
                        "snapshot",
                        nonce,
                        DateTimeOffset.UtcNow,
                        ++sequence,
                        collector.HardwareCount,
                        sensors,
                        warnings.Take(20).ToArray()),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return 0;
            }
            catch (Exception ex)
            {
                await TryWriteErrorAsync(pipe, nonce, "sampling_failed", ex, false, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        return 0;
    }

    private static async Task WriteStatusAsync(
        Stream pipe,
        string nonce,
        string state,
        string message,
        int hardwareCount,
        CancellationToken cancellationToken)
    {
        await HardwareSensorProtocol.WriteFrameAsync(
            pipe,
            new HardwareAgentStatusMessage(
                HardwareSensorProtocol.Version,
                "status",
                nonce,
                DateTimeOffset.UtcNow,
                state,
                message,
                hardwareCount),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryWriteErrorAsync(
        Stream pipe,
        string nonce,
        string code,
        Exception exception,
        bool fatal,
        CancellationToken cancellationToken)
    {
        try
        {
            // 代理错误可能来自驱动或文件系统，不能把异常原文跨权限边界发送到界面。
            string message = code switch
            {
                "sensor_initialization_failed" => "只读硬件传感器初始化失败，请重新授权或检查系统安全策略。",
                "sampling_failed" => "本次硬件传感器采样失败，代理将在下一周期重试。",
                _ => "硬件传感器代理操作失败。"
            };

            await HardwareSensorProtocol.WriteFrameAsync(
                pipe,
                new HardwareAgentErrorMessage(
                    HardwareSensorProtocol.Version,
                    "error",
                    nonce,
                    DateTimeOffset.UtcNow,
                    code,
                    message,
                    fatal),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 管道已经断开时无需继续报告错误，代理会立即结束。
        }
    }

    private static async Task WatchParentExitAsync(Process parentProcess, CancellationToken cancellationToken)
    {
        try
        {
            await parentProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void ValidateHelloAcknowledgement(
        HardwareAgentHelloAckMessage acknowledgement,
        string nonce,
        int expectedParentProcessId,
        long expectedParentProcessStartTimeUtcTicks)
    {
        if (acknowledgement.ProtocolVersion != HardwareSensorProtocol.Version
            || !string.Equals(acknowledgement.Type, "helloAck", StringComparison.Ordinal)
            || !string.Equals(acknowledgement.Nonce, nonce, StringComparison.Ordinal)
            || acknowledgement.ParentProcessId != expectedParentProcessId
            || acknowledgement.ParentProcessStartTimeUtcTicks != expectedParentProcessStartTimeUtcTicks)
        {
            throw new InvalidDataException("主程序返回的传感器握手确认无效。");
        }

        TimeSpan age = DateTimeOffset.UtcNow - acknowledgement.TimestampUtc;
        if (age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromSeconds(30))
        {
            throw new InvalidDataException("主程序返回的传感器握手确认已过期。");
        }
    }

    private static void VerifyPipeOwner(NamedPipeClientStream pipe, int expectedServerProcessId)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverProcessId)
            || serverProcessId != (uint)expectedServerProcessId)
        {
            throw new UnauthorizedAccessException("传感器管道服务器不是请求中的 XTool 主程序。");
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint serverProcessId);
}
