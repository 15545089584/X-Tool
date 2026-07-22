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
    private static readonly string ProfileRestorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "network-profile-restore.json");
    private static readonly Regex KeyValueLine = new(@"^\s*(?<key>[^:：]+)\s*[:：]\s*(?<value>.*?)\s*$", RegexOptions.Compiled);
    private static readonly object WifiCacheLock = new();
    private static WifiDetails _cachedWifi = WifiDetails.Empty;
    private static DateTime _wifiCacheTime = DateTime.MinValue;
    private static readonly object InterfaceMetricCacheLock = new();
    private static IReadOnlyDictionary<int, int> _cachedInterfaceMetrics = new Dictionary<int, int>();
    private static DateTime _interfaceMetricCacheTime = DateTime.MinValue;
    private static readonly SemaphoreSlim ConnectivityProbeGate = new(1, 1);
    private static ConnectivityProbeSnapshot _cachedConnectivityProbe = ConnectivityProbeSnapshot.Empty;
    private static DateTime _connectivityProbeCacheTime = DateTime.MinValue;
    private static int _connectivityProbeGeneration;
    private static readonly HttpClient DirectConnectivityClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(2)
    })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };
    private static readonly string[] VirtualAdapterKeywords =
    {
        "virtual", "vpn", "radmin", "hyper-v", "vmware", "vbox", "virtualbox",
        "wsl", "tap", "tun", "loopback", "npcap", "docker", "default switch"
    };

    static NetworkWorkbenchService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<NetworkOverviewSnapshot> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var adapters = GetAdapters();
        var active = adapters.FirstOrDefault(item => item.IsPrimary);
        var hasPhysicalConnection = active is not null;
        var hasVirtualConnection = adapters.Any(item => item.IsUp && item.IsVirtual);
        var probe = active is null
            ? ConnectivityProbeSnapshot.Empty with { CapturedAt = DateTime.Now }
            : await GetConnectivityProbeAsync(active, cancellationToken);
        var internetAvailable = probe.HttpSucceeded;
        var connectivityText = internetAvailable
            ? "互联网已连接"
            : hasPhysicalConnection
                ? probe.DnsSucceeded
                    ? "互联网受限"
                    : "网络已连接，无互联网"
                : hasVirtualConnection
                    ? "仅虚拟网络可用"
                    : "未连接网络";
        var wifi = active?.InterfaceType == NetworkInterfaceType.Wireless80211.ToString()
            ? GetWifiDetails()
            : WifiDetails.Empty;
        var proxy = GetCurrentUserProxy();
        return new NetworkOverviewSnapshot(
            connectivityText,
            internetAvailable,
            hasPhysicalConnection,
            active?.Id ?? string.Empty,
            active?.Name ?? "无活动物理网卡",
            active?.Description ?? "—",
            active?.InterfaceType ?? "—",
            active?.IPv4Address ?? "—",
            active?.IPv6Address ?? "—",
            active?.Gateway ?? "—",
            active?.DnsServers ?? "—",
            active?.DhcpText ?? "—",
            active?.LinkSpeedBitsPerSecond ?? 0,
            active?.BytesReceived ?? 0,
            active?.BytesSent ?? 0,
            wifi.Ssid,
            wifi.Signal,
            wifi.Channel,
            wifi.ReceiveRate,
            wifi.TransmitRate,
            wifi.Bssid,
            wifi.RadioType,
            wifi.Authentication,
            wifi.ChannelWidth,
            FormatProxySummary(proxy),
            active is null
                ? hasVirtualConnection ? "物理链路已断开，虚拟接口仍处于启用状态" : "未检测到已连接的物理网卡"
                : $"{active.Name} · 默认路由接口 {active.InterfaceIndex}",
            probe.Summary,
            probe.CapturedAt,
            probe.GatewayLatencyMs,
            probe.DnsLatencyMs,
            probe.HttpLatencyMs,
            adapters.Count(item => item.IsUp && !item.IsVirtual),
            DateTime.Now);
    }

    public static IReadOnlyList<NetworkAdapterEntry> GetAdapters()
    {
        var candidates = new List<NetworkAdapterEntry>();
        var interfaceMetrics = GetInterfaceMetrics();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var properties = adapter.GetIPProperties();
                var ipv4Properties = properties.GetIPv4Properties();
                var statistics = adapter.GetIPv4Statistics();
                var ipv4Unicast = properties.UnicastAddresses.FirstOrDefault(item => item.Address.AddressFamily == AddressFamily.InterNetwork);
                var ipv4 = ipv4Unicast?.Address.ToString() ?? "—";
                var ipv4PrefixLength = ipv4Unicast?.PrefixLength ?? 0;
                var ipv6 = properties.UnicastAddresses.FirstOrDefault(item => item.Address.AddressFamily == AddressFamily.InterNetworkV6 && !item.Address.IsIPv6LinkLocal)?.Address.ToString() ?? "—";
                var gateways = properties.GatewayAddresses
                    .Where(item => !IPAddress.Any.Equals(item.Address) && !IPAddress.IPv6Any.Equals(item.Address))
                    .Select(item => item.Address)
                    .ToArray();
                var gateway = gateways.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)?.ToString()
                              ?? gateways.FirstOrDefault()?.ToString()
                              ?? "—";
                var dns = string.Join(" · ", properties.DnsAddresses.Select(item => item.ToString()));
                var isUp = adapter.OperationalStatus == OperationalStatus.Up;
                var isVirtual = IsVirtualAdapter(adapter);
                candidates.Add(new NetworkAdapterEntry(
                    adapter.Id,
                    adapter.Name,
                    adapter.Description,
                    adapter.NetworkInterfaceType.ToString(),
                    isUp,
                    false,
                    ipv4,
                    ipv4PrefixLength,
                    ipv6,
                    gateway,
                    string.IsNullOrWhiteSpace(dns) ? "—" : dns,
                    ipv4Properties?.IsDhcpEnabled == true ? "自动获取" : "手动配置",
                    adapter.Speed,
                    statistics.BytesReceived,
                    statistics.BytesSent,
                    adapter.GetPhysicalAddress().ToString(),
                    ipv4Properties?.Mtu ?? 0,
                    isVirtual,
                    ipv4Properties?.Index ?? 0,
                    interfaceMetrics.GetValueOrDefault(ipv4Properties?.Index ?? 0)));
            }
            catch
            {
                candidates.Add(new NetworkAdapterEntry(adapter.Id, adapter.Name, adapter.Description, adapter.NetworkInterfaceType.ToString(), adapter.OperationalStatus == OperationalStatus.Up, false, "—", 0, "—", "—", "—", "—", adapter.Speed, 0, 0, adapter.GetPhysicalAddress().ToString(), 0, IsVirtualAdapter(adapter), 0, 0));
            }
        }

        var physicalCandidates = candidates
            .Where(item => item.IsUp && !item.IsVirtual && item.IPv4Address != "—")
            .ToArray();
        var bestInterfaceIndex = TryGetBestInterfaceIndex();
        var primary = physicalCandidates.FirstOrDefault(item => item.InterfaceIndex == bestInterfaceIndex)
                      ?? physicalCandidates
                          .OrderByDescending(item => item.Gateway != "—")
                          .ThenByDescending(item => item.LinkSpeedBitsPerSecond)
                          .FirstOrDefault();
        return candidates
            .Select(item => item with { IsPrimary = primary is not null && string.Equals(item.Id, primary.Id, StringComparison.OrdinalIgnoreCase) })
            .OrderByDescending(item => item.IsPrimary)
            .ThenByDescending(item => item.IsUp)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>手动刷新时丢弃 Wi-Fi 命令缓存，确保断开或切换后立即反映。</summary>
    public static void InvalidateNetworkCaches()
    {
        lock (WifiCacheLock)
        {
            _cachedWifi = WifiDetails.Empty;
            _wifiCacheTime = DateTime.MinValue;
            _cachedConnectivityProbe = ConnectivityProbeSnapshot.Empty;
            _connectivityProbeCacheTime = DateTime.MinValue;
            Interlocked.Increment(ref _connectivityProbeGeneration);
        }
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

    /// <summary>按默认路由、DNS、目标端口、HTTP 与当前代理分层执行完整诊断。</summary>
    public static async Task<IReadOnlyList<NetworkDiagnosticResult>> RunFullDiagnosticAsync(string target, int port, CancellationToken cancellationToken)
    {
        var results = new List<NetworkDiagnosticResult>();
        // 总览读取包含 netsh Wi-Fi 信息。完整诊断可能由 UI 线程直接触发，
        // 因此明确放到线程池，避免同步读取命令输出时等待 WPF 同步上下文。
        var overview = await Task.Run(() => GetOverviewAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        results.Add(new NetworkDiagnosticResult(DateTime.Now, "默认路由", overview.ActiveAdapterName,
            overview.HasPhysicalConnection && overview.Gateway != "—",
            $"网卡 {overview.ActiveAdapterName} · IPv4 {overview.IPv4Address} · 网关 {overview.Gateway} · {overview.ConnectivityProbeText}", 0));

        foreach (var kind in new[] { "DNS", "TCP", "HTTP" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunDiagnosticAsync(kind, target, port, cancellationToken).ConfigureAwait(false));
        }
        results.Add(await TestCurrentProxyAsync(cancellationToken).ConfigureAwait(false));
        return results;
    }

    public static async Task<NetworkDiagnosticResult> TestCurrentProxyAsync(CancellationToken cancellationToken)
    {
        var proxy = GetCurrentUserProxy();
        if (!proxy.Enabled)
        {
            return new NetworkDiagnosticResult(DateTime.Now, "代理", "当前用户代理", true,
                string.IsNullOrWhiteSpace(proxy.PacUrl) ? "手动代理已关闭" : $"使用 PAC：{proxy.PacUrl}", 0);
        }
        var endpoint = ParseProxyEndpoint(proxy.Server);
        if (endpoint is null)
        {
            return new NetworkDiagnosticResult(DateTime.Now, "代理", proxy.Server, false, "无法解析代理服务器地址和端口", 0);
        }
        return await RunDiagnosticAsync("TCP", endpoint.Value.Host, endpoint.Value.Port, cancellationToken) with
        {
            Kind = "代理",
            Target = proxy.Server
        };
    }

    public static NetworkProfile CaptureProfile(string name, ProxySettingsSnapshot proxy)
    {
        var adapter = GetAdapters().FirstOrDefault(item => item.IsPrimary);
        var snapshot = adapter is null ? null : new NetworkAdapterConfigurationSnapshot(
            adapter.Id, adapter.Name, adapter.IPv4Address, adapter.IPv4PrefixLength, adapter.Gateway, adapter.DnsServers, adapter.DhcpText, adapter.Mtu, adapter.InterfaceMetric);
        return new NetworkProfile(name, proxy, DateTime.Now, snapshot);
    }

    public static string BuildProfileDifference(NetworkProfile profile)
    {
        var currentProxy = GetCurrentUserProxy();
        var currentAdapter = GetAdapters().FirstOrDefault(item => item.IsPrimary);
        var changes = new List<string>();
        if (currentProxy != profile.Proxy) changes.Add($"当前用户代理：{FormatProxySummary(currentProxy)} → {FormatProxySummary(profile.Proxy)}");
        if (profile.Adapter is not null)
        {
            if (currentAdapter is null) changes.Add($"目标网卡：当前无主用物理网卡 → {profile.Adapter.Name}");
            else
            {
                if (!string.Equals(currentAdapter.Name, profile.Adapter.Name, StringComparison.OrdinalIgnoreCase)) changes.Add($"主用网卡：{currentAdapter.Name} → {profile.Adapter.Name}");
                if (!string.Equals(currentAdapter.DnsServers, profile.Adapter.DnsServers, StringComparison.OrdinalIgnoreCase)) changes.Add($"DNS：{currentAdapter.DnsServers} → {profile.Adapter.DnsServers}");
                if (!string.Equals(currentAdapter.DhcpText, profile.Adapter.DhcpText, StringComparison.OrdinalIgnoreCase)) changes.Add($"地址方式：{currentAdapter.DhcpText} → {profile.Adapter.DhcpText}");
                if (currentAdapter.Mtu != profile.Adapter.Mtu) changes.Add($"MTU：{currentAdapter.Mtu} → {profile.Adapter.Mtu}");
                if (currentAdapter.InterfaceMetric != profile.Adapter.InterfaceMetric) changes.Add($"接口跃点：{currentAdapter.InterfaceMetric} → {profile.Adapter.InterfaceMetric}");
            }
        }
        return changes.Count == 0 ? "当前配置已与该方案一致。" : string.Join(Environment.NewLine, changes);
    }

    public static string BuildDiagnosticReport(IEnumerable<NetworkDiagnosticResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("X-Tool 网络诊断报告");
        builder.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine(new string('=', 64));
        foreach (var result in results.OrderBy(item => item.Time))
        {
            builder.AppendLine($"[{result.Time:HH:mm:ss}] {result.Kind} | {result.Target} | {result.StatusText} | {result.ElapsedText}");
            builder.AppendLine(result.Detail);
            builder.AppendLine();
        }
        return builder.ToString();
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

    public static void SaveProfileRestorePoint(NetworkProfile profile, TimeSpan lifetime)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfileRestorePath)!);
        var envelope = new NetworkProfileRestorePoint(profile, DateTime.Now.Add(lifetime));
        File.WriteAllText(ProfileRestorePath, JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    public static NetworkProfile? LoadProfileRestorePoint()
    {
        try
        {
            if (!File.Exists(ProfileRestorePath)) return null;
            var envelope = JsonSerializer.Deserialize<NetworkProfileRestorePoint>(File.ReadAllText(ProfileRestorePath, Encoding.UTF8));
            if (envelope is null || envelope.ExpiresAt <= DateTime.Now) { File.Delete(ProfileRestorePath); return null; }
            return envelope.Profile;
        }
        catch { return null; }
    }

    public static IReadOnlyList<NetworkProfile> ImportProfiles(string path)
    {
        var profiles = JsonSerializer.Deserialize<List<NetworkProfile>>(File.ReadAllText(path, Encoding.UTF8)) ?? new();
        return profiles.Where(item => !string.IsNullOrWhiteSpace(item.Name)).ToArray();
    }

    public static void ExportProfiles(string path, IEnumerable<NetworkProfile> profiles)
        => File.WriteAllText(path, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

    private static (string Host, int Port)? ParseProxyEndpoint(string value)
    {
        var first = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        var equalIndex = first.IndexOf('=');
        if (equalIndex >= 0) first = first[(equalIndex + 1)..];
        if (!first.Contains("://", StringComparison.Ordinal)) first = $"http://{first}";
        return Uri.TryCreate(first, UriKind.Absolute, out var uri) && uri.Port > 0 ? (uri.Host, uri.Port) : null;
    }

    private static WifiDetails GetWifiDetails()
    {
        lock (WifiCacheLock)
        {
            if (DateTime.UtcNow - _wifiCacheTime < TimeSpan.FromSeconds(2)) return _cachedWifi;
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
            var result = new WifiDetails(Find("SSID"), Find("信号", "Signal"), Find("频道", "通道", "Channel"), Find("接收速率", "Receive rate"), Find("传输速率", "Transmit rate"),
                Find("BSSID"), Find("无线电类型", "Radio type"), Find("身份验证", "Authentication"), Find("信道宽度", "Channel width"));
            lock (WifiCacheLock)
            {
                _cachedWifi = result;
                _wifiCacheTime = DateTime.UtcNow;
            }
            return result;
        }
        catch
        {
            lock (WifiCacheLock)
            {
                _cachedWifi = WifiDetails.Empty;
                _wifiCacheTime = DateTime.UtcNow;
                return _cachedWifi;
            }
        }
    }

    private static bool IsVirtualAdapter(NetworkInterface adapter)
    {
        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return true;
        var identity = $"{adapter.Name} {adapter.Description}";
        return VirtualAdapterKeywords.Any(keyword => identity.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<int, int> GetInterfaceMetrics()
    {
        lock (InterfaceMetricCacheLock)
        {
            if (DateTime.UtcNow - _interfaceMetricCacheTime < TimeSpan.FromSeconds(5))
            {
                return _cachedInterfaceMetrics;
            }
        }

        try
        {
            var output = RunProcessAsync("netsh.exe", "interface ipv4 show interfaces", CancellationToken.None).GetAwaiter().GetResult();
            var result = new Dictionary<int, int>();
            foreach (var line in output.Split('\n'))
            {
                var match = Regex.Match(line, @"^\s*(?<index>\d+)\s+(?<metric>\d+)\s+\d+");
                if (match.Success && int.TryParse(match.Groups["index"].Value, out var index) && int.TryParse(match.Groups["metric"].Value, out var metric)) result[index] = metric;
            }
            lock (InterfaceMetricCacheLock)
            {
                _cachedInterfaceMetrics = result;
                _interfaceMetricCacheTime = DateTime.UtcNow;
                return _cachedInterfaceMetrics;
            }
        }
        catch
        {
            lock (InterfaceMetricCacheLock)
            {
                _interfaceMetricCacheTime = DateTime.UtcNow;
                return _cachedInterfaceMetrics;
            }
        }
    }

    private static string FormatProxySummary(ProxySettingsSnapshot proxy)
    {
        if (proxy.Enabled) return string.IsNullOrWhiteSpace(proxy.Server) ? "手动代理已启用" : $"手动代理 · {proxy.Server}";
        if (!string.IsNullOrWhiteSpace(proxy.PacUrl)) return "PAC 自动配置";
        return proxy.AutoDetect ? "自动检测" : "直连（代理已关闭）";
    }

    private static async Task<ConnectivityProbeSnapshot> GetConnectivityProbeAsync(NetworkAdapterEntry adapter, CancellationToken cancellationToken)
    {
        if (string.Equals(_cachedConnectivityProbe.AdapterId, adapter.Id, StringComparison.OrdinalIgnoreCase) &&
            DateTime.UtcNow - _connectivityProbeCacheTime < TimeSpan.FromSeconds(5))
        {
            return _cachedConnectivityProbe;
        }

        await ConnectivityProbeGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(_cachedConnectivityProbe.AdapterId, adapter.Id, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow - _connectivityProbeCacheTime < TimeSpan.FromSeconds(5))
            {
                return _cachedConnectivityProbe;
            }

            var probeGeneration = Volatile.Read(ref _connectivityProbeGeneration);
            var gatewayTask = ProbeGatewayAsync(adapter.Gateway, cancellationToken);
            var dnsTask = ProbeDnsAsync(cancellationToken);
            var httpTask = ProbeHttpAsync(cancellationToken);
            await Task.WhenAll(gatewayTask, dnsTask, httpTask);

            var gateway = await gatewayTask;
            var dns = await dnsTask;
            var http = await httpTask;

            var result = new ConnectivityProbeSnapshot(
                adapter.Id,
                gateway.Succeeded,
                dns.Succeeded,
                http.Succeeded,
                gateway.ElapsedMilliseconds,
                dns.ElapsedMilliseconds,
                http.ElapsedMilliseconds,
                DateTime.Now);
            if (probeGeneration == Volatile.Read(ref _connectivityProbeGeneration))
            {
                _cachedConnectivityProbe = result;
                _connectivityProbeCacheTime = DateTime.UtcNow;
            }
            return result;
        }
        finally
        {
            ConnectivityProbeGate.Release();
        }
    }

    private static async Task<ProbeResult> ProbeGatewayAsync(string gateway, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gateway) || gateway == "—") return new(false, -1);
        var watch = Stopwatch.StartNew();
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(gateway, 1200).WaitAsync(TimeSpan.FromMilliseconds(1500), cancellationToken);
            return new(reply.Status == IPStatus.Success, watch.ElapsedMilliseconds);
        }
        catch { return new(false, watch.ElapsedMilliseconds); }
    }

    private static async Task<ProbeResult> ProbeDnsAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync("www.msftconnecttest.com")
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            return new(addresses.Length != 0, watch.ElapsedMilliseconds);
        }
        catch { return new(false, watch.ElapsedMilliseconds); }
    }

    private static async Task<ProbeResult> ProbeHttpAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await DirectConnectivityClient.GetAsync(
                "http://www.msftconnecttest.com/connecttest.txt",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return new(response.IsSuccessStatusCode, watch.ElapsedMilliseconds);
        }
        catch { return new(false, watch.ElapsedMilliseconds); }
    }

    private static int TryGetBestInterfaceIndex()
    {
        try
        {
            var destination = BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0);
            return GetBestInterface(destination, out var interfaceIndex) == 0 ? (int)interfaceIndex : 0;
        }
        catch { return 0; }
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
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int bufferLength);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);

    private sealed record WifiDetails(string Ssid, string Signal, string Channel, string ReceiveRate, string TransmitRate,
        string Bssid, string RadioType, string Authentication, string ChannelWidth)
    {
        public static WifiDetails Empty { get; } = new("—", "—", "—", "—", "—", "—", "—", "—", "—");
    }

    private sealed record ConnectivityProbeSnapshot(string AdapterId, bool GatewaySucceeded, bool DnsSucceeded, bool HttpSucceeded,
        long GatewayLatencyMs, long DnsLatencyMs, long HttpLatencyMs, DateTime CapturedAt)
    {
        public static ConnectivityProbeSnapshot Empty { get; } = new(string.Empty, false, false, false, -1, -1, -1, DateTime.MinValue);
        public string Summary => string.IsNullOrWhiteSpace(AdapterId)
            ? "未执行联网探测"
            : $"网关 {(GatewaySucceeded ? $"可达 {GatewayLatencyMs} ms" : "未响应")} · DNS {(DnsSucceeded ? $"正常 {DnsLatencyMs} ms" : "失败")} · HTTP {(HttpSucceeded ? $"正常 {HttpLatencyMs} ms" : "失败")}";
    }

    private readonly record struct ProbeResult(bool Succeeded, long ElapsedMilliseconds);
}

