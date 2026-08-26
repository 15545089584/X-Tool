using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace ScreenshotApp.InformationVault;

/// <summary>只对明确白名单中的登录客户端执行填入，不负责点击登录按钮。</summary>
internal static class CredentialAutoFillService
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const int SwRestore = 9;

    private static readonly IReadOnlyDictionary<InformationVaultEntryType, AutoFillTarget> Targets =
        new Dictionary<InformationVaultEntryType, AutoFillTarget>
        {
            [InformationVaultEntryType.Steam] = new(
                "Steam",
                ["steam", "steamwebhelper"],
                ["steam", "登录", "sign in"]),
            [InformationVaultEntryType.Ubisoft] = new(
                "Ubisoft Connect",
                ["ubisoftconnect", "upc", "ubisoftgamelauncher"],
                ["ubisoft", "登录", "sign in"]),
            [InformationVaultEntryType.Epic] = new(
                "Epic Games",
                ["epicgameslauncher", "epicwebhelper"],
                ["epic", "登录", "sign in"])
        };

    internal static Task<CredentialAutoFillResult> FillAsync(InformationVaultEntry entry)
    {
        return Task.Run(() => FillCore(entry));
    }

    private static CredentialAutoFillResult FillCore(InformationVaultEntry entry)
    {
        if (!Targets.TryGetValue(entry.Type, out var target))
        {
            return CredentialAutoFillResult.Failed("这类记录不支持一键填入，请使用复制。", CredentialAutoFillFailure.UnsupportedType);
        }

        if (string.IsNullOrWhiteSpace(entry.Secret))
        {
            return CredentialAutoFillResult.Failed("记录中没有密码，无法填入。", CredentialAutoFillFailure.MissingSecret);
        }

        var windows = EnumerateCandidateWindows(target);
        if (windows.Count == 0)
        {
            return CredentialAutoFillResult.Failed($"未检测到 {target.DisplayName} 登录窗口，请先打开客户端登录页。", CredentialAutoFillFailure.TargetNotFound);
        }

        foreach (var candidate in windows.OrderByDescending(window => window.Score))
        {
            try
            {
                var root = AutomationElement.FromHandle(candidate.Handle);
                if (root is null)
                {
                    continue;
                }

                var edits = root.FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                    .Cast<AutomationElement>()
                    .Where(IsUsableEdit)
                    .ToList();
                if (edits.Count == 0)
                {
                    continue;
                }

                var passwordEdit = edits.FirstOrDefault(IsPasswordEdit) ?? edits.FirstOrDefault(IsLikelyPasswordEdit);

                if (passwordEdit is null)
                {
                    continue;
                }

                var accountEdit = edits.FirstOrDefault(edit => !ReferenceEquals(edit, passwordEdit) && !IsPasswordEdit(edit));
                NativeMethods.ShowWindow(candidate.Handle, SwRestore);
                NativeMethods.SetForegroundWindow(candidate.Handle);
                Thread.Sleep(120);

                var accountFilled = string.IsNullOrWhiteSpace(entry.Account);
                if (!string.IsNullOrWhiteSpace(entry.Account) && accountEdit is not null)
                {
                    accountFilled = TrySetValue(accountEdit, entry.Account);
                }

                var passwordFilled = TrySetValue(passwordEdit, entry.Secret);
                if (!passwordFilled)
                {
                    continue;
                }

                var detail = accountFilled
                    ? $"已向 {target.DisplayName} 填入登录信息，请核对后手动登录。"
                    : $"已填入 {target.DisplayName} 密码；账号框未被客户端公开，请手动复制账号。";
                return CredentialAutoFillResult.Succeeded(detail, candidate.Title);
            }
            catch (ElementNotAvailableException)
            {
                // 客户端重绘登录页时元素会失效，继续尝试同一客户端的其他窗口。
            }
            catch (InvalidOperationException)
            {
                // 不支持 ValuePattern 的自绘控件交由后续候选窗口处理。
            }
            catch (COMException)
            {
                // UI Automation 提供程序不可用时继续尝试其他候选窗口。
            }
        }

        return CredentialAutoFillResult.Failed(
            $"检测到 {target.DisplayName}，但当前登录页没有公开可安全定位的账号和密码输入框，请使用复制。",
            CredentialAutoFillFailure.InputControlsUnavailable);
    }

    private static List<CandidateWindow> EnumerateCandidateWindows(AutoFillTarget target)
    {
        var windows = new List<CandidateWindow>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.GetWindowTextLength(handle) <= 0)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(handle, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                var processName = process.ProcessName;
                var processIndex = target.ProcessNames.FindIndex(name =>
                    string.Equals(name, processName, StringComparison.OrdinalIgnoreCase));
                if (processIndex < 0)
                {
                    return true;
                }

                var title = GetWindowTitle(handle);
                var titleMatches = target.TitleKeywords.Any(keyword =>
                    title.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                var score = (target.ProcessNames.Count - processIndex) * 10 + (titleMatches ? 50 : 0);
                windows.Add(new CandidateWindow(handle, title, score));
            }
            catch (ArgumentException)
            {
                // 进程恰好退出。
            }
            catch (InvalidOperationException)
            {
                // 进程信息暂时不可访问。
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        var builder = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static bool IsUsableEdit(AutomationElement element)
    {
        try
        {
            return element.Current.IsEnabled && !element.Current.IsOffscreen;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool IsPasswordEdit(AutomationElement element)
    {
        try
        {
            return element.Current.IsPassword;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool IsLikelyPasswordEdit(AutomationElement element)
    {
        try
        {
            var searchableText = string.Join(
                ' ',
                element.Current.Name,
                element.Current.AutomationId,
                element.Current.HelpText);
            return searchableText.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                   searchableText.Contains("passcode", StringComparison.OrdinalIgnoreCase) ||
                   searchableText.Contains("密码", StringComparison.OrdinalIgnoreCase);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool TrySetValue(AutomationElement element, string value)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObject) &&
                patternObject is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly)
            {
                valuePattern.SetValue(value);
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // 受保护输入框通常拒绝 ValuePattern，继续使用聚焦后的 Unicode 键盘输入。
        }

        try
        {
            element.SetFocus();
            Thread.Sleep(45);
            SendControlA();
            return SendUnicodeText(value);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void SendControlA()
    {
        const ushort controlKey = 0x11;
        const ushort aKey = 0x41;
        var inputs = new[]
        {
            KeyboardInput(controlKey, 0),
            KeyboardInput(aKey, 0),
            KeyboardInput(aKey, KeyEventKeyUp),
            KeyboardInput(controlKey, KeyEventKeyUp)
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
    }

    private static bool SendUnicodeText(string text)
    {
        var inputs = new List<NativeMethods.Input>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(KeyboardInput(0, KeyEventUnicode, character));
            inputs.Add(KeyboardInput(0, KeyEventUnicode | KeyEventKeyUp, character));
        }

        return inputs.Count == 0 ||
               NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.Input>()) == inputs.Count;
    }

    private static NativeMethods.Input KeyboardInput(ushort virtualKey, uint flags, char unicodeCharacter = '\0') =>
        new()
        {
            Type = InputKeyboard,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = unicodeCharacter,
                    Flags = flags
                }
            }
        };

    private sealed record AutoFillTarget(string DisplayName, List<string> ProcessNames, List<string> TitleKeywords);

    private sealed record CandidateWindow(IntPtr Handle, string Title, int Score);

    private static class NativeMethods
    {
        internal delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            internal uint Type;
            internal InputUnion Union;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)]
            internal KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput
        {
            internal ushort VirtualKey;
            internal ushort ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr windowHandle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr windowHandle);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr windowHandle, int command);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);
    }
}

internal enum CredentialAutoFillFailure
{
    None,
    UnsupportedType,
    MissingSecret,
    TargetNotFound,
    InputControlsUnavailable
}

internal sealed record CredentialAutoFillResult(
    bool Success,
    string Message,
    CredentialAutoFillFailure Failure,
    string TargetWindowTitle)
{
    internal static CredentialAutoFillResult Succeeded(string message, string title) =>
        new(true, message, CredentialAutoFillFailure.None, title);

    internal static CredentialAutoFillResult Failed(string message, CredentialAutoFillFailure failure) =>
        new(false, message, failure, string.Empty);
}
