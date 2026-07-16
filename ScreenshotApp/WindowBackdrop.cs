using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenshotApp;

/// <summary>
/// 为主窗口启用 Windows 11 系统级桌面亚克力背板。
/// 不支持该能力时直接返回，由 XAML 中的半透明渐变负责视觉降级。
/// </summary>
internal static class WindowBackdrop
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmSystemBackdropType = 38;
    private const int RoundCornerPreference = 2;
    private const int TransientWindowBackdrop = 3;
    private const int DwmColorNone = -2;

    public static bool TryApply(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return false;
        }

        try
        {
            var backdropType = TransientWindowBackdrop;
            var backdropResult = DwmSetWindowAttribute(
                windowHandle,
                DwmSystemBackdropType,
                ref backdropType,
                Marshal.SizeOf<int>());

            if (backdropResult < 0)
            {
                return false;
            }

            var cornerPreference = RoundCornerPreference;
            _ = DwmSetWindowAttribute(
                windowHandle,
                DwmWindowCornerPreference,
                ref cornerPreference,
                Marshal.SizeOf<int>());

            // 禁用 DWM 根据系统强调色绘制的 1 像素外边框，避免桌面颜色在窗口四周形成色边。
            var borderColor = DwmColorNone;
            _ = DwmSetWindowAttribute(
                windowHandle,
                DwmBorderColor,
                ref borderColor,
                Marshal.SizeOf<int>());

            var source = HwndSource.FromHwnd(windowHandle);
            if (source?.CompositionTarget is not null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

}
