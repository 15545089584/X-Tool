using System.Runtime.InteropServices;

namespace ScreenshotApp.VoiceInput;

/// <summary>监听右 Alt 单键，并在触发热键时拦截原始按键消息以保留目标输入框焦点。</summary>
internal sealed class RightAltHotKeyMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint VkMenu = 0x12;
    private const uint VkRightMenu = 0xA5;
    private const uint VkEscape = 0x1B;
    private const uint VkControl = 0x11;
    private const uint VkRightControl = 0xA3;
    private const uint VkShift = 0x10;
    private const uint VkLeftWindows = 0x5B;
    private const uint VkRightWindows = 0x5C;
    private const uint LlkhfExtended = 0x01;
    private readonly HookProcedure _procedure;
    private readonly bool _listenRightAlt;
    private IntPtr _hook;
    private int _isRightAltDown;
    private int _isRightAltCandidate;
    private int _suppressRightAltUntilUp;
    private int _isEscapeDown;
    private int _isTranslationKeyDown;

    internal RightAltHotKeyMonitor(bool listenRightAlt = true)
    {
        _listenRightAlt = listenRightAlt;
        _procedure = HookCallback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _procedure, GetModuleHandle(null), 0);
    }

    internal bool IsInstalled => _hook != IntPtr.Zero;

    internal event EventHandler? Pressed;
    internal event EventHandler? EscapePressed;
    internal event EventHandler? TranslationPressed;

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
            if (hookData.VirtualKeyCode == VkEscape)
            {
                if ((keyboardMessage == WmKeyDown || keyboardMessage == WmSysKeyDown) &&
                    Interlocked.Exchange(ref _isEscapeDown, 1) == 0)
                {
                    EscapePressed?.Invoke(this, EventArgs.Empty);
                }
                else if (keyboardMessage == WmKeyUp || keyboardMessage == WmSysKeyUp)
                {
                    Interlocked.Exchange(ref _isEscapeDown, 0);
                }
            }
            else if (IsRightControl(hookData))
            {
                if ((keyboardMessage == WmKeyDown || keyboardMessage == WmSysKeyDown) &&
                    Interlocked.Exchange(ref _isTranslationKeyDown, 1) == 0)
                {
                    TranslationPressed?.Invoke(this, EventArgs.Empty);
                }
                else if (keyboardMessage == WmKeyUp || keyboardMessage == WmSysKeyUp)
                {
                    Interlocked.Exchange(ref _isTranslationKeyDown, 0);
                }
            }
            else if (_listenRightAlt && IsRightAlt(hookData))
            {
                if (keyboardMessage == WmKeyDown || keyboardMessage == WmSysKeyDown)
                {
                    if (Interlocked.Exchange(ref _isRightAltDown, 1) == 0)
                    {
                        // AltGr 与其他组合键仍交由原程序处理，避免破坏正常输入。
                        var isSingleRightAlt = !IsCompanionModifierDown();
                        Interlocked.Exchange(ref _isRightAltCandidate, isSingleRightAlt ? 1 : 0);
                        Interlocked.Exchange(ref _suppressRightAltUntilUp, isSingleRightAlt ? 1 : 0);
                    }
                }
                else if (keyboardMessage == WmKeyUp || keyboardMessage == WmSysKeyUp)
                {
                    Interlocked.Exchange(ref _isRightAltDown, 0);
                    var shouldTrigger = Interlocked.Exchange(ref _isRightAltCandidate, 0) == 1;
                    var shouldSuppress = Interlocked.Exchange(ref _suppressRightAltUntilUp, 0) == 1;
                    if (shouldTrigger)
                    {
                        // 在抬键后才显示浮窗，前台窗口已稳定，避免浏览器的 Alt 菜单抢占焦点。
                        Pressed?.Invoke(this, EventArgs.Empty);
                    }

                    if (shouldSuppress)
                    {
                        return new IntPtr(1);
                    }
                }

                if (Volatile.Read(ref _suppressRightAltUntilUp) == 1)
                {
                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private static bool IsRightAlt(KeyboardLowLevelHookData data)
    {
        return data.VirtualKeyCode == VkRightMenu ||
               (data.VirtualKeyCode == VkMenu && (data.Flags & LlkhfExtended) != 0);
    }

    private static bool IsRightControl(KeyboardLowLevelHookData data)
    {
        return data.VirtualKeyCode == VkRightControl ||
               (data.VirtualKeyCode == VkControl && (data.Flags & LlkhfExtended) != 0);
    }

    private static bool IsCompanionModifierDown()
    {
        return IsKeyDown(VkControl) || IsKeyDown(VkShift) || IsKeyDown(VkLeftWindows) || IsKeyDown(VkRightWindows);
    }

    private static bool IsKeyDown(uint virtualKey) => (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
