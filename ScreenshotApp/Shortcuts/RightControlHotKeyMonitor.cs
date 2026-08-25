using System.Runtime.InteropServices;

namespace ScreenshotApp.Shortcuts;

/// <summary>监听右 Ctrl 单独按下并抬起；与其他按键组合时不触发，也不吞掉原始键盘消息。</summary>
internal sealed class RightControlHotKeyMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint VkControl = 0x11;
    private const uint VkRightControl = 0xA3;
    private const uint LlkhfExtended = 0x01;

    private readonly HookProcedure _procedure;
    private IntPtr _hook;
    private int _isRightControlDown;
    private int _isCandidate;

    internal RightControlHotKeyMonitor()
    {
        _procedure = HookCallback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _procedure, GetModuleHandle(null), 0);
    }

    internal bool IsInstalled => _hook != IntPtr.Zero;

    internal event EventHandler? Pressed;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var hookData = Marshal.PtrToStructure<KeyboardLowLevelHookData>(data);
            var keyboardMessage = message.ToInt32();
            var isDown = keyboardMessage is WmKeyDown or WmSysKeyDown;
            var isUp = keyboardMessage is WmKeyUp or WmSysKeyUp;
            if (IsRightControl(hookData))
            {
                if (isDown && Interlocked.Exchange(ref _isRightControlDown, 1) == 0)
                {
                    Interlocked.Exchange(ref _isCandidate, 1);
                }
                else if (isUp)
                {
                    Interlocked.Exchange(ref _isRightControlDown, 0);
                    if (Interlocked.Exchange(ref _isCandidate, 0) == 1)
                    {
                        Pressed?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            else if (isDown && Volatile.Read(ref _isRightControlDown) == 1)
            {
                // 右 Ctrl 与其他键组成快捷键时保留原行为，不触发全屏截图。
                Interlocked.Exchange(ref _isCandidate, 0);
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private static bool IsRightControl(KeyboardLowLevelHookData data) =>
        data.VirtualKeyCode == VkRightControl ||
        (data.VirtualKeyCode == VkControl && (data.Flags & LlkhfExtended) != 0);

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

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
    private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure procedure, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