public sealed record NetworkOverviewSnapshot(string ConnectivityText, bool IsInternetAvailable, bool HasPhysicalConnection, string ActiveAdapterId, string ActiveAdapterName, string ActiveAdapterDescription, string ActiveAdapterType, string IPv4Address, string IPv6Address, string Gateway, string DnsServers, string DhcpText, long LinkSpeedBitsPerSecond, long BytesReceived, long BytesSent, string WifiSsid, string WifiSignal, string WifiChannel, string WifiReceiveRate, string WifiTransmitRate, string WifiBssid, string WifiRadioType, string WifiAuthentication, string WifiChannelWidth, string ProxyText, string ConnectionDetail, string ConnectivityProbeText, DateTime ConnectivityProbeCapturedAt, long GatewayLatencyMs, long DnsLatencyMs, long HttpLatencyMs, int ActivePhysicalAdapterCount, DateTime CapturedAt);

public sealed record NetworkAdapterEntry(string Id, string Name, string Description, string InterfaceType, bool IsUp, bool IsPrimary, string IPv4Address, int IPv4PrefixLength, string IPv6Address, string Gateway, string DnsServers, string DhcpText, long LinkSpeedBitsPerSecond, long BytesReceived, long BytesSent, string MacAddress, int Mtu, bool IsVirtual, int InterfaceIndex, int InterfaceMetric)
{
    public string StatusText => IsUp ? "已连接" : "未连接";
    public string PrimaryText => IsPrimary ? "主用物理链路" : IsVirtual ? "虚拟接口" : string.Empty;
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
public sealed record NetworkAdapterConfigurationSnapshot(string Id, string Name, string IPv4Address, int IPv4PrefixLength, string Gateway, string DnsServers, string DhcpText, int Mtu, int InterfaceMetric);
public sealed record NetworkProfile(string Name, ProxySettingsSnapshot Proxy, DateTime UpdatedAt, NetworkAdapterConfigurationSnapshot? Adapter = null)
{
    public string UpdatedAtText => UpdatedAt.ToString("yyyy-MM-dd HH:mm");
    public string Summary
    {
        get
        {
            var proxyText = Proxy.Enabled ? $"代理 {Proxy.Server}" : string.IsNullOrWhiteSpace(Proxy.PacUrl) ? "直连" : $"PAC {Proxy.PacUrl}";
            return Adapter is null ? proxyText : $"{proxyText} · {Adapter.Name} · DNS {Adapter.DnsServers}";
        }
    }
}
internal sealed record NetworkProfileRestorePoint(NetworkProfile Profile, DateTime ExpiresAt);
