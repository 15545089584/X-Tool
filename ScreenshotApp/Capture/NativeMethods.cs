using System.Runtime.InteropServices;

namespace ScreenshotApp.Capture;

internal static class NativeMethods
{
    internal const int HotKeyId = 0x4A59;
    internal const int ClipboardHotKeyId = 0x4A5A;
    internal const int VoiceHotKeyId = 0x4A5B;
    internal const int FullScreenHotKeyId = 0x4A5C;
    internal const int ShortcutProbeHotKeyId = 0x4A5D;
    internal const int WmHotKey = 0x0312;
    internal const int WmClipboardUpdate = 0x031D;
    private const uint WmCancelMode = 0x001F;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModAlt = 0x0001;
    internal const uint ModWin = 0x0008;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoActivate = 0x0010;
    internal const int HwndTopmost = -1;
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const uint DibRgbColors = 0;
    internal const uint BiRgb = 0;
    internal const uint Srccopy = 0x00CC0020;
    internal const uint CaptureBlt = 0x40000000;
    internal const int WheelDelta = 120;
    internal const int GwlExStyle = -20;
    internal const long WsExTransparent = 0x00000020L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const uint WdaExcludeFromCapture = 0x00000011;
    private const int VirtualKeyEscape = 0x1B;
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseEventWheel = 0x0800;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyV = 0x56;
    private const ushort VirtualKeyBack = 0x08;
    private const uint GetAncestorRoot = 2;
    private const int RgnDiff = 4;
    private const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect WorkArea;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput Mouse;

