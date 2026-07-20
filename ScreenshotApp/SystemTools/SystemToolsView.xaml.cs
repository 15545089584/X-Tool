using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScreenshotApp.SystemTools;

/// <summary>系统工具页面：所有高风险操作都保持在用户点击后的明确确认路径上。</summary>
public partial class SystemToolsView : UserControl
{
    private readonly ObservableCollection<PortEntry> _ports = new();
    private readonly ObservableCollection<ProcessListRow> _processes = new();
    private readonly ObservableCollection<ServiceEntry> _services = new();
    private readonly ObservableCollection<SystemRelationshipEntry> _relationships = new();
    private readonly ObservableCollection<EnvironmentVariableEntry> _environmentVariables = new();
    private readonly ObservableCollection<PathEntry> _pathEntries = new();
    private IReadOnlyList<PortEntry> _allPorts = Array.Empty<PortEntry>();
    private IReadOnlyList<ProcessEntry> _allProcesses = Array.Empty<ProcessEntry>();
    private IReadOnlyList<ServiceEntry> _allServices = Array.Empty<ServiceEntry>();
    private IReadOnlyList<SystemRelationshipEntry> _allRelationships = Array.Empty<SystemRelationshipEntry>();
    private IReadOnlyList<EnvironmentVariableEntry> _allEnvironmentVariables = Array.Empty<EnvironmentVariableEntry>();
    private bool _portsAscending = true;
    private string _portSortKey = "Port";
    private bool _portHeaderSortActive;
    private readonly DispatcherTimer _portAutoRefreshTimer;
    private bool _isRefreshingPorts;
    private bool _isRefreshingRelationships;
    private bool _processesAscending = true;
    private bool _servicesAscending = true;
    private string _processSortKey = "Name";
    private string _serviceSortKey = "Name";
    private bool _processHeaderSortActive;
    private bool _serviceHeaderSortActive;
    private readonly Dictionary<TextBlock, (string Key, string Title)> _processHeaders = new();
    private readonly Dictionary<TextBlock, (string Key, string Title)> _serviceHeaders = new();
    private readonly HashSet<string> _expandedProcessGroups = new(StringComparer.OrdinalIgnoreCase);

    public SystemToolsView()
    {
        InitializeComponent();
        PortsListBox.ItemsSource = _ports;
        ProcessesListBox.ItemsSource = _processes;
        ServicesListBox.ItemsSource = _services;
        RelationsBubbleListBox.ItemsSource = _relationships;
        EnvironmentListBox.ItemsSource = _environmentVariables;
        PathEntriesListBox.ItemsSource = _pathEntries;
        _portAutoRefreshTimer = new DispatcherTimer();
        _portAutoRefreshTimer.Tick += AutoRefreshPorts_Tick;
        EnvironmentScopeComboBox.SelectedIndex = 0;
        AutoRefreshIntervalComboBox.SelectedIndex = 1;
        UpdatePortAutoRefreshInterval();
        RelationshipBubbleFilterTextBox.HorizontalContentAlignment = HorizontalAlignment.Left;
        RelationshipBubbleFilterTextBox.TextAlignment = TextAlignment.Left;
        PortsListBox.ItemContainerGenerator.StatusChanged += (_, _) => ConfigurePortColumns();
        RelationsBubbleListBox.ItemContainerGenerator.StatusChanged += (_, _) => ConfigureRelationshipColumns();
        SetActiveTab("Ports");
        Loaded += async (_, _) => { ConfigureInteractiveHeaders(); ConfigurePortColumns(); await RefreshPortsAsync(); };
        Unloaded += (_, _) => _portAutoRefreshTimer.Stop();
    }

    private async Task RefreshPortsAsync()
    {
        if (_isRefreshingPorts) return;
        _isRefreshingPorts = true;
        PortsSummaryText.Text = "正在读取…";
        try { _allPorts = await Task.Run(SystemToolsService.GetPorts); ApplyPortFilter(); ConfigurePortColumns(); }
        catch (Exception exception) { PortsSummaryText.Text = $"读取失败：{exception.Message}"; }
        finally { _isRefreshingPorts = false; }
    }

    private async Task RefreshProcessesAsync()
    {
        ProcessesSummaryText.Text = "正在采样 CPU 与内存…";
        try { _allProcesses = await Task.Run(SystemToolsService.GetProcesses); ApplyProcessFilter(); }
        catch (Exception exception) { ProcessesSummaryText.Text = $"读取失败：{exception.Message}"; }
    }

    private async Task RefreshServicesAsync()
    {
        ServicesSummaryText.Text = "正在读取…";
        try { _allServices = await Task.Run(SystemToolsService.GetServices); ApplyServiceFilter(); }
        catch (Exception exception) { ServicesSummaryText.Text = $"读取失败：{exception.Message}"; }
    }

