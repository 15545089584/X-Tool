using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ScreenshotApp.SystemTools;

/// <summary>普通权限主程序与管理员传感器助手之间的单向快照通道。</summary>
internal sealed class HardwareSensorClient : IDisposable
{
    private NamedPipeServerStream? _pipe;
    private Process? _helper;
    private CancellationTokenSource? _cancellation;
    public event EventHandler<HardwareSensorSnapshot>? SnapshotReceived;
    public event EventHandler<string>? Failed;

    public async Task StartAsync()
    {
        if (_cancellation is not null) return;
        var helperPath = Path.Combine(AppContext.BaseDirectory, "tools", "hardware-sensor", "HardwareSensorHelper.exe");
        if (!File.Exists(helperPath)) { Failed?.Invoke(this, "高级传感器助手未随程序发布，请重新构建或安装 X-Tool。"); return; }
        var pipeName = $"xtool-hardware-{Guid.NewGuid():N}";
        _cancellation = new CancellationTokenSource();
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            _helper = Process.Start(new ProcessStartInfo(helperPath, $"--pipe {pipeName}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (_helper is null) throw new InvalidOperationException("无法启动高级传感器助手。");
            await _pipe.WaitForConnectionAsync(_cancellation.Token).WaitAsync(TimeSpan.FromSeconds(20), _cancellation.Token);
            _ = ReadLoopAsync(_pipe, _cancellation.Token);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) { Failed?.Invoke(this, "已取消管理员授权，高级传感器未启动。"); Stop(); }
        catch (Exception exception) { Failed?.Invoke(this, $"高级传感器启动失败：{exception.Message}"); Stop(); }
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().WaitAsync(token);
                if (line is null) break;
                var payload = JsonSerializer.Deserialize<HardwareSensorSnapshot>(line);
                if (payload is not null) SnapshotReceived?.Invoke(this, payload);
            }
            if (!token.IsCancellationRequested) Failed?.Invoke(this, "高级传感器助手已断开。");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Failed?.Invoke(this, $"高级传感器读取失败：{exception.Message}"); }
    }

    public void Stop()
    {
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null;
        _pipe?.Dispose(); _pipe = null;
        try { if (_helper is { HasExited: false }) _helper.Kill(entireProcessTree: true); } catch { }
        _helper?.Dispose(); _helper = null;
    }
    public void Dispose() => Stop();
}

internal sealed record HardwareSensorSnapshot(DateTimeOffset CapturedAt, HardwareSensorValue[] Sensors);
internal sealed record HardwareSensorValue(string HardwareType, string Name, string Type, float Value);