        [FieldOffset(0)]
        internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateDIBSection(
        IntPtr deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmFlush();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr windowHandle, int index, int newValue);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint affinity);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr windowHandle, IntPtr region, bool redraw);

    internal static bool IsEscapePressed()
    {
        return (GetAsyncKeyState(VirtualKeyEscape) & 0x8000) != 0;
    }

    /// <summary>
    /// 关闭截图触发瞬间前台程序可能仍在显示的右键菜单、编辑菜单和下拉弹层。
    /// 全局热键消息到达时，目标程序有时已经创建菜单但尚未完成收起，直接抓屏
    /// 会把这层瞬态 UI 一并写入截图。消息发往触发前记录的前台窗口，不改变
    /// X-Tool 选择层的输入状态。
    /// </summary>
    internal static void DismissForegroundTransientUi(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return;
        }

        // 只发送 WM_CANCELMODE。不要向前台应用注入真实 ESC，否则浏览器全屏视频、
        // 播放器和编辑器会把截图触发误认为用户按下了取消键。
        _ = PostMessage(windowHandle, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
    }

    internal static bool ActivateWindowAtPoint(int x, int y)
    {
        var windowHandle = WindowFromPoint(new Point { X = x, Y = y });
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        var rootWindow = GetAncestor(windowHandle, GetAncestorRoot);
        return rootWindow != IntPtr.Zero && SetForegroundWindow(rootWindow);
    }

    internal static bool RestoreAndActivateWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        var rootWindow = GetAncestor(windowHandle, GetAncestorRoot);
        if (rootWindow == IntPtr.Zero)
        {
            rootWindow = windowHandle;
        }

        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(rootWindow, out _);
        var attached = targetThread != 0 && targetThread != currentThread && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            // 只恢复并激活顶层窗口，保留外部程序原来获得焦点的输入控件。
            // 对顶层窗口调用 SetFocus 会清空浏览器/编辑器内部输入框焦点，导致 Ctrl+V 没有接收方。
            _ = ShowWindow(rootWindow, SwRestore);
            _ = BringWindowToTop(rootWindow);
            _ = SetActiveWindow(rootWindow);
            var activated = SetForegroundWindow(rootWindow);
            return activated || GetForegroundWindow() == rootWindow;
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }

    internal static bool IsWindowForeground(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        var rootWindow = GetAncestor(windowHandle, GetAncestorRoot);
        return GetForegroundWindow() == (rootWindow == IntPtr.Zero ? windowHandle : rootWindow);
    }

    internal static void MakeWindowNonActivating(IntPtr windowHandle)
    {
        var currentStyle = IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, GwlExStyle).ToInt64()
            : GetWindowLong32(windowHandle, GwlExStyle);
        var newStyle = currentStyle | WsExToolWindow | WsExNoActivate;

        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtr64(windowHandle, GwlExStyle, new IntPtr(newStyle));
        }
        else
        {
            _ = SetWindowLong32(windowHandle, GwlExStyle, unchecked((int)newStyle));
        }
    }

    /// <summary>将提示窗重新置于顶层，但不夺取当前输入焦点。</summary>
    internal static void KeepWindowTopmostWithoutActivating(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowPos(
            windowHandle,
            new IntPtr(HwndTopmost),
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    internal static bool SendMouseWheel(int wheelDelta)
    {
        var inputs = new[]
        {
            new Input
            {
                Type = InputMouse,
                Data = new InputUnion
                {
                    Mouse = new MouseInput
                    {
                        MouseData = unchecked((uint)wheelDelta),
                        Flags = MouseEventWheel
                    }
                }
            }
        };

        return SendInput(1, inputs, Marshal.SizeOf<Input>()) == 1;
    }

    internal static bool SendPasteShortcut()
    {
        var inputs = new[]
        {
            new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = VirtualKeyControl } } },
            new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = VirtualKeyV } } },
            new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = VirtualKeyV, Flags = KeyEventKeyUp } } },
            new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = VirtualKeyControl, Flags = KeyEventKeyUp } } }
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    internal static bool SendBackspaces(int count)
    {
        if (count <= 0)
        {
            return true;
        }

        var inputs = new Input[count * 2];
        for (var index = 0; index < count; index++)
        {
            inputs[index * 2] = new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = VirtualKeyBack } }
            };
            inputs[index * 2 + 1] = new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = VirtualKeyBack, Flags = KeyEventKeyUp } }
            };
        }

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    internal static bool SendUnicodeCharacter(char character)
    {
        var inputs = new[]
        {
            new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = KeyEventUnicode } } },
            new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = KeyEventUnicode | KeyEventKeyUp } } }
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    internal static void MakeWindowMouseTransparent(IntPtr windowHandle)
    {
        var currentStyle = IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, GwlExStyle).ToInt64()
            : GetWindowLong32(windowHandle, GwlExStyle);
        var newStyle = currentStyle | WsExTransparent | WsExToolWindow | WsExNoActivate;

        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtr64(windowHandle, GwlExStyle, new IntPtr(newStyle));
        }
        else
        {
            _ = SetWindowLong32(windowHandle, GwlExStyle, unchecked((int)newStyle));
        }
    }

    internal static void MakeWindowFrameOnly(IntPtr windowHandle, int width, int height, int borderSize)
    {
        var outerRegion = CreateRectRgn(0, 0, width, height);
        var innerRegion = CreateRectRgn(
            borderSize,
            borderSize,
            Math.Max(borderSize, width - borderSize),
            Math.Max(borderSize, height - borderSize));
        if (outerRegion == IntPtr.Zero || innerRegion == IntPtr.Zero)
        {
            if (outerRegion != IntPtr.Zero)
            {
                DeleteObject(outerRegion);
            }

            if (innerRegion != IntPtr.Zero)
            {
                DeleteObject(innerRegion);
            }

            return;
        }

        _ = CombineRgn(outerRegion, outerRegion, innerRegion, RgnDiff);
        DeleteObject(innerRegion);
        if (SetWindowRgn(windowHandle, outerRegion, true) == 0)
        {
            // SetWindowRgn 成功后由系统接管 region；失败时由当前进程释放。
            DeleteObject(outerRegion);
        }
    }
}
