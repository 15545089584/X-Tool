using System.Net.Http;
using MailKit.Net.Proxy;
namespace ScreenshotApp.Mail;
public static class MailNetwork
{
    // IMAP 使用独立 TCP 连接，需要显式接入系统代理；不降级 TLS，不自动绕过代理。
    public static IProxyClient? SystemProxy(string host)
    {
        var destination = new Uri("https://" + host + ":993");
        var proxy = HttpClient.DefaultProxy;
        if (proxy.IsBypassed(destination)) return null;
        var uri = proxy.GetProxy(destination);
        if (uri is null || uri == destination) return null;
        return uri.Scheme.ToLowerInvariant() switch
        {
            "http" => new HttpProxyClient(uri.Host, uri.Port),
            "https" => new HttpsProxyClient(uri.Host, uri.Port),
            "socks5" => new Socks5Client(uri.Host, uri.Port),
            _ => throw new InvalidOperationException("当前系统代理类型不受支持，请使用 HTTP CONNECT 或 SOCKS5 代理。")
        };
    }
}
