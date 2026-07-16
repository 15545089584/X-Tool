using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ScreenshotApp.Capture;

/// <summary>
/// 使用 DXGI Desktop Duplication 抓取鼠标所在显示器。
/// 每次捕获独立创建资源，优先保证阶段一的稳定性；长截图阶段再复用设备和帧池。
/// </summary>
public sealed class DxgiDesktopCaptureBackend : ICaptureBackend
{
    private static readonly FeatureLevel[] FeatureLevels =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    };

    public string Name => "DXGI Desktop Duplication";

    public Task<CaptureFrame> CaptureCurrentMonitorAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CaptureCurrentMonitor(cancellationToken), cancellationToken);
    }

    private CaptureFrame CaptureCurrentMonitor(CancellationToken cancellationToken)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            throw new InvalidOperationException("无法获取鼠标所在位置。");
        }

        using var factory = CreateDXGIFactory1<IDXGIFactory1>();

        for (var adapterIndex = 0; ; adapterIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
            if (adapterResult.Failure)
            {
                break;
            }

            using (adapter)
            {
                for (var outputIndex = 0; ; outputIndex++)
                {
                    var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                    if (outputResult.Failure)
                    {
                        break;
                    }

                    using (output)
                    {
                        var outputDescription = output.Description;
                        var bounds = outputDescription.DesktopCoordinates;
                        if (!outputDescription.AttachedToDesktop ||
                            cursor.X < bounds.Left || cursor.X >= bounds.Right ||
                            cursor.Y < bounds.Top || cursor.Y >= bounds.Bottom)
                        {
                            continue;
                        }

                        return CaptureOutput(adapter, output, outputDescription, cancellationToken);
                    }
                }
            }
        }

        throw new InvalidOperationException("没有找到鼠标所在的可捕获显示器。");
    }

    private CaptureFrame CaptureOutput(
        IDXGIAdapter1 adapter,
        IDXGIOutput output,
        OutputDescription outputDescription,
        CancellationToken cancellationToken)
    {
        var createResult = D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            FeatureLevels,
            out var device,
            out var context);
        createResult.CheckError();

        using (device)
        using (context)
        using (var output1 = output.QueryInterface<IDXGIOutput1>())
        using (var duplication = output1.DuplicateOutput(device))
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                IDXGIResource? desktopResource = null;
                var frameAcquired = false;

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var timeout = attempt == 0 ? 500 : 160;
                    var acquireResult = duplication.AcquireNextFrame(timeout, out _, out desktopResource);
                    if (acquireResult.Failure)
                    {
                        if (attempt == 2)
                        {
                            acquireResult.CheckError();
                        }

                        Thread.Sleep(35);
                        continue;
                    }

                    frameAcquired = true;
                    using var sourceTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                    var sourceDescription = sourceTexture.Description;
                    var stagingDescription = new Texture2DDescription(
                        sourceDescription.Format,
                        sourceDescription.Width,
                        sourceDescription.Height,
                        1,
                        1,
                        BindFlags.None,
                        ResourceUsage.Staging,
                        CpuAccessFlags.Read);

                    using var stagingTexture = device.CreateTexture2D(stagingDescription);
                    context.CopyResource(stagingTexture, sourceTexture);

                    var mapped = context.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    BitmapSource bitmap;
                    try
                    {
                        bitmap = CopyToBitmap(mapped, sourceDescription.Width, sourceDescription.Height);
                    }
                    finally
                    {
                        context.Unmap(stagingTexture, 0);
                    }

                    bitmap = ApplyDisplayRotation(bitmap, outputDescription.Rotation);
                    if (CaptureFrameAnalyzer.IsLikelyBlank(bitmap))
                    {
                        Thread.Sleep(35);
                        continue;
                    }

                    var desktopBounds = outputDescription.DesktopCoordinates;
                    return new CaptureFrame(
                        bitmap,
                        new Int32Rect(
                            desktopBounds.Left,
                            desktopBounds.Top,
                            desktopBounds.Right - desktopBounds.Left,
                            desktopBounds.Bottom - desktopBounds.Top),
                        DateTimeOffset.Now,
                        Name);
                }
                finally
                {
                    desktopResource?.Dispose();
                    if (frameAcquired)
                    {
                        duplication.ReleaseFrame();
                    }
                }
            }

            throw new BlankCaptureFrameException();
        }
    }

    private static BitmapSource CopyToBitmap(MappedSubresource mapped, int width, int height)
    {
        const int bytesPerPixel = 4;
        var stride = checked(width * bytesPerPixel);
        var pixels = new byte[checked(stride * height)];

        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(IntPtr.Add(mapped.DataPointer, row * mapped.RowPitch), pixels, row * stride, stride);
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource ApplyDisplayRotation(BitmapSource source, ModeRotation rotation)
    {
        var angle = rotation switch
        {
            ModeRotation.Rotate90 => 90,
            ModeRotation.Rotate180 => 180,
            ModeRotation.Rotate270 => 270,
            _ => 0
        };

        if (angle == 0)
        {
            return source;
        }

        var rotated = new TransformedBitmap(source, new RotateTransform(angle));
        rotated.Freeze();
        return rotated;
    }
}
