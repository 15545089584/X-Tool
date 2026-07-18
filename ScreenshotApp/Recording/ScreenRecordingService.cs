using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotApp.Capture;
using Vortice.MediaFoundation;

namespace ScreenshotApp.Recording;

/// <summary>
/// 基于 Windows Media Foundation 的本地屏幕录像基础服务。
/// 视频直接由系统 H.264 编码器写入 MP4，不依赖外部转码程序。
/// </summary>
public sealed class ScreenRecordingService
{
    public const string StorageDirectory = @"E:\截影\Recordings";
    private readonly ICaptureBackend _captureBackend;

    public ScreenRecordingService(ICaptureBackend captureBackend)
    {
        _captureBackend = captureBackend;
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
        var stopwatch = Stopwatch.StartNew();
        var frameCount = 0;

        MediaFactory.MFStartup(false).CheckError();
        try
        {
            using var writer = CreateWriter(filePath, firstFrame.PixelWidth, firstFrame.PixelHeight, frameRate, out var videoStream);
            writer.BeginWriting();
            var frameDuration = 10_000_000L / frameRate;
            var nextFrameAt = stopwatch.Elapsed;
            while (!shouldStop())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bitmap = frameCount == 0 ? firstFrame : await CaptureRegionAsync(recordingRegion, cancellationToken);
                var timestamp = frameCount * frameDuration;
                WriteVideoSample(writer, videoStream, bitmap, timestamp, frameDuration);
                frameCount++;
                progressChanged?.Invoke(stopwatch.Elapsed);

                nextFrameAt += TimeSpan.FromSeconds(1d / frameRate);
                var delay = nextFrameAt - stopwatch.Elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }

            writer.Finalize();
        }
        finally
        {
            MediaFactory.MFShutdown().CheckError();
        }

        return new ScreenRecordingResult(
            filePath,
            stopwatch.Elapsed,
            frameCount,
            false,
            false);
    }

    private static IMFSinkWriter CreateWriter(string filePath, int width, int height, int frameRate, out int streamIndex)
    {
        var writer = MediaFactory.MFCreateSinkWriterFromURL(filePath, null, null);
        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outputType.SetUInt32(MediaTypeAttributeKeys.AvgBitrate, (uint)Math.Max(2_000_000, width * height * frameRate / 3));
        outputType.SetUInt32(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)frameRate, 1).CheckError();
        streamIndex = writer.AddStream(outputType);

        using var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        inputType.SetUInt32(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)frameRate, 1).CheckError();
        writer.SetInputMediaType(streamIndex, inputType, null);
        return writer;
    }

    private static void WriteVideoSample(IMFSinkWriter writer, int streamIndex, BitmapSource bitmap, long timestamp, long duration)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        using var buffer = MediaFactory.MFCreateMemoryBuffer(pixels.Length);
        buffer.Lock(out var pointer, out _, out _);
        try
        {
            Marshal.Copy(pixels, 0, pointer, pixels.Length);
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
}
