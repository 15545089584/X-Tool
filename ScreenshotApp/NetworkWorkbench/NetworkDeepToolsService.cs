using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ScreenshotApp.NetworkWorkbench;

internal static class NetworkDeepToolsService
{
    public static async Task<NetworkDeepSnapshot> ReadAsync(CancellationToken token)
    {
        var routeTask = RunAsync("route.exe", "print", token);
        var firewallTask = RunAsync("netsh.exe", "advfirewall show allprofiles", token);
        var wifiTask = RunAsync("netsh.exe", "wlan show interfaces", token);
        var nearbyTask = RunAsync("netsh.exe", "wlan show networks mode=bssid", token);
        await Task.WhenAll(routeTask, firewallTask, wifiTask, nearbyTask);
        return new NetworkDeepSnapshot(routeTask.Result, firewallTask.Result, wifiTask.Result, nearbyTask.Result,
            BuildConnectionExplanation(routeTask.Result, firewallTask.Result));
    }

    public static async Task<IReadOnlyList<LanDeviceEntry>> ScanPrivateLanAsync(string localAddress, CancellationToken token)
    {
        if (!IPAddress.TryParse(localAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(address))
            throw new InvalidOperationException("局域网发现仅允许扫描当前私有 IPv4 /24 网段。");
        var bytes = address.GetAddressBytes();
        var gate = new SemaphoreSlim(24);
        var found = new List<LanDeviceEntry>();
        var sync = new object();
        var tasks = Enumerable.Range(1, 254).Select(async last =>
        {
            await gate.WaitAsync(token);
            try
            {
                var candidate = new IPAddress(new byte[] { bytes[0], bytes[1], bytes[2], (byte)last });
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(candidate, 450).WaitAsync(token);
                if (reply.Status != IPStatus.Success) return;
                var ports = new[] { 22, 53, 80, 443, 445, 3389, 8080 };
                var open = new List<int>();
                foreach (var port in ports)
                {
                    using var client = new TcpClient();
                    try { await client.ConnectAsync(candidate, port).WaitAsync(TimeSpan.FromMilliseconds(220), token); open.Add(port); }
                    catch { }
                }
                string host;
                try { host = (await Dns.GetHostEntryAsync(candidate).WaitAsync(token)).HostName; } catch { host = "—"; }
                lock (sync) found.Add(new LanDeviceEntry(candidate.ToString(), host, open.Count == 0 ? "未发现常用服务" : string.Join("、", open)));
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return found.OrderBy(item => IPAddress.Parse(item.Address).GetAddressBytes()[3]).ToArray();
    }

    private static string BuildConnectionExplanation(string routes, string firewall)
    {
        var defaultRoute = routes.Split('\n').FirstOrDefault(line => line.TrimStart().StartsWith("0.0.0.0", StringComparison.Ordinal));
        var firewallEnabled = firewall.Contains("ON", StringComparison.OrdinalIgnoreCase) || firewall.Contains("启用", StringComparison.OrdinalIgnoreCase);
        var proxy = NetworkWorkbenchService.GetCurrentUserProxy();
        return $"连接解释链\r\n1. 应用 → {(proxy.Enabled ? $"用户代理 {proxy.Server}" : "直接连接 / 系统自动代理")}\r\n" +
               $"2. 默认路由 → {(string.IsNullOrWhiteSpace(defaultRoute) ? "未从路由表识别" : defaultRoute.Trim())}\r\n" +
               $"3. Windows 防火墙 → {(firewallEnabled ? "配置文件已启用" : "未识别到启用状态")}\r\n" +
               "4. 物理网卡 → 网关 → 目标地址\r\n\r\n说明：该链路为配置解释，不代表数据包正文捕获。";
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31);
    }

    private static async Task<string> RunAsync(string file, string arguments, CancellationToken token)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
        }) ?? throw new InvalidOperationException($"无法启动 {file}");
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(token);
        var text = await output;
        return string.IsNullOrWhiteSpace(text) ? await error : text;
    }
}

internal sealed record NetworkDeepSnapshot(string Routes, string FirewallProfiles, string WifiInterface, string NearbyWifi, string Explanation);
internal sealed record LanDeviceEntry(string Address, string HostName, string Services)
{
    public string Display => $"{Address,-16}  {HostName,-32}  {Services}";
}
