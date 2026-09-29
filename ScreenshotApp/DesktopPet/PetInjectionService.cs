using System.Runtime.InteropServices;
using ScreenshotApp.Capture;

namespace ScreenshotApp.DesktopPet;

/// <summary>只向倒计时结束时的前台焦点发送 Unicode，不读取或修改剪贴板。</summary>
internal static class PetInjectionService
{
    internal readonly record struct Target(IntPtr Window, IntPtr Focus);
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    {
        public int Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public Keyboard Keyboard;
        [FieldOffset(0)] public Mouse Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    internal static bool EscapePressed => (GetAsyncKeyState(0x1B) & 0x8000) != 0;
    internal static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");
    internal static Target CaptureTarget()
    {
        var window = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(window, out var process);
        var info = new GuiInfo { Size = Marshal.SizeOf<GuiInfo>() };
        if (window == IntPtr.Zero || process == Environment.ProcessId || !GetGUIThreadInfo(thread, ref info) || info.Focus == IntPtr.Zero)
            throw new InvalidOperationException("未选中外部输入框，已停止注入");
        return new(window, info.Focus);
    }
    private static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0 } } };
    private static bool NewLine()
    {
        var inputs = new[] { Key(0x10), Key(0x0D), Key(0x0D, true), Key(0x10, true) };
        if (SendInput(4, inputs, Marshal.SizeOf<Input>()) == 4) return true;
        // 部分发送失败时仍释放本次按下的修饰键。
        var release = new[] { Key(0x0D, true), Key(0x10, true) };
        SendInput(2, release, Marshal.SizeOf<Input>());
        return false;
    }
    internal static Task WriteAsync(string text, IProgress<int> progress, CancellationToken token)
    {
        // 浏览器多个 DOM 输入框可能共用 HWND，额外检测点击和 Tab，避免用户切换后继续写入。
        foreach (var key in new[] { 1, 2, 4, 9 }) GetAsyncKeyState(key);
        return WriteCoreAsync(text, progress, token, CaptureTarget, () => EscapePressed,
            () => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0)
                || new[] { 1, 2, 4, 9 }.Any(k => (GetAsyncKeyState(k) & 0x8001) != 0),
            ch => ch == '\n' ? NewLine() : NativeMethods.SendUnicodeCharacter(ch));
    }

    internal static async Task WriteCoreAsync(string text, IProgress<int> progress, CancellationToken token,
        Func<Target> capture, Func<bool> escape, Func<bool> modifiers, Func<char, bool> send)
    {
        token.ThrowIfCancellationRequested();
        var target = capture();
        text = Normalize(text);
        for (int i = 0; i < text.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (escape()) throw new OperationCanceledException();
            if (capture() != target) throw new InvalidOperationException("输入焦点已改变，写入已停止");
            if (modifiers())
                throw new InvalidOperationException("检测到键盘或鼠标操作，写入已停止");
            var ch = text[i];
            if (char.IsControl(ch) && ch != '\n') continue;
            if (!send(ch))
                throw new InvalidOperationException("目标拒绝模拟输入，请检查窗口权限");
            if (i % 8 == 0 || i == text.Length - 1) progress.Report((i + 1) * 100 / text.Length);
            await Task.Delay(10, token).ConfigureAwait(false);
        }
        progress.Report(100);
    }
}
