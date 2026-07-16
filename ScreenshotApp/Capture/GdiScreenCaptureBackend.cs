using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// DXGI 不可用或返回空帧时使用的兼容截图后端。
/// </summary>
public sealed class GdiScreenCaptureBackend : ICaptureBackend
{
    public string Name => "GDI BitBlt fallback";

    public Task<CaptureFrame> CaptureCurrentMonitorAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CaptureCurrentMonitor(cancellationToken), cancellationToken);
    }

    private CaptureFrame CaptureCurrentMonitor(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            throw new InvalidOperationException("无法获取鼠标所在位置。");
        }

        var monitorHandle = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };

        if (monitorHandle == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            throw new InvalidOperationException("无法获取显示器信息。");
        }

        var bounds = monitorInfo.Monitor;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("显示器尺寸无效。");
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法获取桌面设备上下文。");
        }

        var memoryDc = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var previousObject = IntPtr.Zero;

        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法创建截图缓冲区。");
            }

            var bitmapInfo = new NativeMethods.BitmapInfo
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = NativeMethods.BiRgb,
                    SizeImage = (uint)checked(width * height * 4)
                }
            };

            dib = NativeMethods.CreateDIBSection(
                screenDc,
                ref bitmapInfo,
                NativeMethods.DibRgbColors,
                out var bits,
                IntPtr.Zero,
                0);

            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法创建截图位图。");
            }

            previousObject = NativeMethods.SelectObject(memoryDc, dib);
            var copied = NativeMethods.BitBlt(
                memoryDc,
                0,
                0,
                width,
                height,
                screenDc,
                bounds.Left,
                bounds.Top,
                NativeMethods.Srccopy | NativeMethods.CaptureBlt);

            if (!copied)
            {
                throw new InvalidOperationException("GDI 桌面复制失败。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            var bitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgr32,
                null,
                pixels,
                stride);
            bitmap.Freeze();

            return new CaptureFrame(
                bitmap,
                new Int32Rect(bounds.Left, bounds.Top, width, height),
                DateTimeOffset.Now,
                Name);
        }
        finally
        {
            if (previousObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previousObject);
            }

            if (dib != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(dib);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
