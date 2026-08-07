using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ScreenshotApp.Capture;
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
        CancellationToken cancellationToken = default)
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
                audioSession?.IncludesMicrophone == true);
        }
        finally
        {
            MediaFactory.MFShutdown().CheckError();
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
