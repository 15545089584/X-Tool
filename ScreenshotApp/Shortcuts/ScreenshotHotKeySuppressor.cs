using System.Runtime.InteropServices;

namespace ScreenshotApp.Shortcuts;

/// <summary>
/// 在保留 RegisterHotKey 冲突检测的同时，吞掉截图快捷键的目标键消息。
/// 部分浏览器和编辑器会在收到同一组合键后执行自己的命令，仅依赖 WM_HOTKEY
/// 不足以阻止这些应用先打开菜单；低级钩子只处理已注册的截图组合键。
/// </summary>
internal sealed class ScreenshotHotKeySuppressor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint VkControl = 0x11;
    private const uint VkShift = 0x10;
    private const uint VkMenu = 0x12;
    private const uint VkLeftWindows = 0x5B;
    private const uint VkRightWindows = 0x5C;
    private const uint VkApps = 0x5D;
    private const uint VkSnapshot = 0x2C;

    private readonly GlobalShortcut _shortcut;
    private readonly HookProcedure _procedure;
    private IntPtr _hook;
    private int _suppressTargetUntilUp;
    private int _lastInstallError;

    internal ScreenshotHotKeySuppressor(GlobalShortcut shortcut)
    {
        _shortcut = shortcut;
        _procedure = HookCallback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _procedure, GetModuleHandle(IntPtr.Zero), 0);
        if (_hook == IntPtr.Zero)
        {
            _lastInstallError = Marshal.GetLastWin32Error();
            _hook = SetWindowsHookEx(WhKeyboardLl, _procedure, IntPtr.Zero, 0);
            if (_hook == IntPtr.Zero)
            {
                _lastInstallError = Marshal.GetLastWin32Error();
            }
        }
    }

    internal bool IsInstalled => _hook != IntPtr.Zero;

    internal int LastInstallError => _lastInstallError;

    internal event EventHandler? Pressed;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        Interlocked.Exchange(ref _suppressTargetUntilUp, 0);
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var hookData = Marshal.PtrToStructure<KeyboardLowLevelHookData>(data);
            var keyboardMessage = message.ToInt32();
            var isDown = keyboardMessage is WmKeyDown or WmSysKeyDown;
            var isUp = keyboardMessage is WmKeyUp or WmSysKeyUp;
            if ((isDown || isUp) && hookData.VirtualKeyCode == _shortcut.VirtualKey)
            {
                if (isDown && (IsExactModifierState() || IsShellReservedShortcutKey()))
                {
                    if (Interlocked.Exchange(ref _suppressTargetUntilUp, 1) == 0)
                    {
                        // 只在钩子线程记录触发，实际启动截图切回 WPF UI 线程。
                        Pressed?.Invoke(this, EventArgs.Empty);
                    }

                    return new IntPtr(1);
                }

                if (isUp && Interlocked.Exchange(ref _suppressTargetUntilUp, 0) == 1)
                {
                    return new IntPtr(1);
                }

                if (isDown && Volatile.Read(ref _suppressTargetUntilUp) == 1)
                {
                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private bool IsExactModifierState()
    {
        return IsKeyDown(VkControl) == _shortcut.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) &&
               IsKeyDown(VkShift) == _shortcut.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) &&
               IsKeyDown(VkMenu) == _shortcut.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt) &&
               IsKeyDown(VkLeftWindows) == _shortcut.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows) &&
               IsKeyDown(VkRightWindows) == _shortcut.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows);
    }

    private bool IsShellReservedShortcutKey() =>
        _shortcut.VirtualKey is VkApps or VkSnapshot;

    private static bool IsKeyDown(uint virtualKey) =>
        (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(IntPtr moduleName);
}
