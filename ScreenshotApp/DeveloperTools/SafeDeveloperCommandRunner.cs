using System.Diagnostics;
using System.IO;
using System.Text;

namespace ScreenshotApp.DeveloperTools;

public sealed record DeveloperCommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public string CombinedOutput => string.Join(Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

/// <summary>只运行已解析出的绝对可执行文件，并统一限制时间与输出规模。</summary>
public sealed class SafeDeveloperCommandRunner
{
    private const int MaxOutputCharacters = 64 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    public async Task<DeveloperCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath) ||
            !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("版本验证只允许运行已存在的绝对 EXE 路径。");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动版本验证进程。");
        }

        var stdoutTask = ReadLimitedAsync(process.StandardOutput);
        var stderrTask = ReadLimitedAsync(process.StandardError);
        using var timeoutSource = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (!process.HasExited)
        {
            TryKill(process);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new DeveloperCommandResult(process.HasExited ? process.ExitCode : -1, stdout, stderr, timedOut);
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        var builder = new StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (builder.Length < MaxOutputCharacters)
            {
                var remaining = MaxOutputCharacters - builder.Length;
                builder.Append(buffer, 0, Math.Min(count, remaining));
            }
        }

        return builder.ToString().Trim();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }
        }
        catch
        {
            // 进程可能已经退出或受系统保护；调用方仍会收到超时或取消状态。
        }
    }
}
