using System.Net.Http;
using System.IO;

namespace ScreenshotApp.SmartHome;

/// <summary>管理连接生命周期、缓存与自动重连；网络故障不能影响 X-Tool 其他模块。</summary>
public sealed class SmartHomeService
{
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly object _reconnectSync = new();
    private ISmartHomeProvider? _provider;
    private CancellationTokenSource? _reconnectSource;
    private Task? _reconnectTask;
    private bool _initialized;
    private bool _manualDisconnect;
    private SmartHomeConnectionSettings _settings = new();

    public static SmartHomeService Instance { get; } = new();

    public event Action<SmartHomeConnectionState, string>? ConnectionStateChanged;

    public event Action<SmartHomeSnapshot, bool>? SnapshotChanged;

    public SmartHomeConnectionState ConnectionState { get; private set; } = SmartHomeConnectionState.Disconnected;

    public SmartHomeSnapshot? CurrentSnapshot { get; private set; }

    public SmartHomeConnectionSettings Settings => _settings;

    public bool HasConfiguration => !string.IsNullOrWhiteSpace(_settings.ServerUrl) &&
                                    !string.IsNullOrWhiteSpace(SmartHomeCredentialStore.ReadToken());

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        _initialized = true;
        _settings = SmartHomeSettingsStore.Load();
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            SetConnectionState(SmartHomeConnectionState.Disconnected, "尚未连接 Home Assistant");
            return;
        }

        var cached = SmartHomeCacheStore.Load(_settings.ServerUrl);
        if (cached is not null)
        {
            CurrentSnapshot = cached;
            SnapshotChanged?.Invoke(cached, true);
        }

        if (!_settings.AutoConnect)
        {
            SetConnectionState(SmartHomeConnectionState.Disconnected, "自动连接已关闭");
            return;
        }

        var token = SmartHomeCredentialStore.ReadToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            SetConnectionState(SmartHomeConnectionState.AuthenticationFailed, "需要重新填写 Access Token");
            return;
        }

        try
        {
            await ConnectCoreAsync(_settings.ServerUrl, token, persist: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // ConnectCoreAsync 已发布可理解的连接状态；初始化不能阻塞主程序。
            if (ConnectionState == SmartHomeConnectionState.ServerUnavailable) StartReconnectLoop();
        }
    }

    public async Task ConnectAsync(
        string serverUrl,
        string? token,
        bool persist = true,
        CancellationToken cancellationToken = default)
    {
        var effectiveToken = string.IsNullOrWhiteSpace(token) ? SmartHomeCredentialStore.ReadToken() : token.Trim();
        if (string.IsNullOrWhiteSpace(effectiveToken))
        {
            throw new ArgumentException("请输入 Home Assistant Access Token。", nameof(token));
        }

        CancelReconnect();
        await ConnectCoreAsync(serverUrl, effectiveToken, persist, cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            throw new InvalidOperationException("尚未配置 Home Assistant。");
        }
        var token = SmartHomeCredentialStore.ReadToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Home Assistant Access Token 不存在。");
        }
        CancelReconnect();
        await ConnectCoreAsync(_settings.ServerUrl, token, persist: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExecuteAsync(SmartHomeControlRequest request, CancellationToken cancellationToken = default)
    {
        var provider = _provider;
        if (provider is null || ConnectionState != SmartHomeConnectionState.Connected)
        {
            throw new InvalidOperationException("Home Assistant 当前未连接，无法控制设备。");
        }
        await provider.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task ForgetAsync()
    {
        _manualDisconnect = true;
        CancelReconnect();
        await ReplaceProviderAsync(null).ConfigureAwait(false);
        SmartHomeCredentialStore.DeleteToken();
        SmartHomeSettingsStore.Delete();
        SmartHomeCacheStore.Delete();
        _settings = new SmartHomeConnectionSettings();
        CurrentSnapshot = null;
        SetConnectionState(SmartHomeConnectionState.Disconnected, "已删除 Home Assistant 连接");
    }

    private async Task ConnectCoreAsync(
        string serverUrl,
        string token,
        bool persist,
        CancellationToken cancellationToken)
    {
        var serverUri = SmartHomeSettingsStore.NormalizeServerUri(serverUrl);
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _manualDisconnect = false;
            SetConnectionState(SmartHomeConnectionState.Connecting, "正在连接 Home Assistant…");
            await ReplaceProviderAsync(null).ConfigureAwait(false);

            var provider = new HomeAssistantProvider(serverUri, token);
            provider.SnapshotChanged += Provider_SnapshotChanged;
            provider.Disconnected += Provider_Disconnected;
            try
            {
                await provider.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _provider = provider;
            }
            catch
            {
                provider.SnapshotChanged -= Provider_SnapshotChanged;
                provider.Disconnected -= Provider_Disconnected;
                await provider.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _settings = new SmartHomeConnectionSettings
            {
                ServerUrl = serverUri.ToString().TrimEnd('/'),
                AutoConnect = true
            };
            if (persist)
            {
                var previousToken = SmartHomeCredentialStore.ReadToken();
                try
                {
                    SmartHomeCredentialStore.SaveToken(token);
                    SmartHomeSettingsStore.Save(_settings);
                }
                catch
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(previousToken)) SmartHomeCredentialStore.DeleteToken();
                        else SmartHomeCredentialStore.SaveToken(previousToken);
                    }
                    catch
                    {
                        // 保留最初的持久化异常；后续再次连接时会要求用户重新确认凭据。
                    }
                    await ReplaceProviderAsync(null).ConfigureAwait(false);
                    throw;
                }
            }

            if (provider.CurrentSnapshot is not null)
            {
                CurrentSnapshot = provider.CurrentSnapshot;
                try
                {
                    await Task.Run(() => SmartHomeCacheStore.Save(_settings.ServerUrl, provider.CurrentSnapshot), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                    // 缓存失败不应破坏已经建立的实时控制连接。
                }
            }
            SetConnectionState(SmartHomeConnectionState.Connected, "Home Assistant 已连接");
        }
        catch (HomeAssistantAuthenticationException)
        {
            SetConnectionState(SmartHomeConnectionState.AuthenticationFailed, "Access Token 无效或已失效");
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetConnectionState(SmartHomeConnectionState.Disconnected, "连接已取消");
            throw;
        }
        catch (TaskCanceledException exception)
        {
            SetConnectionState(SmartHomeConnectionState.ServerUnavailable, "连接 Home Assistant 超时");
            throw new TimeoutException("连接 Home Assistant 超时。", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or IOException or InvalidOperationException)
        {
            SetConnectionState(SmartHomeConnectionState.ServerUnavailable, "无法连接 Home Assistant");
            throw;
        }
        catch
        {
            SetConnectionState(SmartHomeConnectionState.ServerUnavailable, "Home Assistant 连接初始化失败");
            throw;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private void Provider_SnapshotChanged(SmartHomeSnapshot snapshot)
    {
        CurrentSnapshot = snapshot;
        SnapshotChanged?.Invoke(snapshot, false);
        var serverUrl = _settings.ServerUrl;
        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            _ = Task.Run(() =>
            {
                try { SmartHomeCacheStore.Save(serverUrl, snapshot); } catch { }
            });
        }
    }

    private void Provider_Disconnected(Exception? exception)
    {
        if (_manualDisconnect) return;
        SetConnectionState(SmartHomeConnectionState.Reconnecting, "连接中断，正在重连…");
        StartReconnectLoop();
    }

    private void StartReconnectLoop()
    {
        lock (_reconnectSync)
        {
            if (_reconnectTask is { IsCompleted: false }) return;
            _reconnectSource = new CancellationTokenSource();
            _reconnectTask = ReconnectLoopAsync(_reconnectSource.Token);
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested && !_manualDisconnect)
        {
            var delay = ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)];
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                var token = SmartHomeCredentialStore.ReadToken();
                if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(_settings.ServerUrl)) return;
                await ConnectCoreAsync(_settings.ServerUrl, token, persist: false, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                attempt++;
                SetConnectionState(SmartHomeConnectionState.Reconnecting,
                    $"连接中断，{ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)].TotalSeconds:0} 秒后重试");
            }
        }
    }

    private void CancelReconnect()
    {
        lock (_reconnectSync)
        {
            _reconnectSource?.Cancel();
            _reconnectSource?.Dispose();
            _reconnectSource = null;
            _reconnectTask = null;
        }
    }

    private async Task ReplaceProviderAsync(ISmartHomeProvider? next)
    {
        var previous = Interlocked.Exchange(ref _provider, next);
        if (previous is null) return;
        previous.SnapshotChanged -= Provider_SnapshotChanged;
        previous.Disconnected -= Provider_Disconnected;
        await previous.DisposeAsync().ConfigureAwait(false);
    }

    private void SetConnectionState(SmartHomeConnectionState state, string message)
    {
        ConnectionState = state;
        ConnectionStateChanged?.Invoke(state, message);
    }
}
