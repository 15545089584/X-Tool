using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace ScreenshotApp.SmartHome;

public sealed record SmartAreaFilterItem(string Id, string Name, int Count, PackIconMaterialKind IconKind);

public partial class SmartHomeView : UserControl
{
    private readonly SmartHomeService _service = SmartHomeService.Instance;
    private readonly Dictionary<string, SmartDeviceViewModel> _deviceById = new(StringComparer.Ordinal);
    private bool _initialized;
    private string _selectedAreaId = "__online";

    public SmartHomeView()
    {
        InitializeComponent();
        DataContext = this;
        RunningBadgesList.ItemsSource = _runningBadges;
        ((System.Collections.Specialized.INotifyCollectionChanged)_runningBadges).CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(ApplyFanSpinAnimations), System.Windows.Threading.DispatcherPriority.Loaded);
        AreaFilters.Add(new SmartAreaFilterItem("__online", "在线", 0, PackIconMaterialKind.WifiCheck));
        _service.ConnectionStateChanged += Service_ConnectionStateChanged;
        _service.SnapshotChanged += Service_SnapshotChanged;
        Loaded += SmartHomeView_Loaded;
    }

    /// <summary>首页头部“运行中设备”徽标（仅风扇/灯泡/空调三类运行时显示）。</summary>
    public sealed record RunningDeviceBadge(PackIconMaterialKind Icon, string DeviceName, bool Spin);

    private readonly System.Collections.ObjectModel.ObservableCollection<RunningDeviceBadge> _runningBadges = [];

    public IReadOnlyList<RunningDeviceBadge> RunningBadges => _runningBadges;

    public ObservableCollection<SmartAreaFilterItem> AreaFilters { get; } = [];

    public ObservableCollection<SmartDeviceViewModel> VisibleDevices { get; } = [];

    /// <summary>供独立回归预览窗口注入脱敏设备数据，不连接真实 Home Assistant。</summary>
    internal void ShowSnapshotForValidation(SmartHomeSnapshot snapshot)
    {
        _initialized = true;
        ShowDashboard();
        ConnectionStatusDot.Fill = new SolidColorBrush(Color.FromRgb(43, 174, 131));
        ConnectionStatusText.Text = "Home Assistant 已连接 · 预览数据";
        ApplySnapshot(snapshot, false);
    }

    /// <summary>供回归预览核对首次连接引导，不读取本机真实连接配置。</summary>
    internal void ShowSetupForValidation()
    {
        _initialized = true;
        DashboardPanel.Visibility = Visibility.Collapsed;
        ConnectionSettingsOverlay.Visibility = Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Visible;
    }

    private async void SmartHomeView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        var settings = SmartHomeSettingsStore.Load();
        if (!string.IsNullOrWhiteSpace(settings.ServerUrl))
        {
            SetupAddressTextBox.Text = settings.ServerUrl;
            SettingsAddressTextBox.Text = settings.ServerUrl;
        }

        try
        {
            await _service.InitializeAsync();
            if (_service.CurrentSnapshot is not null)
            {
                ShowDashboard();
                ApplySnapshot(_service.CurrentSnapshot, _service.ConnectionState != SmartHomeConnectionState.Connected);
            }
            else if (_service.HasConfiguration)
            {
                ShowDashboard();
            }
        }
        catch (Exception exception)
        {
            SetupMessageText.Text = FriendlyError(exception);
        }
    }

    private async void SetupConnect_Click(object sender, RoutedEventArgs e)
    {
        await ConnectAsync(SetupAddressTextBox.Text, SetupTokenPasswordBox.Password, SetupMessageText, SetupConnectButton);
    }

    private async void SettingsConnect_Click(object sender, RoutedEventArgs e)
    {
        var connected = await ConnectAsync(
            SettingsAddressTextBox.Text,
            SettingsTokenPasswordBox.Password,
            SettingsMessageText,
            SettingsConnectButton);
        if (connected) ConnectionSettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private async Task<bool> ConnectAsync(string address, string token, TextBlock message, Button button)
    {
        message.Foreground = new SolidColorBrush(Color.FromRgb(79, 118, 103));
        message.Text = "正在验证连接并读取设备…";
        button.IsEnabled = false;
        try
        {
            await _service.ConnectAsync(address, token, persist: true);
            SetupTokenPasswordBox.Clear();
            SettingsTokenPasswordBox.Clear();
            SettingsAddressTextBox.Text = _service.Settings.ServerUrl;
            message.Text = "连接成功，设备状态已经同步。";
            ShowDashboard();
            if (_service.CurrentSnapshot is not null) ApplySnapshot(_service.CurrentSnapshot, false);
            return true;
        }
        catch (Exception exception)
        {
            message.Foreground = new SolidColorBrush(Color.FromRgb(192, 93, 105));
            message.Text = FriendlyError(exception);
            return false;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void Service_ConnectionStateChanged(SmartHomeConnectionState state, string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ConnectionStatusText.Text = message;
            ConnectionStatusDot.Fill = new SolidColorBrush(state switch
            {
                SmartHomeConnectionState.Connected => Color.FromRgb(43, 174, 131),
                SmartHomeConnectionState.Connecting or SmartHomeConnectionState.Reconnecting => Color.FromRgb(230, 166, 42),
                SmartHomeConnectionState.AuthenticationFailed or SmartHomeConnectionState.ServerUnavailable => Color.FromRgb(213, 96, 112),
                _ => Color.FromRgb(176, 186, 196)
            });
            RefreshButton.IsEnabled = state is not SmartHomeConnectionState.Connecting and not SmartHomeConnectionState.Reconnecting;
            if (_service.CurrentSnapshot is not null || _service.HasConfiguration) ShowDashboard();
        }));
    }

    private void Service_SnapshotChanged(SmartHomeSnapshot snapshot, bool fromCache)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ShowDashboard();
            ApplySnapshot(snapshot, fromCache);
        }));
    }

    private void ApplySnapshot(SmartHomeSnapshot snapshot, bool fromCache)
    {
        var currentIds = snapshot.Devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _deviceById.Keys.Where(id => !currentIds.Contains(id)).ToArray())
        {
            _deviceById.Remove(removed);
        }

        foreach (var device in snapshot.Devices)
        {
            if (_deviceById.TryGetValue(device.Id, out var existing)) existing.Update(device);
            else _deviceById[device.Id] = new SmartDeviceViewModel(device);
        }

        var selected = _selectedAreaId;
        AreaFilters.Clear();
        AreaFilters.Add(new SmartAreaFilterItem("__online", "在线", snapshot.Devices.Count(device => device.IsAvailable), PackIconMaterialKind.WifiCheck));
        AreaFilters.Add(new SmartAreaFilterItem("__all", "全屋", snapshot.Devices.Count, PackIconMaterialKind.HomeOutline));
        foreach (var area in snapshot.Areas)
        {
            var count = area.Id == "__unassigned"
                ? snapshot.Devices.Count(device => device.AreaId is null)
                : snapshot.Devices.Count(device => device.AreaId == area.Id);
            if (count == 0) continue;
            var displayName = area.Id == "__unassigned" ? "其他" : area.Name;
            AreaFilters.Add(new SmartAreaFilterItem(area.Id, displayName, count, ResolveRoomIconKind(displayName)));
        }
        var selectedIndex = Math.Max(0, AreaFilters.ToList().FindIndex(item => item.Id == selected));
        AreaFilterList.SelectedIndex = selectedIndex;
        _selectedAreaId = AreaFilters[selectedIndex].Id;
        UpdateWeather(snapshot);
        UpdateRunningBadges(snapshot);
        ApplyFilter();

        LastUpdatedText.Text = fromCache
            ? $"上次状态 · {snapshot.UpdatedAt.LocalDateTime:MM-dd HH:mm}"
            : $"更新于 {snapshot.UpdatedAt.LocalDateTime:HH:mm:ss}";
    }

    private void UpdateWeather(SmartHomeSnapshot snapshot)
    {
        var weather = snapshot.Weather;
        if (weather is null || weather.Temperature is null)
        {
            WeatherPill.Visibility = Visibility.Collapsed;
            return;
        }

        var (text, icon) = DescribeWeather(weather.Condition);
        WeatherPill.Visibility = Visibility.Visible;
        WeatherIcon.Kind = icon;
        WeatherText.Text = $"{weather.Temperature:0.#}°C {text}";
        WeatherLocationText.Text = weather.Location;
        WeatherLocationText.Visibility = string.IsNullOrWhiteSpace(weather.Location) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static (string Text, PackIconMaterialKind Icon) DescribeWeather(string condition) => condition.ToLowerInvariant() switch
    {
        "sunny" => ("晴", PackIconMaterialKind.WhiteBalanceSunny),
        "clear" or "clear-night" => ("晴", PackIconMaterialKind.WeatherNight),
        "partlycloudy" => ("多云", PackIconMaterialKind.WeatherPartlyCloudy),
        "cloudy" => ("阴", PackIconMaterialKind.WeatherCloudy),
        "rainy" => ("雨", PackIconMaterialKind.WeatherRainy),
        "pouring" => ("大雨", PackIconMaterialKind.WeatherPouring),
        "lightning" or "lightning-rainy" => ("雷雨", PackIconMaterialKind.WeatherLightning),
        "snowy" or "snowy-heavy" => ("雪", PackIconMaterialKind.WeatherSnowy),
        "hail" => ("冰雹", PackIconMaterialKind.WeatherHail),
        "windy" or "windy-variant" => ("大风", PackIconMaterialKind.WeatherWindy),
        "fog" => ("雾", PackIconMaterialKind.WeatherFog),
        _ => (condition, PackIconMaterialKind.WeatherPartlyCloudy)
    };

    private void UpdateRunningBadges(SmartHomeSnapshot snapshot)
    {
        _runningBadges.Clear();
        foreach (var device in snapshot.Devices.Select(item => _deviceById.GetValueOrDefault(item.Id)))
        {
            if (device is not { IsWorking: true }) continue;
            // 摄像机等设备的指示灯（light 实体）常亮，不能把它们当成“运行中的灯”展示。
            if (device.IsCameraLike) continue;
            var domain = device.Domain;
            if (domain is not ("fan" or "light" or "climate")) continue;
            _runningBadges.Add(new RunningDeviceBadge(
                domain == "fan" ? PackIconMaterialKind.Fan :
                domain == "light" ? PackIconMaterialKind.LightbulbOn :
                PackIconMaterialKind.AirConditioner,
                device.Name,
                domain == "fan"));
        }

        RunningBadgesHost.Visibility = _runningBadges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>徽标生成后由代码后置挂接旋转动画：数据模板内的变换会被自动冻结，无法用 XAML 动画驱动。</summary>
    private void ApplyFanSpinAnimations()
    {
        for (var index = 0; index < RunningBadgesList.Items.Count; index++)
        {
            if (RunningBadgesList.ItemContainerGenerator.ContainerFromIndex(index) is not ContentPresenter presenter) continue;
            foreach (var icon in FindVisualChildren<PackIconMaterial>(presenter))
            {
                if (icon.Tag is not true) continue;
                var transform = new RotateTransform();
                icon.RenderTransformOrigin = new Point(0.5, 0.5);
                icon.RenderTransform = transform;
                transform.BeginAnimation(RotateTransform.AngleProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6))
                    {
                        RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                    });
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(current, index);
                if (child is T typed) yield return typed;
                queue.Enqueue(child);
            }
        }
    }

    private void ApplyFilter()
    {
        var filtered = _deviceById.Values
            .Where(device => _selectedAreaId switch
            {
                "__online" => device.IsAvailable,
                "__all" => true,
                _ =>
                             (_selectedAreaId == "__unassigned" ? device.AreaId is null :
                                 string.Equals(device.AreaId, _selectedAreaId, StringComparison.Ordinal))
            })
            .OrderByDescending(device => device.IsWorking)
            .ThenByDescending(device => device.IsAvailable)
            .ThenBy(device => device.Name, StringComparer.CurrentCulture)
            .ToArray();

        VisibleDevices.Clear();
        foreach (var device in filtered) VisibleDevices.Add(device);
        EmptyState.Visibility = VisibleDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateTitle.Text = _selectedAreaId switch
        {
            "__online" => "当前没有在线设备",
            "__all" => "当前没有可显示的设备",
            _ => "这个房间还没有设备"
        };
        EmptyStateHint.Text = "使用底部导航切换范围，或刷新设备状态";
    }

    private void AreaFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AreaFilterList.SelectedItem is SmartAreaFilterItem item)
        {
            _selectedAreaId = item.Id;
            ApplyFilter();
        }
    }

    private async void DeviceCard_CommandRequested(object? sender, SmartHomeControlRequest request) =>
        await ExecuteDeviceCommandAsync(request);

    private async void DeviceDetailDialog_CommandRequested(object? sender, SmartHomeControlRequest request) =>
        await ExecuteDeviceCommandAsync(request);

    /// <summary>拖动实时命令：不受 IsBusy 防抖限制，直接下发；失败时回滚乐观状态。</summary>
    private async void DeviceDetailDialog_LiveCommandRequested(object? sender, SmartHomeControlRequest request)
    {
        if (_deviceById.Values.FirstOrDefault(device => device.ContainsEntity(request.EntityId)) is not { } device) return;
        try
        {
            await _service.ExecuteAsync(request);
            _ = RollbackIfUnconfirmedAsync(device);
        }
        catch (Exception exception)
        {
            device.Rollback(FriendlyError(exception));
        }
    }

    private async void DeviceDetailDialog_RenameRequested(object? sender, string newName)
    {
        if (DeviceDetailDialog.Device is not { } device) return;
        try
        {
            await _service.RenameDeviceAsync(device.SourceDevice.Id, newName);
            await _service.RefreshAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"设备重命名失败：{exception.Message}", "X-Tool 智能家居",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DeviceDetailDialog_InsightsMonthNavigate(object? sender, int monthOffset, int yearOffset)
    {
        if (DeviceDetailDialog.Device is not { IsClimate: true } device) return;
        device.BeginInsightsLoad();
        var insights = await _service.GetInsightsAsync(device.SourceDevice, monthOffset, yearOffset);
        if (ReferenceEquals(DeviceDetailDialog.Device, device)) device.ApplyInsights(insights);
    }

    private async Task ExecuteDeviceCommandAsync(SmartHomeControlRequest request)
    {
        if (_deviceById.Values.FirstOrDefault(device => device.ContainsEntity(request.EntityId)) is not { } device || device.IsBusy)
        {
            return;
        }

        device.ApplyOptimistic(request);
        try
        {
            await _service.ExecuteAsync(request);
            _ = RollbackIfUnconfirmedAsync(device);
        }
        catch (Exception exception)
        {
            device.Rollback(FriendlyError(exception));
        }
    }

    private async void DeviceCard_DetailRequested(object? sender, SmartDeviceViewModel device)
    {
        DeviceDetailDialog.Device = device;
        DeviceDetailOverlay.Visibility = Visibility.Visible;
        DeviceDetailOverlay.Focus();
        if (!device.IsClimate) return;

        device.BeginInsightsLoad();
        var insights = await _service.GetInsightsAsync(device.SourceDevice);
        if (ReferenceEquals(DeviceDetailDialog.Device, device)) device.ApplyInsights(insights);
    }

    private void DeviceDetailDialog_CloseRequested(object? sender, EventArgs e) => CloseDeviceDetail();

    private void DeviceDetailOverlay_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DeviceDetailOverlay)) CloseDeviceDetail();
    }

    private void DeviceDetailOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseDeviceDetail();
    }

    private void CloseDeviceDetail()
    {
        DeviceDetailOverlay.Visibility = Visibility.Collapsed;
        DeviceDetailDialog.Device = null;
    }

    private static async Task RollbackIfUnconfirmedAsync(SmartDeviceViewModel device)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (device.IsBusy) device.Rollback("未收到设备状态确认");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            await _service.RefreshAsync();
        }
        catch (Exception exception)
        {
            ConnectionStatusText.Text = FriendlyError(exception);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void OpenConnectionSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsAddressTextBox.Text = _service.Settings.ServerUrl;
        SettingsTokenPasswordBox.Clear();
        SettingsMessageText.Text = string.Empty;
        ConnectionSettingsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseConnectionSettings_Click(object sender, RoutedEventArgs e) =>
        ConnectionSettingsOverlay.Visibility = Visibility.Collapsed;

    private async void ForgetConnection_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "将删除 Home Assistant 地址、安全凭据和本机设备缓存。Home Assistant 与米家中的设备不会受到影响。",
            "删除智能家居连接",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            await _service.ForgetAsync();
            _deviceById.Clear();
            VisibleDevices.Clear();
            ConnectionSettingsOverlay.Visibility = Visibility.Collapsed;
            DashboardPanel.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
            SetupTokenPasswordBox.Clear();
            SetupMessageText.Text = "连接信息已删除。";
        }
        catch (Exception exception)
        {
            SettingsMessageText.Text = FriendlyError(exception);
        }
    }

    private void ShowDashboard()
    {
        SetupPanel.Visibility = Visibility.Collapsed;
        DashboardPanel.Visibility = Visibility.Visible;
    }

    private static string FriendlyError(Exception exception)
    {
        var root = exception.GetBaseException();
        return root switch
        {
            HomeAssistantAuthenticationException => "Access Token 无效或已经失效，请在 Home Assistant 中重新创建。",
            ArgumentException => root.Message,
            HttpRequestException => "无法访问 Home Assistant，请检查地址、网络和服务状态。",
            TaskCanceledException => "连接 Home Assistant 超时，请稍后重试。",
            _ => string.IsNullOrWhiteSpace(root.Message) ? "智能家居操作失败，请稍后重试。" : root.Message
        };
    }

    private static PackIconMaterialKind ResolveRoomIconKind(string roomName)
    {
        if (roomName.Contains("客厅", StringComparison.CurrentCultureIgnoreCase) ||
            roomName.Contains("起居", StringComparison.CurrentCultureIgnoreCase)) return PackIconMaterialKind.SofaOutline;
        if (roomName.Contains("卧室", StringComparison.CurrentCultureIgnoreCase) ||
            roomName.Contains("寝室", StringComparison.CurrentCultureIgnoreCase) ||
            roomName.Contains("床", StringComparison.CurrentCultureIgnoreCase)) return PackIconMaterialKind.BedKingOutline;
        if (roomName.Contains("厨房", StringComparison.CurrentCultureIgnoreCase) ||
            roomName.Contains("餐厅", StringComparison.CurrentCultureIgnoreCase)) return PackIconMaterialKind.SilverwareForkKnife;
        if (roomName.Contains("洗手", StringComparison.CurrentCultureIgnoreCase) ||
            roomName.Contains("卫生", StringComparison.CurrentCultureIgnoreCase) ||
            roomName.Contains("浴", StringComparison.CurrentCultureIgnoreCase)) return PackIconMaterialKind.ShowerHead;
        if (roomName.Contains("阳台", StringComparison.CurrentCultureIgnoreCase)) return PackIconMaterialKind.Balcony;
        return PackIconMaterialKind.DoorOpen;
    }
}
