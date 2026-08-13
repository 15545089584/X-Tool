using LibreHardwareMonitor.PawnIo;
using ScreenshotApp.HardwareMonitoring;

namespace XTool.HardwareSensorAgent;

/// <summary>仅返回可展示的 PawnIO 状态，不向普通权限进程暴露驱动细节。</summary>
internal static class PawnIoAccessProbe
{
    public static string GetSummary()
    {
        try
        {
            if (!PawnIo.IsInstalled)
            {
                return "底层访问：未安装 PawnIO，正在使用基础只读访问";
            }

            string version = Limit(PawnIo.Version?.ToString());
            return string.IsNullOrWhiteSpace(version)
                ? "底层访问：已检测到 PawnIO，正在由只读采集器验证传感器"
                : $"底层访问：已检测到 PawnIO {version}，正在由只读采集器验证传感器";
        }
        catch
        {
            // 不把底层驱动异常、设备路径或调用细节跨进程传递。
            return "底层访问：无法确认 PawnIO 状态，正在使用基础只读访问";
        }
    }

    private static string Limit(string? value)
    {
        string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= HardwareSensorProtocol.MaximumStringLength - 48
            ? normalized
            : normalized[..(HardwareSensorProtocol.MaximumStringLength - 48)];
    }
}
