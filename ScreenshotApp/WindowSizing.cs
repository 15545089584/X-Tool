using System.Runtime.InteropServices;

namespace ScreenshotApp;

/// <summary>
/// 修正无边框窗口的最大化范围，确保窗口只占用当前显示器的工作区。
/// </summary>
internal static class WindowSizing
{
    internal const int WmGetMinMaxInfo = 0x0024;
    internal const int WmNcHitTest = 0x0084;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int HitLeft = 10;
    private const int HitRight = 11;
    private const int HitTop = 12;
    private const int HitTopLeft = 13;
    private const int HitTopRight = 14;
    private const int HitBottom = 15;
    private const int HitBottomLeft = 16;
    private const int HitBottomRight = 17;

    internal static bool TryGetResizeHit(IntPtr windowHandle, IntPtr messageData, out IntPtr hitResult)
    {
        hitResult = IntPtr.Zero;
        if (IsZoomed(windowHandle) || !GetWindowRect(windowHandle, out var windowArea))
        {
            return false;
        }

        var packedPosition = messageData.ToInt64();
        var pointerX = unchecked((short)(packedPosition & 0xFFFF));
        var pointerY = unchecked((short)((packedPosition >> 16) & 0xFFFF));
        var resizeBorder = Math.Max(6, (int)Math.Round(8 * GetWindowDpi(windowHandle) / 96d));

        var onLeft = pointerX < windowArea.Left + resizeBorder;
        var onRight = pointerX >= windowArea.Right - resizeBorder;
        var onTop = pointerY < windowArea.Top + resizeBorder;
        var onBottom = pointerY >= windowArea.Bottom - resizeBorder;

        var hitCode = (onLeft, onRight, onTop, onBottom) switch
        {
            (true, _, true, _) => HitTopLeft,
            (_, true, true, _) => HitTopRight,
            (true, _, _, true) => HitBottomLeft,
            (_, true, _, true) => HitBottomRight,
            (true, _, _, _) => HitLeft,
            (_, true, _, _) => HitRight,
            (_, _, true, _) => HitTop,
            (_, _, _, true) => HitBottom,
            _ => 0
        };

        if (hitCode == 0)
        {
            return false;
        }

        hitResult = new IntPtr(hitCode);
        return true;
    }

    internal static bool TryConstrainToWorkArea(IntPtr windowHandle, IntPtr messageData)
    {
        var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitorHandle == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };

        if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return false;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(messageData);
        var monitorArea = monitorInfo.MonitorArea;
        var workArea = monitorInfo.WorkArea;

        minMaxInfo.MaxPosition.X = workArea.Left - monitorArea.Left;
        minMaxInfo.MaxPosition.Y = workArea.Top - monitorArea.Top;
        minMaxInfo.MaxSize.X = workArea.Right - workArea.Left;
        minMaxInfo.MaxSize.Y = workArea.Bottom - workArea.Top;
        minMaxInfo.MaxTrackSize = minMaxInfo.MaxSize;

        Marshal.StructureToPtr(minMaxInfo, messageData, false);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out Rect windowArea);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    private static uint GetWindowDpi(IntPtr windowHandle)
    {
        try
        {
            return GetDpiForWindow(windowHandle);
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        internal Point Reserved;
        internal Point MaxSize;
        internal Point MaxPosition;
        internal Point MinTrackSize;
        internal Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Rect MonitorArea;
        internal Rect WorkArea;
        internal uint Flags;
    }
}
