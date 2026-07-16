using System.Runtime.InteropServices;

namespace ScreenshotApp;

/// <summary>
/// 为自定义无边框窗口补齐 Windows Shell 识别最小化/还原动画所需的原生样式。
/// WindowChrome 仍负责非客户区绘制，因此不会恢复系统标题栏外观。
/// </summary>
internal static class NativeWindowAnimation
{
    internal const long WsCaption = 0x00C00000L;
    internal const long WsSysMenu = 0x00080000L;
    internal const long WsThickFrame = 0x00040000L;
    internal const long WsMinimizeBox = 0x00020000L;
    internal const long WsMaximizeBox = 0x00010000L;

    private const int GwlStyle = -16;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private static readonly long RequiredStyles =
        WsCaption |
        WsSysMenu |
        WsThickFrame |
        WsMinimizeBox |
        WsMaximizeBox;

    internal static long AddRequiredStyles(long currentStyle)
    {
        return currentStyle | RequiredStyles;
    }

    internal static bool HasRequiredStyles(long style)
    {
        return (style & RequiredStyles) == RequiredStyles;
    }

    internal static bool EnableSystemTransitions(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        var currentStyle = GetStyle(windowHandle);
        var updatedStyle = AddRequiredStyles(currentStyle);
        if (updatedStyle == currentStyle)
        {
            return true;
        }

        SetStyle(windowHandle, updatedStyle);
        return SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    internal static bool Minimize(IntPtr windowHandle)
    {
        return windowHandle != IntPtr.Zero && ShowWindowAsync(windowHandle, SwMinimize);
    }

    internal static bool Restore(IntPtr windowHandle)
    {
        return windowHandle != IntPtr.Zero && ShowWindowAsync(windowHandle, SwRestore);
    }

    private static long GetStyle(IntPtr windowHandle)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, GwlStyle).ToInt64()
            : GetWindowLong32(windowHandle, GwlStyle);
    }

    private static void SetStyle(IntPtr windowHandle, long style)
    {
        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtr64(windowHandle, GwlStyle, new IntPtr(style));
        }
        else
        {
            _ = SetWindowLong32(windowHandle, GwlStyle, unchecked((int)style));
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr windowHandle, int index, int newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);
}