    /// <summary>通过同一次快照建立服务、进程与端口关系，避免页面间依赖搜索框或重复读取数据。</summary>
    private async Task RefreshRelationshipsAsync()
    {
        if (_isRefreshingRelationships) return;
        _isRefreshingRelationships = true;
        RelationsSummaryText.Text = "正在建立关联…";
        RelationsBubbleSummaryText.Text = "正在建立关联…";
        try
        {
            var snapshot = await Task.Run(SystemToolsService.GetRelationshipSnapshot);
            _allRelationships = snapshot.Processes
                .Select(process => new SystemRelationshipEntry(
                    process,
                    snapshot.Services.Where(service => service.ProcessId == process.ProcessId).ToArray(),
                    snapshot.Ports.Where(port => port.ProcessId == process.ProcessId).ToArray()))
                .OrderByDescending(item => item.HasNetworkActivity || item.HasServices)
                .ThenByDescending(item => item.MemoryBytes)
                .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ApplyRelationshipFilter();
        }
        catch (Exception exception)
        {
            RelationsSummaryText.Text = $"读取失败：{exception.Message}";
            RelationsBubbleSummaryText.Text = $"读取失败：{exception.Message}";
        }
        finally
        {
            _isRefreshingRelationships = false;
        }
    }

    private void RefreshEnvironment()
    {
        try { _allEnvironmentVariables = SystemToolsService.GetEnvironmentVariables(SelectedEnvironmentScope); ApplyEnvironmentFilter(); }
        catch (Exception exception) { EnvironmentSummaryText.Text = $"读取失败：{exception.Message}"; }
    }

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        var section = (sender as FrameworkElement)?.Tag?.ToString() ?? "Ports";
        PortsPanel.Visibility = section == "Ports" ? Visibility.Visible : Visibility.Collapsed;
        ProcessesPanel.Visibility = section == "Processes" ? Visibility.Visible : Visibility.Collapsed;
        ServicesPanel.Visibility = section == "Services" ? Visibility.Visible : Visibility.Collapsed;
        RelationsBubblePanel.Visibility = section == "Relations" ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentPanel.Visibility = section == "Environment" ? Visibility.Visible : Visibility.Collapsed;
        SetActiveTab(section);
        if (section == "Processes") _ = RefreshProcessesAsync();
        else if (section == "Services") _ = RefreshServicesAsync();
        else if (section == "Relations") _ = RefreshRelationshipsAsync();
        else if (section == "Environment") RefreshEnvironment();
    }

    private void SetActiveTab(string section)
    {
        foreach (var (button, name) in new[] { (PortsTabButton, "Ports"), (ProcessesTabButton, "Processes"), (ServicesTabButton, "Services"), (RelationsTabButton, "Relations"), (EnvironmentTabButton, "Environment") })
        {
            var active = name == section;
            button.Background = new SolidColorBrush(active ? Color.FromRgb(77, 124, 254) : Color.FromArgb(134, 255, 255, 255));
            button.BorderBrush = new SolidColorBrush(active ? Color.FromRgb(118, 160, 255) : Color.FromRgb(166, 209, 232));
            button.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(74, 105, 135));
        }
    }

    private async void RefreshPorts_Click(object sender, RoutedEventArgs e) => await RefreshPortsAsync();
    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e) => await RefreshProcessesAsync();
    private async void RefreshServices_Click(object sender, RoutedEventArgs e) => await RefreshServicesAsync();
    private async void RefreshRelationships_Click(object sender, RoutedEventArgs e) => await RefreshRelationshipsAsync();
    private void PortFilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyPortFilter();
    private void PortDisplayOption_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyPortFilter();
    }

    private void AutoRefreshCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var enabled = AutoRefreshCheckBox.IsChecked == true;
        if (!enabled)
        {
            _portAutoRefreshTimer.Stop();
            return;
        }

        UpdatePortAutoRefreshInterval();
        if (IsLoaded) _portAutoRefreshTimer.Start();
    }

    private void AutoRefreshIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdatePortAutoRefreshInterval();
        if (AutoRefreshCheckBox.IsChecked == true) _portAutoRefreshTimer.Start();
    }

    private void UpdatePortAutoRefreshInterval()
    {
        var seconds = int.TryParse(SelectedTag(AutoRefreshIntervalComboBox), out var value) ? value : 5;
        _portAutoRefreshTimer.Interval = TimeSpan.FromSeconds(seconds);
    }

    private async void AutoRefreshPorts_Tick(object? sender, EventArgs e)
    {
        if (AutoRefreshCheckBox.IsChecked != true) return;
        if (PortsPanel.Visibility == Visibility.Visible) await RefreshPortsAsync();
        else if (ProcessesPanel.Visibility == Visibility.Visible) await RefreshProcessesAsync();
        else if (ServicesPanel.Visibility == Visibility.Visible) await RefreshServicesAsync();
        else if (RelationsBubblePanel.Visibility == Visibility.Visible) await RefreshRelationshipsAsync();
    }
    private void ProcessFilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyProcessFilter();
    private void ProcessDisplayOption_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyProcessFilter();
    }
    private void ServiceFilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyServiceFilter();
    private void ServiceDisplayOption_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyServiceFilter();
    }
    private void RelationshipFilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyRelationshipFilter();
    private void RelationshipDisplayOption_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyRelationshipFilter();
    }
    private void RelationshipScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyRelationshipFilter();
    }

    private void RelationsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = RelationsListBox.SelectedItem as SystemRelationshipEntry;
        RelationDetailsPanel.DataContext = selected;
        RelationDetailsPanel.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        RelationEmptyState.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RelationsBubbleListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = RelationsBubbleListBox.SelectedItem as SystemRelationshipEntry;
        RelationDetailsPopup.DataContext = selected;
        if (selected is not null && RelationsBubbleListBox.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item)
        {
            RelationDetailsPopup.PlacementTarget = item;
            RelationDetailsPopup.Placement = item.TranslatePoint(new Point(0, 0), RelationsBubbleListBox).Y > RelationsBubbleListBox.ActualHeight / 2
                ? PlacementMode.Top
                : PlacementMode.Bottom;
            RelationDetailsPopup.VerticalOffset = 8;
        }

        RelationDetailsPopup.IsOpen = selected is not null;
        if (selected is not null) Dispatcher.BeginInvoke(new Action(ConfigureRelationBubble), DispatcherPriority.Loaded);
    }

    private void CloseRelationDetails_Click(object sender, RoutedEventArgs e)
    {
        RelationDetailsPopup.IsOpen = false;
        RelationsBubbleListBox.SelectedItem = null;
    }

    /// <summary>修正详情气泡的三色分隔、圆角裁切和 CPU 指标列，避免动态布局产生重叠。</summary>
    private void ConfigureRelationBubble()
    {
        if (RelationDetailsPopup.Child is not Border popupBorder || VisualTreeHelper.GetChildrenCount(popupBorder) == 0) return;
        if (VisualTreeHelper.GetChild(popupBorder, 0) is not Grid root) return;
        popupBorder.ClipToBounds = true;
        popupBorder.Background = new SolidColorBrush(Color.FromRgb(244, 250, 255));
        root.Background = Brushes.Transparent;
        var closeButton = FindVisualDescendants<Button>(root).FirstOrDefault(button => string.Equals(button.Content?.ToString(), "×", StringComparison.Ordinal));
        if (closeButton is not null)
        {
            closeButton.Style = (Style)FindResource("SystemButton");
            closeButton.Width = 32;
            closeButton.Height = 32;
            closeButton.Padding = new Thickness(0);
            closeButton.FontSize = 16;
            closeButton.FontWeight = FontWeights.SemiBold;
            closeButton.Foreground = new SolidColorBrush(Color.FromRgb(77, 124, 254));
            closeButton.Background = new SolidColorBrush(Color.FromArgb(148, 255, 255, 255));
            closeButton.BorderBrush = new SolidColorBrush(Color.FromArgb(174, 209, 232, 247));
        }

        var accent = root.Children.OfType<Grid>().FirstOrDefault(grid =>
            grid.ColumnDefinitions.Count == 4 &&
            grid.Children.OfType<Border>().Count() == 4 &&
            grid.Children.OfType<Border>().All(border => Math.Abs(border.Height - 3) < 0.1));
        if (accent is not null)
        {
            Grid.SetRow(accent, 1);
            while (accent.ColumnDefinitions.Count > 3)
            {
                accent.Children.RemoveAt(accent.Children.Count - 1);
                accent.ColumnDefinitions.RemoveAt(accent.ColumnDefinitions.Count - 1);
            }

            var colors = new[] { Color.FromRgb(45, 174, 188), Color.FromRgb(137, 113, 200), Color.FromRgb(201, 138, 59) };
            foreach (var (border, index) in accent.Children.OfType<Border>().Select((border, index) => (border, index))) border.Background = new SolidColorBrush(colors[index]);
        }

        var metrics = FindVisualDescendants<Grid>(root).FirstOrDefault(grid => grid.ColumnDefinitions.Count == 5 && grid.Children.OfType<Border>().Count() == 4);
        if (metrics is null) return;
        var metricBorders = metrics.Children.OfType<Border>().ToArray();
        var separator = metricBorders.FirstOrDefault(border => Math.Abs(border.Width - 1) < 0.1);
        var cpu = metricBorders.FirstOrDefault(border => Grid.GetColumn(border) == 3);
        if (separator is not null) Grid.SetColumn(separator, 1);
        if (cpu is not null) Grid.SetColumn(cpu, 2);
    }
    private void PortColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag?.ToString() ?? "Port";
        _portsAscending = string.Equals(_portSortKey, key, StringComparison.OrdinalIgnoreCase) ? !_portsAscending : true;
        _portSortKey = key;
        _portHeaderSortActive = true;
        UpdatePortHeaderIndicators();
        ApplyPortFilter();
    }

    private void EnvironmentFilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyEnvironmentFilter();

    private void ProcessColumnHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { Tag: string key }) return;
        _processesAscending = string.Equals(_processSortKey, key, StringComparison.OrdinalIgnoreCase) ? !_processesAscending : true;
        _processSortKey = key;
        _processHeaderSortActive = true;
        UpdateProcessHeaderIndicators();
        ApplyProcessFilter();
        e.Handled = true;
    }

    private void ServiceColumnHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { Tag: string key }) return;
        _servicesAscending = string.Equals(_serviceSortKey, key, StringComparison.OrdinalIgnoreCase) ? !_servicesAscending : true;
        _serviceSortKey = key;
        _serviceHeaderSortActive = true;
        UpdateServiceHeaderIndicators();
        ApplyServiceFilter();
        e.Handled = true;
    }

    private void ApplyPortFilter()
    {
        var keyword = PortFilterTextBox?.Text.Trim() ?? string.Empty;
        var showSystemProcesses = ShowSystemProcessesCheckBox?.IsChecked != false;
        var showIpv6 = ShowIPv6CheckBox?.IsChecked != false;
        var filtered = _allPorts.Where(item =>
            (showSystemProcesses || !item.IsSystemProcess) &&
            (showIpv6 || !item.IsIpv6) &&
            (string.IsNullOrWhiteSpace(keyword) || $"{item.Protocol} {item.LocalAddress} {item.Port} {item.ProcessName} {item.ProcessId} {item.State}".Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        filtered = _portSortKey switch
        {
            "Protocol" => Sort(filtered, item => item.Protocol, _portsAscending),
            "LocalAddress" => Sort(filtered, item => item.LocalAddress, _portsAscending),
            "Process" => Sort(filtered, item => item.ProcessName, _portsAscending),
            "ProcessId" => Sort(filtered, item => item.ProcessId, _portsAscending),
            "State" => Sort(filtered, item => item.State, _portsAscending),
            _ => Sort(filtered, item => item.Port, _portsAscending)
        };
        var visiblePorts = filtered.ToArray();
        Replace(_ports, visiblePorts);
        PortsSummaryText.Text = $"显示 {_ports.Count:N0} 个端口";
        ConfigurePortColumns();
    }

    private void ApplyProcessFilter()
    {
        var keyword = ProcessFilterTextBox?.Text.Trim() ?? string.Empty;
        var showActiveOnly = ShowActiveProcessesCheckBox?.IsChecked == true;
        var showSystemProcesses = ShowSystemProcessEntriesCheckBox?.IsChecked != false;
        var filtered = _allProcesses.Where(item =>
            (!showActiveOnly || item.IsActive) &&
            (showSystemProcesses || !item.IsSystemProcess) &&
            (string.IsNullOrWhiteSpace(keyword) || $"{item.Name} {item.ProcessId} {item.Path}".Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        var groups = filtered
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProcessGroup(group.Key, group.ToArray()));
        groups = _processSortKey switch
        {
            "ProcessId" => Sort(groups, item => item.Items.Min(process => process.ProcessId), _processesAscending),
            "Cpu" => Sort(groups, item => item.CpuPercent, _processesAscending),
            "Memory" => Sort(groups, item => item.MemoryBytes, _processesAscending),
            "Disk" => Sort(groups, item => item.DiskBytesPerSecond, _processesAscending),
            "Network" => Sort(groups, item => item.NetworkBitsPerSecond, _processesAscending),
            "Started" => Sort(groups, item => item.StartedAt ?? DateTime.MinValue, _processesAscending),
            _ => Sort(groups, item => item.Name, _processesAscending)
        };
        var rows = new List<ProcessListRow>();
        foreach (var group in groups)
        {
            var expanded = group.Items.Count > 1 && _expandedProcessGroups.Contains(group.Name);
            rows.Add(ProcessListRow.CreateGroup(group, expanded));
            if (!expanded) continue;

            var children = _processSortKey switch
            {
                "Cpu" => Sort(group.Items, item => item.CpuPercent, _processesAscending),
                "Memory" => Sort(group.Items, item => item.MemoryBytes, _processesAscending),
                "Disk" => Sort(group.Items, item => item.DiskBytesPerSecond, _processesAscending),
                "Network" => Sort(group.Items, item => item.NetworkBitsPerSecond, _processesAscending),
                "Started" => Sort(group.Items, item => item.StartedAt ?? DateTime.MinValue, _processesAscending),
                _ => Sort(group.Items, item => item.Name, _processesAscending)
            };
            rows.AddRange(children.Select(ProcessListRow.CreateChild));
        }

        var visibleRows = rows.ToArray();
        Replace(_processes, visibleRows);
        ProcessesSummaryText.Text = $"显示 {groups.Count():N0} 个应用 · {filtered.Count():N0} 个进程";
    }

    private void ApplyServiceFilter()
    {
        var keyword = ServiceFilterTextBox?.Text.Trim() ?? string.Empty;
        var showRunning = ShowRunningServicesCheckBox?.IsChecked != false;
        var showStopped = ShowStoppedServicesCheckBox?.IsChecked != false;
        var filtered = _allServices.Where(item =>
            ((showRunning && item.Status == "运行中") || (showStopped && item.Status != "运行中")) &&
            (string.IsNullOrWhiteSpace(keyword) || $"{item.Name} {item.DisplayName} {item.Status}".Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        filtered = _serviceSortKey switch
        {
            "Status" => Sort(filtered, item => item.Status, _servicesAscending),
            "Service" => Sort(filtered, item => item.Name, _servicesAscending),
            _ => Sort(filtered, item => item.DisplayName, _servicesAscending)
        };
        Replace(_services, filtered);
        ServicesSummaryText.Text = $"显示 {_services.Count:N0} 项服务";
    }

    private void ApplyRelationshipFilter()
    {
        var keyword = RelationsBubblePanel.Visibility == Visibility.Visible
            ? RelationshipBubbleFilterTextBox.Text.Trim()
            : RelationshipFilterTextBox?.Text.Trim() ?? string.Empty;
        var scope = RelationsBubblePanel.Visibility == Visibility.Visible
            ? SelectedTag(RelationshipBubbleScopeComboBox)
            : SelectedTag(RelationshipScopeComboBox);
        var showSystemProcesses = RelationsBubblePanel.Visibility == Visibility.Visible
            ? ShowSystemBubbleRelationsCheckBox.IsChecked != false
            : ShowSystemRelationsCheckBox?.IsChecked != false;
        var filtered = _allRelationships.Where(item =>
            (showSystemProcesses || !item.Process.IsSystemProcess) &&
            (scope switch
            {
                "Services" => item.HasServices,
                "Network" => item.HasNetworkActivity,
                "Listening" => item.HasListeningPorts,
                "Unrelated" => !item.HasServices && !item.HasNetworkActivity,
                _ => true
            }) &&
            (string.IsNullOrWhiteSpace(keyword) ||
              $"{item.ProcessName} {item.ProcessId} {item.ProcessPath} {item.ServiceSummary} {string.Join(' ', item.Services.Select(service => service.Name + " " + service.DisplayName))} {string.Join(' ', item.NetworkEntries.Select(port => port.Protocol + " " + port.LocalAddress + " " + port.RemoteAddress + " " + port.State))}"
                 .Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        var visible = filtered.ToArray();
        Replace(_relationships, visible);
        ConfigureRelationshipColumns();
        RelationsSummaryText.Text = $"运行进程 {_allRelationships.Count:N0} · 已显示 {visible.Length:N0} · 关联服务 {_allRelationships.Sum(item => item.Services.Count):N0} · 网络记录 {_allRelationships.Sum(item => item.NetworkEntries.Count):N0}";
        RelationsBubbleSummaryText.Text = RelationsSummaryText.Text;

        if (RelationsBubbleListBox.SelectedItem is not SystemRelationshipEntry selected || !visible.Contains(selected))
        {
            RelationsBubbleListBox.SelectedItem = null;
            RelationDetailsPopup.IsOpen = false;
        }
    }

    /// <summary>关系看板的表头与数据行使用同一列宽，压缩首列空白并为服务、端口和网络摘要保留阅读间距。</summary>
    private void ConfigureRelationshipColumns()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var grid in FindVisualDescendants<Grid>(RelationsBubblePanel).Where(grid => grid.ColumnDefinitions.Count == 7 && grid.Children.OfType<TextBlock>().Count() >= 6))
            {
                grid.ColumnDefinitions[0].Width = new GridLength(230);
                grid.ColumnDefinitions[1].Width = new GridLength(84);
                grid.ColumnDefinitions[2].Width = new GridLength(166);
                grid.ColumnDefinitions[3].Width = new GridLength(218);
                grid.ColumnDefinitions[4].Width = new GridLength(172);
                grid.ColumnDefinitions[5].Width = new GridLength(76);
                grid.ColumnDefinitions[6].Width = new GridLength(112);
            }
        }), DispatcherPriority.Loaded);
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T item)
            {
                yield return item;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void ApplyEnvironmentFilter()
    {
        var keyword = EnvironmentFilterTextBox?.Text.Trim() ?? string.Empty;
        Replace(_environmentVariables, _allEnvironmentVariables.Where(item => string.IsNullOrWhiteSpace(keyword) || $"{item.Name} {item.Value}".Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        EnvironmentSummaryText.Text = $"{(SelectedEnvironmentScope == EnvironmentVariableTarget.User ? "当前用户" : "系统")} · {_environmentVariables.Count:N0} 项";
    }

    private void OpenProcessDirectory_Click(object sender, RoutedEventArgs e)
    {
        var path = GetEntryFromMenu(sender) switch { ProcessEntry process => process.Path, PortEntry port => port.ProcessPath, _ => string.Empty };
        if (!SystemToolsService.TryOpenProcessDirectory(path, out var error)) MessageBox.Show(error, "无法打开目录", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void EndProcess_Click(object sender, RoutedEventArgs e)
    {
        var entry = GetEntryFromMenu(sender);
        var processId = entry switch { ProcessEntry process => process.ProcessId, PortEntry port => port.ProcessId, _ => 0 };
        var name = entry switch { ProcessEntry process => process.Name, PortEntry port => port.ProcessName, _ => "该进程" };
        if (processId <= 0 || MessageBox.Show($"确定结束 {name}（PID {processId}）吗？\n\n未保存的数据可能丢失。", "确认结束进程", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!SystemToolsService.TryEndProcess(processId, out var error)) MessageBox.Show(error, "结束进程失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        _ = RefreshProcessesAsync();
        _ = RefreshPortsAsync();
    }

    // 旧版端口模板仍保留该事件名称以兼容已加载的 XAML；菜单已从界面隐藏，关联追踪统一由关系看板承载。
    private void LocateProcessByPid_Click(object sender, RoutedEventArgs e) { }

    private void ProcessRowToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProcessListRow { IsGroup: true, CanExpand: true } row) return;
        if (!_expandedProcessGroups.Add(row.Name)) _expandedProcessGroups.Remove(row.Name);
        ApplyProcessFilter();
    }

    private async void ToggleService_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServiceEntry service) return;
        await ControlServiceAsync(sender, service.ShouldStart);
    }

    private async Task ControlServiceAsync(object sender, bool start)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServiceEntry service) return;
        var verb = start ? "启动" : "停止";
        if (MessageBox.Show($"确定{verb}服务“{service.DisplayName}”吗？", $"确认{verb}服务", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var result = await Task.Run(() => SystemToolsService.TryControlService(service.Name, start, out var error) ? null : error);
        if (!string.IsNullOrWhiteSpace(result)) MessageBox.Show(result, $"{verb}服务失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        await RefreshServicesAsync();
    }

    private void EnvironmentScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RefreshEnvironment(); }

    private void EnvironmentListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EnvironmentListBox.SelectedItem is not EnvironmentVariableEntry variable) return;
        EnvironmentNameTextBox.Text = variable.Name;
        EnvironmentValueTextBox.Text = variable.Value;
        SetValueEditor(variable.Name, variable.Value, reloadPathEntries: true);
    }

    private void ClearEnvironmentEditor_Click(object sender, RoutedEventArgs e)
    {
        EnvironmentListBox.SelectedItem = null;
        EnvironmentNameTextBox.Clear();
        EnvironmentValueTextBox.Clear();
        _pathEntries.Clear();
        StandardValueEditor.Visibility = Visibility.Visible;
        PathValueEditor.Visibility = Visibility.Collapsed;
    }

    private void SaveEnvironmentVariable_Click(object sender, RoutedEventArgs e)
    {
        var name = EnvironmentNameTextBox.Text;
        var value = IsPathEditorActive ? string.Join(";", _pathEntries.Select(item => item.Value.Trim()).Where(item => !string.IsNullOrWhiteSpace(item))) : EnvironmentValueTextBox.Text;
        if (!SystemToolsService.TrySaveEnvironmentVariable(name, value, SelectedEnvironmentScope, out var error))
        {
            MessageBox.Show(error, "保存环境变量失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RefreshEnvironment();
        MessageBox.Show(string.IsNullOrWhiteSpace(value) ? "已删除环境变量。" : "环境变量已保存；新启动的程序会读取到新值。", "环境变量", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void EnvironmentNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SetValueEditor(EnvironmentNameTextBox.Text, EnvironmentValueTextBox.Text, reloadPathEntries: PathValueEditor.Visibility != Visibility.Visible);
    }

    private void AddPathFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要加入 Path 的目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        _pathEntries.Add(new PathEntry(dialog.SelectedPath));
        PathEntriesListBox.SelectedIndex = _pathEntries.Count - 1;
    }

    private void AddPathEntry_Click(object sender, RoutedEventArgs e)
    {
        _pathEntries.Add(new PathEntry(string.Empty));
        PathEntriesListBox.SelectedIndex = _pathEntries.Count - 1;
    }

    private void MovePathEntryUp_Click(object sender, RoutedEventArgs e) => MovePathEntry(-1);
    private void MovePathEntryDown_Click(object sender, RoutedEventArgs e) => MovePathEntry(1);

    private void MovePathEntry(int offset)
    {
        var index = PathEntriesListBox.SelectedIndex;
        var nextIndex = index + offset;
        if (index < 0 || nextIndex < 0 || nextIndex >= _pathEntries.Count) return;
        _pathEntries.Move(index, nextIndex);
        PathEntriesListBox.SelectedIndex = nextIndex;
    }

    private void RemovePathEntry_Click(object sender, RoutedEventArgs e)
    {
        var index = PathEntriesListBox.SelectedIndex;
        if (index < 0) return;
        _pathEntries.RemoveAt(index);
        PathEntriesListBox.SelectedIndex = Math.Min(index, _pathEntries.Count - 1);
    }

    private void PathEntriesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private bool IsPathEditorActive => PathValueEditor.Visibility == Visibility.Visible;

    private void SetValueEditor(string name, string value, bool reloadPathEntries)
    {
        var isPath = string.Equals(name.Trim(), "Path", StringComparison.OrdinalIgnoreCase);
        StandardValueEditor.Visibility = isPath ? Visibility.Collapsed : Visibility.Visible;
        PathValueEditor.Visibility = isPath ? Visibility.Visible : Visibility.Collapsed;
        if (!isPath || !reloadPathEntries) return;

        _pathEntries.Clear();
        foreach (var segment in value.Split(';', StringSplitOptions.None))
        {
            _pathEntries.Add(new PathEntry(segment.Trim()));
        }
    }

    private EnvironmentVariableTarget SelectedEnvironmentScope => (EnvironmentScopeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Machine" ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User;

    private static string SelectedTag(ComboBox comboBox) => (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

    private void UpdatePortHeaderIndicators()
    {
        foreach (var (key, indicator) in new[]
                 {
                     ("Protocol", PortProtocolSortIndicator), ("LocalAddress", PortAddressSortIndicator), ("Port", PortNumberSortIndicator),
                     ("Process", PortProcessSortIndicator), ("ProcessId", PortPidSortIndicator), ("State", PortStateSortIndicator)
                 })
        {
            indicator.Text = _portHeaderSortActive && string.Equals(_portSortKey, key, StringComparison.OrdinalIgnoreCase)
                ? (_portsAscending ? "↑" : "↓")
                : string.Empty;
        }
    }

    /// <summary>为进程和服务列表表头注册点击排序，保持与端口表一致的交互。</summary>
    private void ConfigureInteractiveHeaders()
    {
        RegisterHeaders(ProcessesPanel, new[] { "Name", "ProcessId", "Cpu", "Memory", "Disk", "Network", "Started" }, _processHeaders, ProcessColumnHeader_MouseLeftButtonUp);
        RegisterHeaders(ServicesPanel, new[] { "DisplayName", "Status" }, _serviceHeaders, ServiceColumnHeader_MouseLeftButtonUp);
        UpdateProcessHeaderIndicators();
        UpdateServiceHeaderIndicators();
    }

    private static void RegisterHeaders(DependencyObject panel, IReadOnlyList<string> keys, IDictionary<TextBlock, (string Key, string Title)> registry, MouseButtonEventHandler handler)
    {
        if (registry.Count != 0) return;
        var headerGrid = FindHeaderGrid(panel, keys.Count);
        if (headerGrid is null) return;

        for (var column = 0; column < keys.Count; column++)
        {
            var header = headerGrid.Children.OfType<TextBlock>().FirstOrDefault(item => Grid.GetColumn(item) == column);
            if (header is null) continue;
            registry[header] = (keys[column], header.Text);
            header.Tag = keys[column];
            header.Cursor = Cursors.Hand;
            header.ToolTip = "点击按此列排序";
            header.MouseLeftButtonUp += handler;
        }
    }

    private static Grid? FindHeaderGrid(DependencyObject parent, int minimumColumns)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Grid grid && grid.ColumnDefinitions.Count >= minimumColumns && grid.Children.OfType<TextBlock>().Count() >= minimumColumns) return grid;
            var descendant = FindHeaderGrid(child, minimumColumns);
            if (descendant is not null) return descendant;
        }

        return null;
    }

    private void UpdateProcessHeaderIndicators()
    {
        foreach (var (header, data) in _processHeaders)
        {
            header.Text = data.Title + (_processHeaderSortActive && string.Equals(data.Key, _processSortKey, StringComparison.OrdinalIgnoreCase) ? (_processesAscending ? " ↑" : " ↓") : string.Empty);
        }
    }

    private void UpdateServiceHeaderIndicators()
    {
        foreach (var (header, data) in _serviceHeaders)
        {
            header.Text = data.Title + (_serviceHeaderSortActive && string.Equals(data.Key, _serviceSortKey, StringComparison.OrdinalIgnoreCase) ? (_servicesAscending ? " ↑" : " ↓") : string.Empty);
        }
    }

    private static void SelectComboItemByTag(ComboBox comboBox, string key, ref bool isSynchronizing)
    {
        var index = -1;
        for (var candidate = 0; candidate < comboBox.Items.Count; candidate++)
        {
            if ((comboBox.Items[candidate] as ComboBoxItem)?.Tag?.ToString() == key)
            {
                index = candidate;
                break;
            }
        }

        if (index < 0 || comboBox.SelectedIndex == index) return;
        isSynchronizing = true;
        comboBox.SelectedIndex = index;
        isSynchronizing = false;
    }

    private static IEnumerable<T> Sort<T, TKey>(IEnumerable<T> values, Func<T, TKey> selector, bool ascending)
        => ascending ? values.OrderBy(selector) : values.OrderByDescending(selector);

    /// <summary>统一端口表头与行的列宽，避免 IPv6 地址与端口号视觉粘连。</summary>
    private void ConfigurePortColumns()
    {
        ConfigurePortGrid(PortProtocolHeader?.Parent as Grid);
        for (var index = 0; index < PortsListBox.Items.Count; index++)
        {
            if (PortsListBox.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject item) continue;
            var rowGrid = FindPortRowGrid(item);
            ConfigurePortGrid(rowGrid);
            if (rowGrid is null) continue;

            foreach (var text in rowGrid.Children.OfType<TextBlock>().Where(item => Grid.GetColumn(item) == 1))
            {
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.ToolTip = text.Text;
            }
        }
    }

    private static void ConfigurePortGrid(Grid? grid)
    {
        if (grid is null || grid.ColumnDefinitions.Count != 6) return;
        grid.ColumnDefinitions[0].Width = new GridLength(70);
        grid.ColumnDefinitions[1].Width = new GridLength(330);
        grid.ColumnDefinitions[2].Width = new GridLength(130);
        grid.ColumnDefinitions[3].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[4].Width = new GridLength(100);
        grid.ColumnDefinitions[5].Width = new GridLength(150);
    }

    private static Grid? FindPortRowGrid(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Grid { ColumnDefinitions.Count: 6 } rowGrid) return rowGrid;
            var descendant = FindPortRowGrid(child);
            if (descendant is not null) return descendant;
        }

        return null;
    }

    private static void UpdateDirectionButton(Button button, bool ascending)
    {
        button.Content = ascending ? "↑" : "↓";
        button.ToolTip = ascending ? "当前为升序，点击切换为降序" : "当前为降序，点击切换为升序";
    }

    private static object? GetEntryFromMenu(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is { } entry)
        {
            return entry is ProcessListRow processRow ? processRow.Process : entry;
        }

        return ((sender as FrameworkElement)?.Parent as ContextMenu)?.PlacementTarget is FrameworkElement target
            ? target.DataContext
            : null;
    }
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values) { collection.Clear(); foreach (var value in values) collection.Add(value); }

    /// <summary>Path 的单个路径条目，允许在列表中逐项修改与调整顺序。</summary>
    private sealed class PathEntry
    {
        public PathEntry(string value) => Value = value;
        public string Value { get; set; }
    }

    /// <summary>同名进程的汇总，避免多进程应用占满列表；展开后保留原始 PID 行。</summary>
    private sealed record ProcessGroup(string Name, IReadOnlyList<ProcessEntry> Items)
    {
        public double CpuPercent => Items.Sum(item => item.CpuPercent);
        public long MemoryBytes => Items.Sum(item => item.MemoryBytes);
        public double DiskBytesPerSecond => Items.Sum(item => item.DiskBytesPerSecond);
        public double NetworkBitsPerSecond => Items.Sum(item => item.NetworkBitsPerSecond);
        public DateTime? StartedAt
        {
            get
            {
                var values = Items.Select(item => item.StartedAt).Where(item => item.HasValue).Select(item => item!.Value).ToArray();
                return values.Length == 0 ? null : values.Min();
            }
        }
    }

    /// <summary>进程列表的显示行：应用汇总行或可操作的单个子进程行。</summary>
    private sealed record ProcessListRow(string Name, bool IsGroup, bool IsExpanded, int Count, ProcessEntry? Process, double CpuPercent, long MemoryBytes, double DiskBytesPerSecond, double NetworkBitsPerSecond, DateTime? StartedAt)
    {
        public bool CanExpand => IsGroup && Count > 1;
        public string ExpandGlyph => CanExpand ? (IsExpanded ? "▾" : "▸") : string.Empty;
        public string DisplayName => IsGroup && Count > 1 ? $"{Name} ({Count})" : Name;
        public string ProcessIdText => Process?.ProcessId.ToString() ?? "—";
        public string CpuText => $"{CpuPercent:F1}%";
        public string MemoryText => $"{MemoryBytes / 1024d / 1024d:F1} MB";
        public string DiskText => $"{DiskBytesPerSecond / 1024d / 1024d:F2}";
        public string NetworkText => $"{NetworkBitsPerSecond / 1_000_000d:F2}";
        public string StartedAtText => StartedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—";
        public string Path => Process?.Path ?? string.Empty;

        public static ProcessListRow CreateGroup(ProcessGroup group, bool expanded)
            => new(group.Name, true, expanded, group.Items.Count, group.Items.Count == 1 ? group.Items[0] : null, group.CpuPercent, group.MemoryBytes, group.DiskBytesPerSecond, group.NetworkBitsPerSecond, group.StartedAt);

        public static ProcessListRow CreateChild(ProcessEntry process)
            => new(process.Name, false, false, 1, process, process.CpuPercent, process.MemoryBytes, process.DiskBytesPerSecond, process.NetworkBitsPerSecond, process.StartedAt);
    }
}
