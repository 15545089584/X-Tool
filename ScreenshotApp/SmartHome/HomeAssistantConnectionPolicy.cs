using System.Net;
using System.Net.Http;

namespace ScreenshotApp.SmartHome;

/// <summary>仅 HA 私网目标直连，不修改系统代理，也不影响其他模块。</summary>
internal static class HomeAssistantConnectionPolicy
{
    // 中继链路可能有较大抖动；每阶段有界，整体初始化也有独立上限。
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(90);

    internal static bool BypassProxy(Uri serverUri)
    {
        var host = serverUri.IdnHost.TrimEnd('.');
        if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }
        // IPv6 本地链路和 ULA（包含 Tailscale 的 fd7a:115c:a1e0::/48）。
        return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }

    internal static HttpClientHandler CreateHttpHandler(Uri serverUri) => new()
    {
        UseProxy = !BypassProxy(serverUri),
        // 不携带认证信息跟随重定向到其他目标。
        AllowAutoRedirect = false
    };
}
