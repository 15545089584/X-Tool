using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenshotApp.HardwareMonitoring;

/// <summary>
/// 硬件传感器代理与主程序之间的版本化只读协议。
/// </summary>
public static class HardwareSensorProtocol
{
    public const int Version = 2;
    public const int MaximumFrameBytes = 512 * 1024;
    public const int MaximumSensorCount = 2048;
    public const int MaximumStringLength = 256;
    public const string PipePrefix = "XTool.HardwareSensors.v2";
    public const string ScheduledTaskFolder = @"\X-Tool";

    public static string GetScheduledTaskName(string userSid)
    {
        return $"HardwareSensors.v2.{GetUserKey(userSid)}";
    }

    public static string GetUserKey(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(userSid));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    public static string GetScheduledTaskPath(string userSid) =>
        $@"{ScheduledTaskFolder}\{GetScheduledTaskName(userSid)}";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async ValueTask WriteFrameAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length <= 0 || payload.Length > MaximumFrameBytes)
        {
            throw new InvalidDataException($"硬件传感器消息长度无效：{payload.Length} 字节。");
        }

        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T> ReadFrameAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);

        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (payloadLength <= 0 || payloadLength > MaximumFrameBytes)
        {
            throw new InvalidDataException($"硬件传感器消息长度无效：{payloadLength} 字节。");
        }

        byte[] payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidDataException("硬件传感器消息为空或格式无效。");
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("硬件传感器管道已断开。");
            }

            offset += count;
        }
    }
}

public sealed record HardwareAgentHelloMessage(
    int ProtocolVersion,
    string Type,
    string Nonce,
    int AgentProcessId,
    string AgentVersion,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<string> Capabilities);

public sealed record HardwareAgentHelloAckMessage(
    int ProtocolVersion,
    string Type,
    string Nonce,
    int ParentProcessId,
    long ParentProcessStartTimeUtcTicks,
    DateTimeOffset TimestampUtc);

public sealed record HardwareAgentLaunchRequest(
    int ProtocolVersion,
    string Type,
    string Nonce,
    int ParentProcessId,
    long ParentProcessStartTimeUtcTicks,
    string ParentExecutablePath,
    string RequestingUserSid,
    string PipeName,
    DateTimeOffset TimestampUtc);

public sealed record HardwareAgentStatusMessage(
    int ProtocolVersion,
    string Type,
    string Nonce,
    DateTimeOffset TimestampUtc,
    string State,
    string Message,
    int HardwareCount);

public sealed record HardwareAgentErrorMessage(
    int ProtocolVersion,
    string Type,
    string Nonce,
    DateTimeOffset TimestampUtc,
    string Code,
    string Message,
    bool Fatal);

public sealed record HardwareSensorSnapshotMessage(
    int ProtocolVersion,
    string Type,
    string Nonce,
    DateTimeOffset TimestampUtc,
    long Sequence,
    int HardwareCount,
    IReadOnlyList<HardwareSensorReading> Sensors,
    IReadOnlyList<string> Warnings,
    string? LowLevelAccessSummary = null);

public sealed record HardwareSensorReading(
    string HardwareId,
    string HardwareType,
    string HardwareName,
    string? ParentHardwareId,
    string SensorId,
    string SensorType,
    string SensorName,
    string Unit,
    double Value,
    double? Minimum,
    double? Maximum);

/// <summary>普通权限主程序在申请 UAC 前生成的代理安装清单。</summary>
public sealed record HardwareAgentBundleManifest(
    int ProtocolVersion,
    string AgentVersion,
    string RequestingUserSid,
    IReadOnlyList<HardwareAgentBundleFile> Files);

public sealed record HardwareAgentBundleFile(
    string Name,
    long Length,
    string Sha256);
