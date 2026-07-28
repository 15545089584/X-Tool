using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Security.Principal;

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
        var userKey = (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName).Replace('\\', '-');
        var pipeName = $"xtool-hardware-{userKey}";
        var taskName = $"X-Tool Hardware Sensors ({userKey})";
        _cancellation = new CancellationTokenSource();
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            if (!ScheduledTaskExists(taskName))
            {
                _helper = Process.Start(new ProcessStartInfo(helperPath, $"--install-task \"{taskName}\" \"{pipeName}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                if (_helper is null) throw new InvalidOperationException("无法安装高级传感器授权任务。");
                await _helper.WaitForExitAsync(_cancellation.Token);
                if (_helper.ExitCode != 0) throw new InvalidOperationException("高级传感器授权任务安装失败。");
                _helper.Dispose();
                _helper = null;
            }
            RunScheduledTask(taskName);
            await _pipe.WaitForConnectionAsync(_cancellation.Token).WaitAsync(TimeSpan.FromSeconds(20), _cancellation.Token);
            _ = ReadLoopAsync(_pipe, _cancellation.Token);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) { Failed?.Invoke(this, "已取消管理员授权，高级传感器未启动。"); Stop(); }
        catch (Exception exception) { Failed?.Invoke(this, $"高级传感器启动失败：{exception.Message}"); Stop(); }
    }

    private static bool ScheduledTaskExists(string taskName) => RunTaskCommand("/Query", "/TN", taskName) == 0;

    private static void RunScheduledTask(string taskName)
    {
        var exitCode = RunTaskCommand("/Run", "/TN", taskName);
        if (exitCode != 0) throw new InvalidOperationException("无法启动已授权的高级传感器任务。");
    }

    private static int RunTaskCommand(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        if (process is null) return -1;
        process.WaitForExit(10000);
        return process.HasExited ? process.ExitCode : -1;
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
internal sealed record HardwareSensorValue(string HardwareType, string HardwareName, string HardwareIdentifier, string Name, string Type, float Value);
