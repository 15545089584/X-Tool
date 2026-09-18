using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ScreenshotApp.Collaboration;

/// <summary>独立通知 TLS 服务，不允许回退明文；监听端口独占，拒绝多个接收实例。</summary>
public sealed class PhoneNotificationHub : IDisposable
{
    private readonly string? _storageRoot;
    public PhoneNotificationHub(string? storageRoot = null) { _storageRoot = storageRoot; }
    public static PhoneNotificationHub Instance { get; } = new();
    public const int Port = 18122;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _slots = new(4);
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private X509Certificate2? _certificate;
    private string _secret = "";
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private FileStream? _owner;
    public PhoneNotificationState State { get; } = new();
    public bool Paused { get; set; }
    public bool AlertsEnabled { get; set; }
    public event Action<PhoneNotificationItem[]>? NotificationsArrived;
    public bool Running => _listener is not null;
    public bool Locked { get; set; }
    public string LastError { get; private set; } = "";
    public event Action? Changed;
    private string IdentityPath => Path.Combine(_storageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "NotificationBridge"), "identity.bin");
    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public void ResumeIfConfigured() { if (File.Exists(IdentityPath)) Start(); }
    private sealed record Identity(string Certificate, string Secret);

    public void Start(int port = Port)
    {
        lock (_gate)
        {
            if (Running) return;
            Directory.CreateDirectory(Path.GetDirectoryName(IdentityPath)!);
            try
            {
                _owner = new FileStream(IdentityPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (File.Exists(IdentityPath))
                {
                    var raw = ProtectedData.Unprotect(File.ReadAllBytes(IdentityPath), null, DataProtectionScope.CurrentUser);
                    var identity = JsonSerializer.Deserialize<Identity>(raw)!;
                    _certificate = new X509Certificate2(Convert.FromBase64String(identity.Certificate), (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
                    _secret = identity.Secret;
                }
                else
                {
                    using var rsa = RSA.Create(2048);
                    var request = new CertificateRequest("CN=XTool Notification Bridge", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                    using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(3));
                    // Windows Schannel 需要可访问的用户密钥容器；不安装到系统信任根。
                    _certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
                    _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    SaveIdentity();
                }
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Server.ExclusiveAddressUse = true;
                _listener.Start(4);
                _stop = new CancellationTokenSource();
                _ = AcceptAsync(_listener, _stop.Token);
                _ = ExpireAsync(_stop.Token);
            }
            catch { Dispose(); throw; }
        }
    }
    private async Task ExpireAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(token)) State.Expire(DateTime.UtcNow);
        }
        catch (OperationCanceledException) { }
    }
    private void SaveIdentity()
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new Identity(Convert.ToBase64String(_certificate!.Export(X509ContentType.Pfx)), _secret));
        var temporary = IdentityPath + ".tmp";
        File.WriteAllBytes(temporary, ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
        File.Move(temporary, IdentityPath, true);
    }
    public string PairingPayload(string host, string serverId)
    {
        lock (_gate)
        {
            Start();
            return JsonSerializer.Serialize(new { kind = "xtool-notifications-v1", host, port = BoundPort, serverId,
                fingerprint = Convert.ToHexString(SHA256.HashData(_certificate!.RawData)), secret = _secret }, _json);
        }
    }
    // 通知凭据仅放在扫码片段中，不进入旧 HTTP 配对请求、查询参数或访问日志。
    public string CollaborationPairingPayload(string host, int port, string pin, string serverId)
    {
        var fragment = Convert.ToBase64String(Encoding.UTF8.GetBytes(PairingPayload(host, serverId)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"http://{host}:{port}/pair?pin={Uri.EscapeDataString(pin)}#xtool-notify={fragment}";
    }
    public void Revoke()
    {
        lock (_gate) { _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); SaveIdentity(); State.Reset(); }
        Changed?.Invoke();
    }
    private async Task AcceptAsync(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                if (!_slots.Wait(0)) { client.Dispose(); continue; }
                _ = HandleAsync(client, token);
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch { /* 端口失效交由页面显示，禁止在后台形成无限重启循环。 */ }
    }
    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var tls = new SslStream(client.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, budget.Token);
                var header = new List<byte>(); var one = new byte[1];
                while (header.Count < 8192)
                {
                    if (await tls.ReadAsync(one, budget.Token) == 0) return;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                if (header.Count >= 8192 || lines[0] != "POST /api/v2/notifications/snapshot HTTP/1.1") { await Reply(tls, 400, budget.Token); return; }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
                {
                    var split = line.IndexOf(':');
                    if (split < 1 || !headers.TryAdd(line[..split], line[(split + 1)..].Trim())) { await Reply(tls, 400, budget.Token); return; }
                }
                string secret; lock (_gate) secret = _secret;
                var supplied = headers.GetValueOrDefault("Authorization") ?? "";
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes("Bearer " + secret))) { await Reply(tls, 401, budget.Token); return; }
                if (headers.ContainsKey("Transfer-Encoding") || !int.TryParse(headers.GetValueOrDefault("Content-Length"), out var length) || length < 2 || length > 512 * 1024) { await Reply(tls, 413, budget.Token); return; }
                var body = new byte[length]; await tls.ReadExactlyAsync(body, budget.Token);
                var snapshot = JsonSerializer.Deserialize<PhoneNotificationSnapshot>(body, _json) ?? throw new InvalidDataException();
                bool changed;
                PhoneNotificationItem[] fresh = [];
                lock (_gate)
                {
                    if (secret != _secret) throw new UnauthorizedAccessException();
                    if (Paused) { changed = false; }
                    else
                    {
                        var previous = State.Items;
                        var recentlyConnected = DateTime.UtcNow - State.LastReceivedUtc < TimeSpan.FromSeconds(60);
                        changed = State.Apply(snapshot);
                        if (changed && recentlyConnected && AlertsEnabled && !Locked)
                            fresh = State.Items.Where(i => !previous.Contains(i)).ToArray();
                    }
                }
                if (Paused) { await Reply(tls, 409, budget.Token); return; }
                await Reply(tls, 200, budget.Token);
                if (changed) Changed?.Invoke();
                if (fresh.Length > 0) NotificationsArrived?.Invoke(fresh);
            }
            catch (Exception exception) { LastError = exception.GetType().Name; /* 仅保留错误类型，不记录正文和凭据。 */ }
            finally { _slots.Release(); }
        }
    }
    private static Task Reply(Stream stream, int status, CancellationToken token)
    {
        var text = $"HTTP/1.1 {status} Result\r\nContent-Length: 2\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{{}}";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(text), token).AsTask();
    }
    public void Dispose()
    {
        _stop?.Cancel(); _listener?.Stop(); _listener = null;
        _owner?.Dispose(); _owner = null;
        // 正在收尾的握手仍可能引用证书；进程退出时由运行时释放。
        State.Reset();
    }
}
