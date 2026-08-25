using System.Windows.Input;

namespace ScreenshotApp.Shortcuts;

/// <summary>可持久化的全局快捷键描述与 Windows 热键参数转换。</summary>
internal readonly record struct GlobalShortcut(
    Key Key,
    ModifierKeys Modifiers,
    bool IsRightAlt = false,
    bool IsRightCtrl = false)
{
    internal static GlobalShortcut ScreenshotDefault => new(Key.A, ModifierKeys.Control | ModifierKeys.Shift);

    internal static GlobalShortcut FullScreenDefault => new(Key.RightCtrl, ModifierKeys.None, IsRightCtrl: true);

    internal static GlobalShortcut ClipboardDefault => new(Key.V, ModifierKeys.Control | ModifierKeys.Shift);

    internal static GlobalShortcut VoiceDefault => new(Key.RightAlt, ModifierKeys.None, true);

    internal string DisplayText => IsRightAlt
        ? "右 Alt"
        : IsRightCtrl
            ? "右 Ctrl"
            : string.Join(" + ", GetModifierNames().Append(GetKeyName()));

    internal uint NativeModifiers
    {
        get
        {
            var modifiers = 0u;
            if (Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= Capture.NativeMethods.ModControl;
            if (Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= Capture.NativeMethods.ModShift;
            if (Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= Capture.NativeMethods.ModAlt;
            if (Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= Capture.NativeMethods.ModWin;
            return modifiers;
        }
    }

    internal uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    internal bool IsSupportedGlobalCombination =>
        !IsRightAlt && !IsRightCtrl &&
        Key != Key.None &&
        !IsModifierKey(Key) &&
        !Modifiers.HasFlag(ModifierKeys.Windows);

    internal bool IsKnownWindowsReserved =>
        Modifiers.HasFlag(ModifierKeys.Windows) ||
        (Modifiers == ModifierKeys.Alt && Key is Key.Tab or Key.F4 or Key.Escape) ||
        (Modifiers.HasFlag(ModifierKeys.Control) && Modifiers.HasFlag(ModifierKeys.Alt) && Key == Key.Delete);

    internal static GlobalShortcut FromKey(Key key, ModifierKeys modifiers) =>
        key == Key.RightAlt && modifiers == ModifierKeys.None
            ? VoiceDefault
            : key == Key.RightCtrl && modifiers == ModifierKeys.Control
                ? FullScreenDefault
                : new GlobalShortcut(key, modifiers);

    internal static bool TryParse(string? value, GlobalShortcut fallback, out GlobalShortcut shortcut)
    {
        shortcut = fallback;
        if (string.Equals(value, "RightAlt", StringComparison.OrdinalIgnoreCase))
        {
            shortcut = VoiceDefault;
            return true;
        }

        if (string.Equals(value, "RightCtrl", StringComparison.OrdinalIgnoreCase))
        {
            shortcut = FullScreenDefault;
            return true;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var segment in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (segment.ToUpperInvariant())
            {
                case "CTRL": modifiers |= ModifierKeys.Control; break;
                case "SHIFT": modifiers |= ModifierKeys.Shift; break;
                case "ALT": modifiers |= ModifierKeys.Alt; break;
                case "WIN": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (!Enum.TryParse<Key>(segment, true, out var parsedKey))
                    {
                        return false;
                    }

                    key = parsedKey;
                    break;
            }
        }

        if (key is null || IsModifierKey(key.Value))
        {
            return false;
        }

        shortcut = new GlobalShortcut(key.Value, modifiers);
        return true;
    }

    internal string ToPreferenceValue() => IsRightAlt
        ? "RightAlt"
        : IsRightCtrl
            ? "RightCtrl"
            : string.Join("+", GetModifierNames().Append(Key.ToString()));

    private IEnumerable<string> GetModifierNames()
    {
        if (Modifiers.HasFlag(ModifierKeys.Control)) yield return "Ctrl";
        if (Modifiers.HasFlag(ModifierKeys.Shift)) yield return "Shift";
        if (Modifiers.HasFlag(ModifierKeys.Alt)) yield return "Alt";
        if (Modifiers.HasFlag(ModifierKeys.Windows)) yield return "Win";
    }

    private string GetKeyName() => Key switch
    {
        Key.LeftAlt => "左 Alt",
        Key.RightAlt => "右 Alt",
        Key.LeftCtrl => "左 Ctrl",
        Key.RightCtrl => "右 Ctrl",
        Key.LeftShift => "左 Shift",
        Key.RightShift => "右 Shift",
        _ => Key.ToString()
    };

    internal static bool IsModifierKey(Key key) => key is Key.LeftAlt or Key.RightAlt or
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
}
