using System.Runtime.InteropServices;

namespace ScreenshotApp.Capture;

/// <summary>
/// 记录长截图期间用户真实的滚轮和翻页方向。
/// 图像匹配仍负责计算精确位移，输入方向只用于消除上下方向歧义。
/// </summary>
internal sealed class ScrollInputDirectionMonitor : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmMouseWheel = 0x020A;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkUp = 0x26;
    private const int VkDown = 0x28;
    private const int VkPageUp = 0x21;
    private const int VkPageDown = 0x22;
    private const int VkSpace = 0x20;
    private const int VkHome = 0x24;
    private const int VkEnd = 0x23;

    private readonly HookProcedure _mouseProcedure;
    private readonly HookProcedure _keyboardProcedure;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private int _latestDirection;

    internal ScrollInputDirectionMonitor()
    {
        _mouseProcedure = MouseHookCallback;
        _keyboardProcedure = KeyboardHookCallback;
        var moduleHandle = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProcedure, moduleHandle, 0);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProcedure, moduleHandle, 0);
    }

    /// <summary>
    /// 读取自上次采样以来最后一次滚动方向。正数向下，负数向上。
    /// 用户快速反向回滚时，最后一次输入比简单相加更能代表当前画面方向。
    /// </summary>
    internal int ConsumeDirection()
    {
        return Interlocked.Exchange(ref _latestDirection, 0);
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() == WmMouseWheel)
        {
            var hookData = Marshal.PtrToStructure<MouseLowLevelHookData>(data);
            var wheelDelta = unchecked((short)((hookData.MouseData >> 16) & 0xFFFF));
            // Windows 的正滚轮值表示页面向上移动，因此内容坐标方向为负。
            RecordDirection(wheelDelta > 0 ? -1 : 1);
        }

        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message.ToInt32() == WmKeyDown || message.ToInt32() == WmSysKeyDown))
        {
            var hookData = Marshal.PtrToStructure<KeyboardLowLevelHookData>(data);
            var direction = hookData.VirtualKeyCode switch
            {
                VkUp or VkPageUp or VkHome => -1,
                VkDown or VkPageDown or VkSpace or VkEnd => 1,
                _ => 0
            };
            if (direction != 0)
            {
                RecordDirection(direction);
            }
        }

        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private void RecordDirection(int direction)
    {
        Interlocked.Exchange(ref _latestDirection, direction);
    }

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseLowLevelHookData
    {
        internal NativeMethods.Point Point;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardLowLevelHookData
    {
        internal uint VirtualKeyCode;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        HookProcedure procedure,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr message,
        IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
