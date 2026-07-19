using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ScreenshotApp.Converters;

/// <summary>音视频文件的探测结果。</summary>
internal sealed record MediaProbeInfo(
    TimeSpan Duration,
    string Format,
    long FileSize,
    string Codec,
    int SampleRate,
    int Channels,
    int Width,
    int Height,
    double FramesPerSecond);

/// <summary>FFmpeg 任务的实时状态。</summary>
internal sealed record MediaConversionProgress(double Percent, string Message);

internal enum AudioQualityPreset
{
    High,
    Standard,
    Small,
    Voice
}

internal sealed record AudioConversionOptions(
    string OutputFormat,
    AudioQualityPreset Preset,
    int? BitRateKbps,
    int? SampleRate,
    int? Channels,
    bool PreserveMetadata);

internal enum VideoOperation
{
    Convert,
    Compress,
    ExtractAudio,
    Gif
}

internal sealed record VideoConversionOptions(
    VideoOperation Operation,
    string OutputFormat,
    string Quality,
    int? Width,
    int? VideoBitRateKbps,
    string Codec,
    TimeSpan? StartTime,
    TimeSpan? EndTime,
    int GifFps);

/// <summary>
/// FFmpeg/ffprobe 的统一调用入口。界面层只提交任务参数，不直接拼接命令行，
/// 以便后续替换为随包组件、GPU 编码器或队列调度器。
/// </summary>
internal sealed class MediaConversionService
{
    private readonly string? _ffmpegPath;
    private readonly string? _ffprobePath;

    public MediaConversionService()
    {
        _ffmpegPath = FindExecutable("ffmpeg.exe");
        _ffprobePath = FindExecutable("ffprobe.exe");
    }

    public bool IsAvailable => _ffmpegPath is not null && _ffprobePath is not null;

    public string AvailabilityMessage => IsAvailable
        ? "FFmpeg 转换引擎已就绪"
        : "未检测到 FFmpeg，请将 ffmpeg.exe 与 ffprobe.exe 放入 tools\\ffmpeg";

    public async Task<MediaProbeInfo> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var output = await RunForOutputAsync(
            _ffprobePath!,
            new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", filePath },
            cancellationToken);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var formatElement = root.TryGetProperty("format", out var format) ? format : default;
        var streams = root.TryGetProperty("streams", out var streamArray)
            ? streamArray.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        var audio = streams.FirstOrDefault(stream => ReadString(stream, "codec_type") == "audio");
        var video = streams.FirstOrDefault(stream => ReadString(stream, "codec_type") == "video");
        var durationSeconds = ReadDouble(formatElement, "duration");
        if (durationSeconds <= 0)
        {
            durationSeconds = Math.Max(ReadDouble(audio, "duration"), ReadDouble(video, "duration"));
        }

        var size = (long)ReadDouble(formatElement, "size");
        if (size <= 0 && File.Exists(filePath))
        {
            size = new FileInfo(filePath).Length;
        }

        var codec = ReadString(video, "codec_name");
        if (string.IsNullOrWhiteSpace(codec))
        {
            codec = ReadString(audio, "codec_name");
        }

