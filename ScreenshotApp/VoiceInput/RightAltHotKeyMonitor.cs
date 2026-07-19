using System.Runtime.InteropServices;

namespace ScreenshotApp.VoiceInput;

/// <summary>监听右 Alt 单键，不吞掉原始键盘消息。</summary>
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
    private const uint LlkhfExtended = 0x01;
    private readonly HookProcedure _procedure;
    private readonly bool _listenRightAlt;
    private IntPtr _hook;
    private int _isRightAltDown;
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
                if ((keyboardMessage == WmKeyDown || keyboardMessage == WmSysKeyDown) &&
                    Interlocked.Exchange(ref _isRightAltDown, 1) == 0)
                {
                    Pressed?.Invoke(this, EventArgs.Empty);
                }
                else if (keyboardMessage == WmKeyUp || keyboardMessage == WmSysKeyUp)
                {
                    Interlocked.Exchange(ref _isRightAltDown, 0);
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
