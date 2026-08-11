using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ScreenshotApp.Capture;
using ScreenshotApp.Converters;
using ScreenshotApp.Settings;
using Vortice.MediaFoundation;

namespace ScreenshotApp.Recording;

/// <summary>
/// 基于 Windows Media Foundation 的本地屏幕录像服务。
/// 视频由系统 H.264 编码器写入 MP4，声音通过 WASAPI 本地采集并混入 AAC 音轨。
/// </summary>
public sealed class ScreenRecordingService
{
    private readonly ICaptureBackend _captureBackend;
    private readonly AppPreferences _preferences;
    private readonly string? _ffmpegPath = MediaConversionService.FindExecutablePath("ffmpeg.exe");

    private const int GifDefaultFps = 12;
    private const int GifMaximumFps = 15;
    private const int GifDefaultMaxWidth = 960;
    private const int GifDefaultMaxDurationSeconds = 10;
    private const int GifAbsoluteMaxDurationSeconds = 15;

    public string StorageDirectory => _preferences.RecordingDirectory;

    public string CoverDirectory => Path.Combine(StorageDirectory, "Covers");

    public ScreenRecordingService(ICaptureBackend captureBackend, AppPreferences preferences)
    {
        _captureBackend = captureBackend;
        _preferences = preferences;
    }

    public async Task<ScreenRecordingResult> RecordAsync(
        ScreenRecordingOptions options,
        Func<bool> shouldStop,
        Action<TimeSpan>? progressChanged,
        CancellationToken cancellationToken = default,
        Action<string>? statusChanged = null)
    {
        return options.Mode == ScreenRecordingMode.Gif
            ? await RecordGifAsync(options, shouldStop, progressChanged, cancellationToken, statusChanged)
            : await RecordMp4Async(options, shouldStop, progressChanged, cancellationToken);
    }

