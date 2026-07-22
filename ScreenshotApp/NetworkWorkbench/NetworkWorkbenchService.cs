using Microsoft.Win32;
using ScreenshotApp.SystemTools;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotApp.NetworkWorkbench;

/// <summary>网络工作台服务：读取网络状态、执行可取消诊断，并安全管理当前用户代理。</summary>
public static class NetworkWorkbenchService
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static readonly string ProfilesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "network-profiles.json");
    private static readonly Regex KeyValueLine = new(@"^\s*(?<key>[^:：]+)\s*[:：]\s*(?<value>.*?)\s*$", RegexOptions.Compiled);
    private static readonly object WifiCacheLock = new();
    private static WifiDetails _cachedWifi = new("—", "—", "—", "—", "—");
    private static DateTime _wifiCacheTime = DateTime.MinValue;

    static NetworkWorkbenchService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static NetworkOverviewSnapshot GetOverview()
    {
        var adapters = GetAdapters();
        var active = adapters.FirstOrDefault(item => item.IsPrimary) ?? adapters.FirstOrDefault(item => item.IsUp);
        var internetAvailable = TryGetInternetConnectivity() ?? NetworkInterface.GetIsNetworkAvailable();
        var wifi = GetWifiDetails();
        return new NetworkOverviewSnapshot(
            internetAvailable ? "互联网已连接" : active is null ? "未连接网络" : "仅本地网络",
            internetAvailable,
            active?.Name ?? "无活动网卡",
            active?.Description ?? "—",
            active?.IPv4Address ?? "—",
            active?.IPv6Address ?? "—",
            active?.Gateway ?? "—",
            active?.DnsServers ?? "—",
            active?.DhcpText ?? "—",
            active?.LinkSpeedBitsPerSecond ?? 0,
            adapters.Where(item => item.IsUp).Sum(item => item.BytesReceived),
            adapters.Where(item => item.IsUp).Sum(item => item.BytesSent),
            wifi.Ssid,
            wifi.Signal,
            wifi.Channel,
            wifi.ReceiveRate,
            wifi.TransmitRate,
            DateTime.Now);
    }

    public static IReadOnlyList<NetworkAdapterEntry> GetAdapters()
    {
        var candidates = new List<NetworkAdapterEntry>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var properties = adapter.GetIPProperties();
                var ipv4Properties = properties.GetIPv4Properties();
                var statistics = adapter.GetIPv4Statistics();
                var ipv4 = properties.UnicastAddresses.FirstOrDefault(item => item.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "—";
                var ipv6 = properties.UnicastAddresses.FirstOrDefault(item => item.Address.AddressFamily == AddressFamily.InterNetworkV6 && !item.Address.IsIPv6LinkLocal)?.Address.ToString() ?? "—";
                var gateway = properties.GatewayAddresses.FirstOrDefault(item => !IPAddress.Any.Equals(item.Address) && !IPAddress.IPv6Any.Equals(item.Address))?.Address.ToString() ?? "—";
                var dns = string.Join(" · ", properties.DnsAddresses.Select(item => item.ToString()));
                var isUp = adapter.OperationalStatus == OperationalStatus.Up;
                candidates.Add(new NetworkAdapterEntry(
                    adapter.Id,
                    adapter.Name,
                    adapter.Description,
                    adapter.NetworkInterfaceType.ToString(),
                    isUp,
                    false,
                    ipv4,
                    ipv6,
                    gateway,
                    string.IsNullOrWhiteSpace(dns) ? "—" : dns,
                    ipv4Properties?.IsDhcpEnabled == true ? "自动获取" : "手动配置",
                    adapter.Speed,
                    statistics.BytesReceived,
                    statistics.BytesSent,
                    adapter.GetPhysicalAddress().ToString(),
                    ipv4Properties?.Mtu ?? 0));
            }
            catch
            {
                candidates.Add(new NetworkAdapterEntry(adapter.Id, adapter.Name, adapter.Description, adapter.NetworkInterfaceType.ToString(), adapter.OperationalStatus == OperationalStatus.Up, false, "—", "—", "—", "—", "—", adapter.Speed, 0, 0, adapter.GetPhysicalAddress().ToString(), 0));
            }
        }

        var primary = candidates.FirstOrDefault(item => item.IsUp && item.Gateway != "—") ?? candidates.FirstOrDefault(item => item.IsUp);
        return candidates
            .Select(item => item with { IsPrimary = primary is not null && string.Equals(item.Id, primary.Id, StringComparison.OrdinalIgnoreCase) })
            .OrderByDescending(item => item.IsPrimary)
            .ThenByDescending(item => item.IsUp)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<PortEntry> GetConnections() => SystemToolsService.GetPorts();

    public static ProxySettingsSnapshot GetCurrentUserProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
        var enabled = Convert.ToInt32(key?.GetValue("ProxyEnable", 0), CultureInfo.InvariantCulture) != 0;
        return new ProxySettingsSnapshot(
            enabled,
            key?.GetValue("ProxyServer")?.ToString() ?? string.Empty,
            key?.GetValue("ProxyOverride")?.ToString() ?? string.Empty,
            key?.GetValue("AutoConfigURL")?.ToString() ?? string.Empty,
            Convert.ToInt32(key?.GetValue("AutoDetect", 0), CultureInfo.InvariantCulture) != 0);
    }

    public static bool TrySaveCurrentUserProxy(ProxySettingsSnapshot settings, out string? error)
    {
        try
        {
            if (settings.Enabled && string.IsNullOrWhiteSpace(settings.Server))
            {
                error = "启用手动代理时必须填写服务器地址和端口";
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true);
            key.SetValue("ProxyEnable", settings.Enabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", settings.Server.Trim(), RegistryValueKind.String);
            key.SetValue("ProxyOverride", settings.BypassList.Trim(), RegistryValueKind.String);
            key.SetValue("AutoDetect", settings.AutoDetect ? 1 : 0, RegistryValueKind.DWord);
            if (string.IsNullOrWhiteSpace(settings.PacUrl)) key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            else key.SetValue("AutoConfigURL", settings.PacUrl.Trim(), RegistryValueKind.String);
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static string GetWinHttpProxyText()
    {
        var result = RunProcessAsync("netsh.exe", "winhttp show proxy", CancellationToken.None).GetAwaiter().GetResult();
        return string.IsNullOrWhiteSpace(result) ? "未能读取 WinHTTP 代理" : result.Trim();
    }

    public static async Task<NetworkDiagnosticResult> RunDiagnosticAsync(string kind, string target, int port, CancellationToken cancellationToken)
    {
        target = target.Trim();
        if (string.IsNullOrWhiteSpace(target)) return new NetworkDiagnosticResult(DateTime.Now, kind, target, false, "请输入域名或 IP 地址", 0);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            string detail;
            switch (kind)
            {
                case "Ping":
                    using (var ping = new Ping())
                    {
                        var reply = await ping.SendPingAsync(target, 3000).WaitAsync(cancellationToken);
                        detail = reply.Status == IPStatus.Success ? $"{reply.Address} · {reply.RoundtripTime} ms · TTL {reply.Options?.Ttl}" : reply.Status.ToString();
                        return new NetworkDiagnosticResult(DateTime.Now, "Ping", target, reply.Status == IPStatus.Success, detail, stopwatch.ElapsedMilliseconds);
                    }
                case "DNS":
                    var addresses = await Dns.GetHostAddressesAsync(target).WaitAsync(cancellationToken);
                    detail = addresses.Length == 0 ? "没有返回地址" : string.Join(" · ", addresses.Select(item => item.ToString()));
                    return new NetworkDiagnosticResult(DateTime.Now, "DNS", target, addresses.Length != 0, detail, stopwatch.ElapsedMilliseconds);
                case "TCP":
                    using (var client = new TcpClient())
                    {
                        await client.ConnectAsync(target, port).WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);
                        return new NetworkDiagnosticResult(DateTime.Now, "TCP", $"{target}:{port}", true, "端口连接成功", stopwatch.ElapsedMilliseconds);
                    }
                case "HTTP":
                    var uriText = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? target : $"https://{target}";
                    using (var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = TimeSpan.FromSeconds(8) })
                    using (var response = await client.GetAsync(uriText, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        return new NetworkDiagnosticResult(DateTime.Now, "HTTP", uriText, response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", stopwatch.ElapsedMilliseconds);
                    }
                case "Trace":
                    detail = await RunProcessAsync("tracert.exe", $"-d -w 1000 \"{target}\"", cancellationToken);
                    return new NetworkDiagnosticResult(DateTime.Now, "路由追踪", target, !string.IsNullOrWhiteSpace(detail), detail.Trim(), stopwatch.ElapsedMilliseconds);
                default:
                    return new NetworkDiagnosticResult(DateTime.Now, kind, target, false, "不支持的诊断类型", stopwatch.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException)
        {
            return new NetworkDiagnosticResult(DateTime.Now, kind, target, false, "已取消", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            return new NetworkDiagnosticResult(DateTime.Now, kind, target, false, exception.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    public static IReadOnlyList<NetworkProfile> LoadProfiles()
    {
        try
        {
            if (!File.Exists(ProfilesPath)) return Array.Empty<NetworkProfile>();
            return JsonSerializer.Deserialize<List<NetworkProfile>>(File.ReadAllText(ProfilesPath, Encoding.UTF8)) ?? new List<NetworkProfile>();
        }
        catch { return Array.Empty<NetworkProfile>(); }
    }

    public static void SaveProfiles(IEnumerable<NetworkProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilesPath)!);
        File.WriteAllText(ProfilesPath, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static WifiDetails GetWifiDetails()
    {
        lock (WifiCacheLock)
        {
            if (DateTime.UtcNow - _wifiCacheTime < TimeSpan.FromSeconds(5)) return _cachedWifi;
        }

        try
        {
            var output = RunProcessAsync("netsh.exe", "wlan show interfaces", CancellationToken.None).GetAwaiter().GetResult();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = KeyValueLine.Match(line);
                if (match.Success) values[match.Groups["key"].Value.Trim()] = match.Groups["value"].Value.Trim();
            }
            string Find(params string[] keys) => keys.Select(key => values.FirstOrDefault(item => item.Key.Contains(key, StringComparison.OrdinalIgnoreCase)).Value).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "—";
            var result = new WifiDetails(Find("SSID"), Find("信号", "Signal"), Find("频道", "通道", "Channel"), Find("接收速率", "Receive rate"), Find("传输速率", "Transmit rate"));
            lock (WifiCacheLock)
            {
                _cachedWifi = result;
                _wifiCacheTime = DateTime.UtcNow;
            }
            return result;
        }
        catch
        {
            lock (WifiCacheLock) return _cachedWifi;
        }
    }

    private static bool? TryGetInternetConnectivity()
    {
        try
        {
            var type = Type.GetTypeFromProgID("NetworkListManager");
            if (type is null) return null;
            dynamic manager = Activator.CreateInstance(type)!;
            return manager.IsConnectedToInternet;
        }
        catch { return null; }
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
        });
        if (process is null) return string.Empty;
        using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int bufferLength);

    private sealed record WifiDetails(string Ssid, string Signal, string Channel, string ReceiveRate, string TransmitRate);
}

public sealed record NetworkOverviewSnapshot(string ConnectivityText, bool IsInternetAvailable, string ActiveAdapterName, string ActiveAdapterDescription, string IPv4Address, string IPv6Address, string Gateway, string DnsServers, string DhcpText, long LinkSpeedBitsPerSecond, long BytesReceived, long BytesSent, string WifiSsid, string WifiSignal, string WifiChannel, string WifiReceiveRate, string WifiTransmitRate, DateTime CapturedAt);

public sealed record NetworkAdapterEntry(string Id, string Name, string Description, string InterfaceType, bool IsUp, bool IsPrimary, string IPv4Address, string IPv6Address, string Gateway, string DnsServers, string DhcpText, long LinkSpeedBitsPerSecond, long BytesReceived, long BytesSent, string MacAddress, int Mtu)
{
    public string StatusText => IsUp ? "已连接" : "未连接";
    public string PrimaryText => IsPrimary ? "主用" : string.Empty;
    public string LinkSpeedText => FormatBits(LinkSpeedBitsPerSecond);
    public string MacText => string.IsNullOrWhiteSpace(MacAddress) ? "—" : string.Join("-", Enumerable.Range(0, MacAddress.Length / 2).Select(index => MacAddress.Substring(index * 2, 2)));
    private static string FormatBits(long bits) => bits >= 1_000_000_000 ? $"{bits / 1_000_000_000d:F1} Gbps" : bits >= 1_000_000 ? $"{bits / 1_000_000d:F0} Mbps" : $"{bits / 1_000d:F0} Kbps";
}

public sealed record ProxySettingsSnapshot(bool Enabled, string Server, string BypassList, string PacUrl, bool AutoDetect);
public sealed record NetworkDiagnosticResult(DateTime Time, string Kind, string Target, bool Succeeded, string Detail, long ElapsedMilliseconds)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string StatusText => Succeeded ? "成功" : "失败";
    public string ElapsedText => $"{ElapsedMilliseconds:N0} ms";
}
public sealed record NetworkProfile(string Name, ProxySettingsSnapshot Proxy, DateTime UpdatedAt)
{
    public string UpdatedAtText => UpdatedAt.ToString("yyyy-MM-dd HH:mm");
    public string Summary => Proxy.Enabled ? $"代理 {Proxy.Server}" : string.IsNullOrWhiteSpace(Proxy.PacUrl) ? "直连" : $"PAC {Proxy.PacUrl}";
}
