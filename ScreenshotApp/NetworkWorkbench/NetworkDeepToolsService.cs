using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

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
        var wifiEnvironment = BuildWifiEnvironment(wifiTask.Result, nearbyTask.Result, NetworkWorkbenchService.GetAdapters());
        return new NetworkDeepSnapshot(routeTask.Result, firewallTask.Result, wifiTask.Result, nearbyTask.Result,
            BuildConnectionExplanation(routeTask.Result, firewallTask.Result), wifiEnvironment);
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

    private static WifiEnvironmentSnapshot BuildWifiEnvironment(string interfaceText, string nearbyText, IReadOnlyList<NetworkAdapterEntry> adapters)
    {
        var values = ParseKeyValueLines(interfaceText);
        string Find(params string[] keys) => keys.Select(key => values.FirstOrDefault(item => item.Key.Contains(key, StringComparison.OrdinalIgnoreCase)).Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "—";
        var adapter = adapters.FirstOrDefault(item => item.IsPrimary && item.InterfaceType == NetworkInterfaceType.Wireless80211.ToString())
            ?? adapters.FirstOrDefault(item => item.IsUp && item.InterfaceType == NetworkInterfaceType.Wireless80211.ToString());
        var ssid = Find("SSID");
        var channel = Find("频道", "通道", "Channel");
        var band = GetBand(channel);
        var description = adapter?.Description ?? Find("描述", "Description");
        var manufacturer = string.IsNullOrWhiteSpace(description) || description == "—"
            ? "—"
            : description.Split(new[] { ' ', '(', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? description;
        var dhcp = adapter?.DhcpText ?? "—";
        var dnsAllocation = dhcp.Contains("自动", StringComparison.OrdinalIgnoreCase) ? "自动 (DHCP)" : "手动 / 未识别";
        var properties = new List<WifiPropertyRow>
        {
            new("IP 分配", dhcp), new("DNS 服务器分配", dnsAllocation), new("SSID", ssid),
            new("协议", Find("无线电类型", "Radio type")), new("安全类型", Find("身份验证", "Authentication")),
            new("制造商", manufacturer), new("描述", description), new("网络频带", band), new("网络信道", channel),
            new("链路速度（接收 / 传输）", $"{Find("接收速率", "Receive rate")} / {Find("传输速率", "Transmit rate")}"),
            new("IPv6 地址", adapter?.IPv6Address ?? "—"), new("IPv4 地址", adapter?.IPv4Address ?? "—"),
            new("IPv4 DNS 服务器", adapter?.DnsServers ?? "—"), new("物理地址 (MAC)", adapter?.MacAddress ?? Find("物理地址", "Physical address")),
            new("接入点 BSSID", Find("BSSID")), new("信号强度", Find("信号", "Signal"))
        };
        return new WifiEnvironmentSnapshot(properties, ParseNearbyNetworks(nearbyText, ssid));
    }

    private static Dictionary<string, string> ParseKeyValueLines(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^\s*(?<key>[^:：]+)\s*[:：]\s*(?<value>.*?)\s*$");
            if (match.Success) values[match.Groups["key"].Value.Trim()] = match.Groups["value"].Value.Trim();
        }
        return values;
    }

    private static IReadOnlyList<WifiNetworkEntry> ParseNearbyNetworks(string text, string currentSsid)
    {
        var networks = new Dictionary<string, NearbyWifiBuilder>(StringComparer.OrdinalIgnoreCase);
        NearbyWifiBuilder? current = null;
        foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var ssid = Regex.Match(line, @"^\s*SSID\s+\d+\s*[:：]\s*(?<value>.*)$", RegexOptions.IgnoreCase);
            if (ssid.Success)
            {
                var name = string.IsNullOrWhiteSpace(ssid.Groups["value"].Value) ? "隐藏网络" : ssid.Groups["value"].Value.Trim();
                if (!networks.TryGetValue(name, out current)) networks[name] = current = new NearbyWifiBuilder(name);
                continue;
            }
            if (current is null) continue;
            var field = Regex.Match(line, @"^\s*(?<key>[^:：]+)\s*[:：]\s*(?<value>.*?)\s*$");
            if (!field.Success) continue;
            var key = field.Groups["key"].Value.Trim();
            var value = field.Groups["value"].Value.Trim();
            if (key.Contains("信号", StringComparison.OrdinalIgnoreCase) || key.Contains("Signal", StringComparison.OrdinalIgnoreCase)) current.Signal = Math.Max(current.Signal, ParsePercent(value));
            else if (key.Contains("身份验证", StringComparison.OrdinalIgnoreCase) || key.Contains("Authentication", StringComparison.OrdinalIgnoreCase)) current.Security = value;
            else if (key.Contains("无线电类型", StringComparison.OrdinalIgnoreCase) || key.Contains("Radio type", StringComparison.OrdinalIgnoreCase)) current.RadioType = value;
            else if (key.Contains("频道", StringComparison.OrdinalIgnoreCase) || key.Contains("通道", StringComparison.OrdinalIgnoreCase) || key.Contains("Channel", StringComparison.OrdinalIgnoreCase)) current.Channel = value;
        }
        return networks.Values.Select(item => new WifiNetworkEntry(item.Ssid, item.Signal, item.Security, item.RadioType, item.Channel,
                string.Equals(item.Ssid, currentSsid, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.IsConnected).ThenByDescending(item => item.SignalPercent).ThenBy(item => item.Ssid).ToArray();
    }

    private static int ParsePercent(string value)
    {
        var match = Regex.Match(value, @"\d+");
        return match.Success && int.TryParse(match.Value, out var number) ? Math.Clamp(number, 0, 100) : 0;
    }

    private static string GetBand(string channel)
    {
        var match = Regex.Match(channel, @"\d+");
        if (!match.Success || !int.TryParse(match.Value, out var value)) return "—";
        return value <= 14 ? "2.4 GHz" : value <= 196 ? "5 GHz" : "6 GHz";
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

internal sealed record NetworkDeepSnapshot(string Routes, string FirewallProfiles, string WifiInterface, string NearbyWifi, string Explanation, WifiEnvironmentSnapshot WifiEnvironment);
internal sealed record WifiEnvironmentSnapshot(IReadOnlyList<WifiPropertyRow> Properties, IReadOnlyList<WifiNetworkEntry> Networks);
internal sealed record WifiPropertyRow(string Label, string Value);
internal sealed record WifiNetworkEntry(string Ssid, int SignalPercent, string Security, string RadioType, string Channel, bool IsConnected)
{
    public string SignalText => SignalPercent > 0 ? $"{SignalPercent}%" : "信号未知";
    public string SignalColor => SignalPercent >= 70 ? "#61C995" : SignalPercent >= 45 ? "#F0B15A" : "#EF7E83";
    public string MetaText => string.Join(" · ", new[] { Security, RadioType, string.IsNullOrWhiteSpace(Channel) || Channel == "—" ? string.Empty : $"信道 {Channel}" }.Where(value => !string.IsNullOrWhiteSpace(value) && value != "—"));
}
internal sealed class NearbyWifiBuilder
{
    public NearbyWifiBuilder(string ssid) => Ssid = ssid;
    public string Ssid { get; }
    public int Signal { get; set; }
    public string Security { get; set; } = "—";
    public string RadioType { get; set; } = "—";
    public string Channel { get; set; } = "—";
}
internal sealed record LanDeviceEntry(string Address, string HostName, string Services)
{
    public string Display => $"{Address,-16}  {HostName,-32}  {Services}";
}