        return new MediaProbeInfo(
            TimeSpan.FromSeconds(Math.Max(0, durationSeconds)),
            ReadString(formatElement, "format_name").ToUpperInvariant(),
            size,
            codec.ToUpperInvariant(),
            ReadInt(audio, "sample_rate"),
            ReadInt(audio, "channels"),
            ReadInt(video, "width"),
            ReadInt(video, "height"),
            ParseFrameRate(ReadString(video, "avg_frame_rate")));
    }

    public Task ConvertAudioAsync(
        string inputPath,
        string outputPath,
        MediaProbeInfo probe,
        AudioConversionOptions options,
        IProgress<MediaConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-y", "-i", inputPath, "-vn" };
        var bitRate = options.BitRateKbps ?? options.Preset switch
        {
            AudioQualityPreset.High => 320,
            AudioQualityPreset.Small => 128,
            AudioQualityPreset.Voice => 64,
            _ => 192
        };

        switch (options.OutputFormat.ToLowerInvariant())
        {
            case "wav":
                arguments.AddRange(new[] { "-c:a", "pcm_s16le" });
                break;
            case "flac":
                arguments.AddRange(new[] { "-c:a", "flac" });
                break;
            case "ogg":
                arguments.AddRange(new[] { "-c:a", "libvorbis", "-b:a", $"{bitRate}k" });
                break;
            case "opus":
                arguments.AddRange(new[] { "-c:a", "libopus", "-b:a", $"{bitRate}k" });
                break;
            case "m4a":
                arguments.AddRange(new[] { "-c:a", "aac", "-b:a", $"{bitRate}k" });
                break;
            default:
                arguments.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", $"{bitRate}k" });
                break;
        }

        if (options.SampleRate is > 0)
        {
            arguments.AddRange(new[] { "-ar", options.SampleRate.Value.ToString(CultureInfo.InvariantCulture) });
        }

        if (options.Channels is > 0)
        {
            arguments.AddRange(new[] { "-ac", options.Channels.Value.ToString(CultureInfo.InvariantCulture) });
        }

        if (!options.PreserveMetadata)
        {
            arguments.AddRange(new[] { "-map_metadata", "-1" });
        }

        arguments.Add(outputPath);
        return RunConversionAsync(arguments, probe.Duration, progress, cancellationToken);
    }

    public Task ConvertVideoAsync(
        string inputPath,
        string outputPath,
        MediaProbeInfo probe,
        VideoConversionOptions options,
        IProgress<MediaConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-y" };
        if (options.StartTime is { } start && start > TimeSpan.Zero)
        {
            arguments.AddRange(new[] { "-ss", start.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture) });
        }

        arguments.AddRange(new[] { "-i", inputPath });
        if (options.EndTime is { } end && end > TimeSpan.Zero)
        {
            var duration = end - (options.StartTime ?? TimeSpan.Zero);
            if (duration > TimeSpan.Zero)
            {
                arguments.AddRange(new[] { "-t", duration.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture) });
            }
        }

        if (options.Operation == VideoOperation.ExtractAudio)
        {
            AddExtractAudioArguments(arguments, options.OutputFormat);
        }
        else if (options.Operation == VideoOperation.Gif)
        {
            var width = options.Width is > 0 ? options.Width.Value : 720;
            arguments.AddRange(new[]
            {
                "-vf", $"fps={Math.Clamp(options.GifFps, 5, 30)},scale={width}:-1:flags=lanczos",
                "-loop", "0"
            });
        }
        else
        {
            AddVideoCodecArguments(arguments, options);
            if (options.Width is > 0)
            {
                arguments.AddRange(new[] { "-vf", $"scale={options.Width.Value}:-2" });
            }
        }

        arguments.Add(outputPath);
        return RunConversionAsync(arguments, probe.Duration, progress, cancellationToken);
    }

    private async Task RunConversionAsync(
        IReadOnlyList<string> arguments,
        TimeSpan duration,
        IProgress<MediaConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureAvailable();
        if (arguments.Count == 0)
        {
            throw new InvalidOperationException("FFmpeg 转换参数不完整");
        }

        // 进度参数必须位于输出文件之前，否则 FFmpeg 会把它们解释为下一个输出任务的参数。
        var commandArguments = arguments.Take(arguments.Count - 1)
            .Concat(new[] { "-progress", "pipe:1", "-nostats", arguments[^1] })
            .ToArray();
        AppendLog($"开始任务：{string.Join(' ', commandArguments.Select(QuoteForLog))}");
        var startInfo = CreateStartInfo(_ffmpegPath!, commandArguments);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                errors.AppendLine(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 FFmpeg 进程");
        }

        process.BeginErrorReadLine();
        try
        {
            while (!process.StandardOutput.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await process.StandardOutput.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("out_time_ms=", StringComparison.Ordinal) &&
                    long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                {
                    var percent = duration.TotalMilliseconds <= 0
                        ? 0
                        : Math.Clamp(microseconds / 1000d / duration.TotalMilliseconds * 100d, 0, 99.5);
                    progress?.Report(new MediaConversionProgress(percent, $"正在转换 {percent:0}%"));
                }
            }

            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            AppendLog("任务已由用户取消");
            throw;
        }

        if (process.ExitCode != 0)
        {
            var message = SummarizeError(errors.ToString());
            AppendLog($"任务失败：{message}");
            throw new InvalidOperationException(message);
        }

        AppendLog("任务完成");
        progress?.Report(new MediaConversionProgress(100, "转换完成"));
    }

    private static void AddExtractAudioArguments(List<string> arguments, string outputFormat)
    {
        arguments.Add("-vn");
        switch (outputFormat.ToLowerInvariant())
        {
            case "wav":
                arguments.AddRange(new[] { "-c:a", "pcm_s16le" });
                break;
            case "m4a":
                arguments.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
                break;
            default:
                arguments.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", "192k" });
                break;
        }
    }

    private static void AddVideoCodecArguments(List<string> arguments, VideoConversionOptions options)
    {
        var format = options.OutputFormat.ToLowerInvariant();
        var codec = options.Codec.ToLowerInvariant();
        var quality = options.Quality switch
        {
            "高质量" => "18",
            "小文件" => "30",
            _ => "23"
        };

        if (format == "webm")
        {
            arguments.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", quality, "-b:v", "0", "-c:a", "libopus" });
            return;
        }

        var videoCodec = codec switch
        {
            "h.265" or "hevc" => "libx265",
            "av1" => "libaom-av1",
            _ => "libx264"
        };
        arguments.AddRange(new[] { "-c:v", videoCodec, "-crf", quality, "-preset", "medium", "-c:a", "aac", "-b:a", "192k" });
        if (options.VideoBitRateKbps is > 0)
        {
            arguments.AddRange(new[] { "-maxrate", $"{options.VideoBitRateKbps.Value}k", "-bufsize", $"{options.VideoBitRateKbps.Value * 2}k" });
        }
    }

    private async Task<string> RunForOutputAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(executable, arguments);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动媒体探测进程");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(SummarizeError(error));
        }

        return output;
    }

    private static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    private static string? FindExecutable(string fileName)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "tools", "ffmpeg", fileName),
            Path.Combine(baseDirectory, fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "ffmpeg", fileName)
        };
        var direct = candidates.FirstOrDefault(File.Exists);
        if (direct is not null)
        {
            return direct;
        }

        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(path.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // PATH 中的无效条目不应影响其他候选项。
            }
        }

        return null;
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException(AvailabilityMessage);
        }
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value)
            ? value.ToString()
            : string.Empty;

    private static double ReadDouble(JsonElement element, string propertyName) =>
        double.TryParse(ReadString(element, propertyName), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static int ReadInt(JsonElement element, string propertyName) =>
        int.TryParse(ReadString(element, propertyName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static double ParseFrameRate(string value)
    {
        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator > 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static string SummarizeError(string error)
    {
        var lines = error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0
            ? "FFmpeg 未返回可识别的错误信息"
            : string.Join("；", lines.TakeLast(Math.Min(4, lines.Length)).Select(line => line.Trim()));
    }

    private static void AppendLog(string message)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "X-Tool",
                "Logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "media-conversion.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // 日志写入失败不能中断转换主流程。
        }
    }

    private static string QuoteForLog(string argument) =>
        argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 取消阶段进程可能已经自行退出。
        }
    }
}