    private async Task<ScreenRecordingResult> RecordMp4Async(
        ScreenRecordingOptions options,
        Func<bool> shouldStop,
        Action<TimeSpan>? progressChanged,
        CancellationToken cancellationToken)
    {
        if (options.ScreenRegion.Width < 64 || options.ScreenRegion.Height < 64)
        {
            throw new InvalidOperationException("录像区域过小，请重新框选。 ");
        }

        var frameRate = Math.Clamp(options.FramesPerSecond, 5, 30);
        var recordingRegion = MakeEven(options.ScreenRegion);
        Directory.CreateDirectory(StorageDirectory);
        var filePath = Path.Combine(StorageDirectory, $"截影_录像_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.mp4");
        var firstFrame = await CaptureRegionAsync(recordingRegion, cancellationToken);
        var coverImagePath = await TrySaveCoverAsync(firstFrame, filePath);
        var stopwatch = Stopwatch.StartNew();
        var frameCount = 0;

        MediaFactory.MFStartup(false).CheckError();
        try
        {
            using var audioSession = RecordingAudioSession.Create(options.RecordSystemAudio, options.RecordMicrophone);
            using var writer = CreateWriter(
                filePath,
                firstFrame.PixelWidth,
                firstFrame.PixelHeight,
                frameRate,
                audioSession is not null,
                out var videoStream,
                out var audioStream);
            writer.BeginWriting();
            var writerGate = new object();
            var audioCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var audioTask = audioSession?.WriteAsync(writer, audioStream, writerGate, audioCancellation.Token);
            var frameDuration = 10_000_000L / frameRate;
            var nextFrameAt = stopwatch.Elapsed;
            try
            {
                while (!shouldStop())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bitmap = frameCount == 0 ? firstFrame : await CaptureRegionAsync(recordingRegion, cancellationToken);
                    var timestamp = frameCount * frameDuration;
                    lock (writerGate)
                    {
                        WriteVideoSample(writer, videoStream, bitmap, timestamp, frameDuration);
                    }

                    frameCount++;
                    progressChanged?.Invoke(stopwatch.Elapsed);

                    nextFrameAt += TimeSpan.FromSeconds(1d / frameRate);
                    var delay = nextFrameAt - stopwatch.Elapsed;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                }
            }
            finally
            {
                audioCancellation.Cancel();
                if (audioTask is not null)
                {
                    try
                    {
                        await audioTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // 结束录像时取消音频写入是正常流程。
                    }
                }

                audioCancellation.Dispose();
            }

            // SinkWriter 收尾会刷新文件头并同步写入，放在后台线程避免停止录像时卡住界面。
            await Task.Run(() =>
            {
                lock (writerGate)
                {
                    writer.Finalize();
                }
            });

            return new ScreenRecordingResult(
                filePath,
                coverImagePath,
                stopwatch.Elapsed,
                frameCount,
                audioSession?.IncludesSystemAudio == true,
                audioSession?.IncludesMicrophone == true,
                ScreenRecordingMode.Mp4);
        }
        finally
        {
            MediaFactory.MFShutdown().CheckError();
        }

    }

    private async Task<ScreenRecordingResult> RecordGifAsync(
        ScreenRecordingOptions options,
        Func<bool> shouldStop,
        Action<TimeSpan>? progressChanged,
        CancellationToken cancellationToken,
        Action<string>? statusChanged)
    {
        if (_ffmpegPath is null)
        {
            throw new InvalidOperationException("GIF 录制需要随附的 FFmpeg 引擎，请修复或重新安装 X-Tool。");
        }

        if (options.ScreenRegion.Width < 64 || options.ScreenRegion.Height < 64)
        {
            throw new InvalidOperationException("录像区域过小，请重新框选。 ");
        }

        var frameRate = Math.Clamp(options.FramesPerSecond <= 0 ? GifDefaultFps : options.FramesPerSecond, 5, GifMaximumFps);
        var maxWidth = options.GifMaxWidth <= 0 ? GifDefaultMaxWidth : Math.Clamp(options.GifMaxWidth, 320, 1_920);
        var maxDurationSeconds = options.GifMaxDurationSeconds <= 0
            ? GifDefaultMaxDurationSeconds
            : Math.Clamp(options.GifMaxDurationSeconds, 1, GifAbsoluteMaxDurationSeconds);
        var recordingRegion = MakeEven(options.ScreenRegion);
        Directory.CreateDirectory(StorageDirectory);
        var filePath = Path.Combine(StorageDirectory, $"截影_GIF_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.gif");
        var firstFrame = EnsureBgra32(await CaptureRegionAsync(recordingRegion, cancellationToken));
        var coverImagePath = await TrySaveCoverAsync(firstFrame, filePath);
        var stopwatch = Stopwatch.StartNew();
        var frameCount = 0;
        Process? encoder = null;
        var completed = false;

        try
        {
            encoder = StartGifEncoder(
                _ffmpegPath,
                filePath,
                firstFrame.PixelWidth,
                firstFrame.PixelHeight,
                frameRate,
                maxWidth);
            var errorTask = encoder.StandardError.ReadToEndAsync();
            var pixels = new byte[firstFrame.PixelWidth * firstFrame.PixelHeight * 4];
            await WriteGifFrameAsync(encoder, firstFrame, pixels, cancellationToken);
            frameCount = 1;
            progressChanged?.Invoke(TimeSpan.Zero);

            var nextFrameAt = TimeSpan.FromSeconds(1d / frameRate);
            var maxDuration = TimeSpan.FromSeconds(maxDurationSeconds);
            while (!shouldStop() && stopwatch.Elapsed < maxDuration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var delay = nextFrameAt - stopwatch.Elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                if (shouldStop() || stopwatch.Elapsed >= maxDuration)
                {
                    break;
                }

                var bitmap = EnsureBgra32(await CaptureRegionAsync(recordingRegion, cancellationToken));
                await WriteGifFrameAsync(encoder, bitmap, pixels, cancellationToken);
                frameCount++;
                progressChanged?.Invoke(stopwatch.Elapsed);
                nextFrameAt += TimeSpan.FromSeconds(1d / frameRate);
            }

            if (stopwatch.Elapsed >= maxDuration)
            {
                // 最后一帧可能刚好落在 9 秒刻度，收尾前把控制条补到真实上限，
                // 避免用户误以为 GIF 只能录制 9 秒。
                progressChanged?.Invoke(maxDuration);
            }

            statusChanged?.Invoke("正在合成 GIF…");
            // 直接关闭二进制管道，避免 StreamWriter 在关闭时写入 UTF-8 BOM，
            // 否则 GIF 的 rawvideo 输入末尾会多出 3 个字节并触发残帧警告。
            encoder.StandardInput.BaseStream.Close();
            using var finalizeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await encoder.WaitForExitAsync(finalizeCancellation.Token);
            var error = await errorTask;
            if (encoder.ExitCode != 0)
            {
                throw new InvalidOperationException($"GIF 编码失败：{SummarizeProcessError(error)}");
            }

            completed = true;
            return new ScreenRecordingResult(
                filePath,
                coverImagePath,
                stopwatch.Elapsed,
                frameCount,
                false,
                false,
                ScreenRecordingMode.Gif);
        }
        catch (OperationCanceledException)
        {
            TryTerminateProcess(encoder);
            throw;
        }
        catch
        {
            TryTerminateProcess(encoder);
            throw;
        }
        finally
        {
            if (!completed)
            {
                TryDeleteFile(filePath);
                TryDeleteFile(coverImagePath);
            }

            encoder?.Dispose();
        }
    }

    private static Process StartGifEncoder(string? ffmpegPath, string filePath, int width, int height, int frameRate, int maxWidth)
    {
        var scaledWidth = Math.Max(2, Math.Min(width, maxWidth) & ~1);
        // 逐帧生成调色板并立即消费，避免 stats_mode=diff 等待整段输入后再合成，
        // 让停止 GIF 时的 FFmpeg 收尾保持在可接受范围内。
        var filter = $"scale={scaledWidth}:-2:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=256:stats_mode=single[p];[s1][p]paletteuse=new=1:dither=sierra2_4a";
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath ?? throw new InvalidOperationException("找不到 FFmpeg 引擎。"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s:v", $"{width}x{height}",
            "-r", frameRate.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0",
            "-filter_complex", filter, "-loop", "-1", "-f", "gif", filePath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 GIF 编码进程。");
    }

    private static async Task WriteGifFrameAsync(Process encoder, BitmapSource bitmap, byte[] pixels, CancellationToken cancellationToken)
    {
        var stride = bitmap.PixelWidth * 4;
        bitmap.CopyPixels(pixels, stride, 0);
        await encoder.StandardInput.BaseStream.WriteAsync(pixels.AsMemory(0, stride * bitmap.PixelHeight), cancellationToken);
    }

    private static BitmapSource EnsureBgra32(BitmapSource bitmap)
    {
        if (bitmap.Format == PixelFormats.Bgra32)
        {
            return bitmap;
        }

        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static string SummarizeProcessError(string error)
    {
        var message = error.Trim();
        if (message.Length > 600)
        {
            message = message[^600..];
        }

        return string.IsNullOrWhiteSpace(message) ? "FFmpeg 未返回错误详情" : message;
    }

    private static void TryTerminateProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
        }
        catch
        {
            // 进程已退出或清理失败时不覆盖原始录像错误。
        }
    }

    private static void TryDeleteFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // 临时文件清理失败不应覆盖原始录像错误。
        }
    }

    private static IMFSinkWriter CreateWriter(
        string filePath,
        int width,
        int height,
        int frameRate,
        bool includeAudio,
        out int videoStream,
        out int audioStream)
    {
        var writer = MediaFactory.MFCreateSinkWriterFromURL(filePath, null, null);
        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outputType.SetUInt32(MediaTypeAttributeKeys.AvgBitrate, (uint)Math.Max(2_000_000, width * height * frameRate / 3));
        outputType.SetUInt32(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)frameRate, 1).CheckError();
        videoStream = writer.AddStream(outputType);

        using var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        inputType.SetUInt32(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)frameRate, 1).CheckError();
        writer.SetInputMediaType(videoStream, inputType, null);

        audioStream = -1;
        if (includeAudio)
        {
            audioStream = ConfigureAudioStream(writer);
        }

        return writer;
    }

    private static int ConfigureAudioStream(IMFSinkWriter writer)
    {
        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        outputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        outputType.SetUInt32(MediaTypeAttributeKeys.AudioNumChannels, 2);
        outputType.SetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48_000);
        outputType.SetUInt32(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24_000);
        outputType.SetUInt32(MediaTypeAttributeKeys.AacPayloadType, 0);
        var stream = writer.AddStream(outputType);

        using var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        inputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Float);
        inputType.SetUInt32(MediaTypeAttributeKeys.AudioNumChannels, 2);
        inputType.SetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48_000);
        inputType.SetUInt32(MediaTypeAttributeKeys.AudioBitsPerSample, 32);
        inputType.SetUInt32(MediaTypeAttributeKeys.AudioBlockAlignment, 8);
        inputType.SetUInt32(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 384_000);
        writer.SetInputMediaType(stream, inputType, null);
        return stream;
    }

    private static void WriteVideoSample(IMFSinkWriter writer, int streamIndex, BitmapSource bitmap, long timestamp, long duration)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        var stride = bitmap.PixelWidth * 4;
        bitmap.CopyPixels(pixels, stride, 0);
        using var buffer = MediaFactory.MFCreateMemoryBuffer(pixels.Length);
        buffer.Lock(out var pointer, out _, out _);
        try
        {
            // WPF CopyPixels 是自顶向下，Media Foundation 的 RGB32 输入按 DIB 约定读取为自底向上。
            // 不翻转行序会使最终 H.264 画面倒置。
            for (var row = 0; row < bitmap.PixelHeight; row++)
            {
                var sourceOffset = (bitmap.PixelHeight - 1 - row) * stride;
                Marshal.Copy(pixels, sourceOffset, IntPtr.Add(pointer, row * stride), stride);
            }
            buffer.CurrentLength = pixels.Length;
        }
        finally
        {
            buffer.Unlock();
        }

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp;
        sample.SampleDuration = duration;
        writer.WriteSample(streamIndex, sample);
    }

    private static void WriteAudioSample(IMFSinkWriter writer, int streamIndex, byte[] samples, long timestamp, long duration)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(samples.Length);
        buffer.Lock(out var pointer, out _, out _);
        try
        {
            Marshal.Copy(samples, 0, pointer, samples.Length);
            buffer.CurrentLength = samples.Length;
        }
        finally
        {
            buffer.Unlock();
        }

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp;
        sample.SampleDuration = duration;
        writer.WriteSample(streamIndex, sample);
    }

    private Task<string?> TrySaveCoverAsync(BitmapSource frame, string videoPath)
    {
        if (frame.CanFreeze && !frame.IsFrozen)
        {
            frame.Freeze();
        }

        return Task.Run<string?>(() =>
        {
            try
            {
                Directory.CreateDirectory(CoverDirectory);
                var coverPath = Path.Combine(CoverDirectory, $"{Path.GetFileNameWithoutExtension(videoPath)}.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frame));
                using var stream = new FileStream(coverPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                encoder.Save(stream);
                return coverPath;
            }
            catch
            {
                // 封面写入失败不应影响录像文件本身。
                return null;
            }
        });
    }

    private async Task<BitmapSource> CaptureRegionAsync(Int32Rect screenRegion, CancellationToken cancellationToken)
    {
        _ = NativeMethods.DwmFlush();
        var frame = await _captureBackend.CaptureCurrentMonitorAsync(cancellationToken);
        var x = screenRegion.X - frame.ScreenBounds.X;
        var y = screenRegion.Y - frame.ScreenBounds.Y;
        if (x < 0 || y < 0 || x + screenRegion.Width > frame.Bitmap.PixelWidth || y + screenRegion.Height > frame.Bitmap.PixelHeight)
        {
            throw new InvalidOperationException("录像区域超出了当前显示器范围。 ");
        }

        var cropped = new CroppedBitmap(frame.Bitmap, new Int32Rect(x, y, screenRegion.Width, screenRegion.Height));
        cropped.Freeze();
        return cropped;
    }

    private static Int32Rect MakeEven(Int32Rect region) => new(
        region.X,
        region.Y,
        Math.Max(2, region.Width & ~1),
        Math.Max(2, region.Height & ~1));

    /// <summary>
    /// 将系统回环和麦克风统一转换为 48 kHz 双声道浮点 PCM，再由 Media Foundation 编码为 AAC。
    /// </summary>
    private sealed class RecordingAudioSession : IDisposable
    {
        private const int SampleRate = 48_000;
        private const int Channels = 2;
        private const int SamplesPerChunk = 960;
        private const long ChunkDuration = 200_000;

        private readonly MMDeviceEnumerator _deviceEnumerator = new();
        private readonly List<WasapiCapture> _captures = new();
        private readonly List<BufferedWaveProvider> _buffers = new();
        private readonly ISampleProvider _mixedProvider;
        private bool _disposed;

        private RecordingAudioSession(bool recordSystemAudio, bool recordMicrophone)
        {
            IncludesSystemAudio = recordSystemAudio;
            IncludesMicrophone = recordMicrophone;
            if (recordSystemAudio)
            {
                var renderDevice = _deviceEnumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                AddCapture(new WasapiLoopbackCapture(renderDevice));
            }

            if (recordMicrophone)
            {
                var captureDevice = _deviceEnumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Multimedia);
                AddCapture(new WasapiCapture(captureDevice));
            }

            var providers = _buffers.Select(buffer => ConvertToStereo48k(buffer.ToSampleProvider())).ToArray();
            // 即使只有一个音源也走混音器，确保音频缓冲暂时不足时补静音，
            // 不会把上一帧 PCM 尾部重复写入。
            _mixedProvider = new MixingSampleProvider(providers) { ReadFully = true };
        }

        public bool IncludesSystemAudio { get; }

        public bool IncludesMicrophone { get; }

        public static RecordingAudioSession? Create(bool recordSystemAudio, bool recordMicrophone)
        {
            if (!recordSystemAudio && !recordMicrophone)
            {
                return null;
            }

            try
            {
                return new RecordingAudioSession(recordSystemAudio, recordMicrophone);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("无法初始化选择的录音设备。请检查电脑声音或麦克风是否可用。", exception);
            }
        }

        public async Task WriteAsync(IMFSinkWriter writer, int streamIndex, object writerGate, CancellationToken cancellationToken)
        {
            foreach (var capture in _captures)
            {
                capture.StartRecording();
            }

            var samples = new float[SamplesPerChunk * Channels];
            var bytes = new byte[samples.Length * sizeof(float)];
            var timestamp = 0L;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    _ = _mixedProvider.Read(samples, 0, samples.Length);
                    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                    lock (writerGate)
                    {
                        WriteAudioSample(writer, streamIndex, bytes, timestamp, ChunkDuration);
                    }

                    timestamp += ChunkDuration;
                    await Task.Delay(20, cancellationToken);
                }
            }
            finally
            {
                foreach (var capture in _captures)
                {
                    capture.StopRecording();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var capture in _captures)
            {
                capture.Dispose();
            }

            _deviceEnumerator.Dispose();
        }

        private void AddCapture(WasapiCapture capture)
        {
            var buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2)
            };
            capture.DataAvailable += (_, args) => buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
            _captures.Add(capture);
            _buffers.Add(buffer);
        }

        private static ISampleProvider ConvertToStereo48k(ISampleProvider provider)
        {
            if (provider.WaveFormat.Channels == 1)
            {
                provider = new MonoToStereoSampleProvider(provider);
            }
            else if (provider.WaveFormat.Channels != Channels)
            {
                throw new NotSupportedException("当前音频设备的声道数不受支持。 ");
            }

            return provider.WaveFormat.SampleRate == SampleRate
                ? provider
                : new WdlResamplingSampleProvider(provider, SampleRate);
        }
    }
}
