using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenshotApp.SystemTools;

/// <summary>系统工具页面：所有高风险操作都保持在用户点击后的明确确认路径上。</summary>
public partial class SystemToolsView : UserControl
{
    private readonly ObservableCollection<PortEntry> _ports = new();
    private readonly ObservableCollection<ProcessEntry> _processes = new();
    private readonly ObservableCollection<ServiceEntry> _services = new();
    private readonly ObservableCollection<EnvironmentVariableEntry> _environmentVariables = new();
    private IReadOnlyList<PortEntry> _allPorts = Array.Empty<PortEntry>();
    private IReadOnlyList<ProcessEntry> _allProcesses = Array.Empty<ProcessEntry>();
    private IReadOnlyList<ServiceEntry> _allServices = Array.Empty<ServiceEntry>();
    private IReadOnlyList<EnvironmentVariableEntry> _allEnvironmentVariables = Array.Empty<EnvironmentVariableEntry>();
    private bool _portsAscending = true;
    private bool _processesAscending = true;
    private bool _servicesAscending = true;

    public SystemToolsView()
    {
        InitializeComponent();
        PortsListBox.ItemsSource = _ports;
        ProcessesListBox.ItemsSource = _processes;
        ServicesListBox.ItemsSource = _services;
        EnvironmentListBox.ItemsSource = _environmentVariables;
        EnvironmentScopeComboBox.SelectedIndex = 0;
        PortSortComboBox.SelectedIndex = 0;
        ProcessSortComboBox.SelectedIndex = 0;
        ServiceSortComboBox.SelectedIndex = 0;
        SetActiveTab("Ports");
        Loaded += async (_, _) => await RefreshPortsAsync();
    }

    private async Task RefreshPortsAsync()
    {
        PortsSummaryText.Text = "正在读取…";
        try { _allPorts = await Task.Run(SystemToolsService.GetPorts); ApplyPortFilter(); }
        catch (Exception exception) { PortsSummaryText.Text = $"读取失败：{exception.Message}"; }
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
        EnvironmentPanel.Visibility = section == "Environment" ? Visibility.Visible : Visibility.Collapsed;
        SetActiveTab(section);
        if (section == "Processes") _ = RefreshProcessesAsync();
        else if (section == "Services") _ = RefreshServicesAsync();
        else if (section == "Environment") RefreshEnvironment();
    }

    private void SetActiveTab(string section)
    {
        foreach (var (button, name) in new[] { (PortsTabButton, "Ports"), (ProcessesTabButton, "Processes"), (ServicesTabButton, "Services"), (EnvironmentTabButton, "Environment") })
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
    private void PortFilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyPortFilter();
    private void ProcessFilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyProcessFilter();
    private void ServiceFilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyServiceFilter();
    private void PortSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyPortFilter();
    private void ProcessSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyProcessFilter();
    private void ServiceSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyServiceFilter();
    private void PortSortDirection_Click(object sender, RoutedEventArgs e)
    {
        _portsAscending = !_portsAscending;
        UpdateDirectionButton(PortSortDirectionButton, _portsAscending);
        ApplyPortFilter();
    }

    private void ProcessSortDirection_Click(object sender, RoutedEventArgs e)
    {
        _processesAscending = !_processesAscending;
        UpdateDirectionButton(ProcessSortDirectionButton, _processesAscending);
        ApplyProcessFilter();
    }

    private void ServiceSortDirection_Click(object sender, RoutedEventArgs e)
    {
        _servicesAscending = !_servicesAscending;
        UpdateDirectionButton(ServiceSortDirectionButton, _servicesAscending);
        ApplyServiceFilter();
    }
    private void EnvironmentFilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyEnvironmentFilter();

    private void ApplyPortFilter()
    {
        var keyword = PortFilterTextBox?.Text.Trim() ?? string.Empty;
        var filtered = _allPorts.Where(item => string.IsNullOrWhiteSpace(keyword) || $"{item.Protocol} {item.LocalAddress} {item.ProcessName} {item.ProcessId} {item.State}".Contains(keyword, StringComparison.OrdinalIgnoreCase));
        filtered = SelectedTag(PortSortComboBox) switch
        {
            "Protocol" => Sort(filtered, item => item.Protocol, _portsAscending),
            "Process" => Sort(filtered, item => item.ProcessName, _portsAscending),
            "State" => Sort(filtered, item => item.State, _portsAscending),
            _ => Sort(filtered, item => ExtractPort(item.LocalAddress), _portsAscending)
        };
        Replace(_ports, filtered);
        PortsSummaryText.Text = $"显示 {_ports.Count:N0} 个端口";
    }

    private void ApplyProcessFilter()
    {
        var keyword = ProcessFilterTextBox?.Text.Trim() ?? string.Empty;
        var filtered = _allProcesses.Where(item => string.IsNullOrWhiteSpace(keyword) || $"{item.Name} {item.ProcessId} {item.Path}".Contains(keyword, StringComparison.OrdinalIgnoreCase));
        filtered = SelectedTag(ProcessSortComboBox) switch
        {
            "Cpu" => Sort(filtered, item => item.CpuPercent, _processesAscending),
            "Memory" => Sort(filtered, item => item.MemoryBytes, _processesAscending),
            "Started" => Sort(filtered, item => item.StartedAt ?? DateTime.MinValue, _processesAscending),
            _ => Sort(filtered, item => item.Name, _processesAscending)
        };
        Replace(_processes, filtered);
        ProcessesSummaryText.Text = $"显示 {_processes.Count:N0} 个进程";
    }

    private void ApplyServiceFilter()
    {
        var keyword = ServiceFilterTextBox?.Text.Trim() ?? string.Empty;
        var filtered = _allServices.Where(item => string.IsNullOrWhiteSpace(keyword) || $"{item.Name} {item.DisplayName} {item.Status}".Contains(keyword, StringComparison.OrdinalIgnoreCase));
        filtered = SelectedTag(ServiceSortComboBox) switch
        {
            "Status" => Sort(filtered, item => item.Status, _servicesAscending),
            "Service" => Sort(filtered, item => item.Name, _servicesAscending),
            _ => Sort(filtered, item => item.DisplayName, _servicesAscending)
        };
        Replace(_services, filtered);
        ServicesSummaryText.Text = $"显示 {_services.Count:N0} 项服务";
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

    private async void StartService_Click(object sender, RoutedEventArgs e) => await ControlServiceAsync(sender, start: true);
    private async void StopService_Click(object sender, RoutedEventArgs e) => await ControlServiceAsync(sender, start: false);

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
    }

    private void ClearEnvironmentEditor_Click(object sender, RoutedEventArgs e)
    {
        EnvironmentListBox.SelectedItem = null;
        EnvironmentNameTextBox.Clear();
        EnvironmentValueTextBox.Clear();
    }

    private void SaveEnvironmentVariable_Click(object sender, RoutedEventArgs e)
    {
        var name = EnvironmentNameTextBox.Text;
        var value = EnvironmentValueTextBox.Text;
        if (!SystemToolsService.TrySaveEnvironmentVariable(name, value, SelectedEnvironmentScope, out var error))
        {
            MessageBox.Show(error, "保存环境变量失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RefreshEnvironment();
        MessageBox.Show(string.IsNullOrWhiteSpace(value) ? "已删除环境变量。" : "环境变量已保存；新启动的程序会读取到新值。", "环境变量", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private EnvironmentVariableTarget SelectedEnvironmentScope => (EnvironmentScopeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Machine" ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User;

    private static string SelectedTag(ComboBox comboBox) => (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

    private static IEnumerable<T> Sort<T, TKey>(IEnumerable<T> values, Func<T, TKey> selector, bool ascending)
        => ascending ? values.OrderBy(selector) : values.OrderByDescending(selector);

    private static int ExtractPort(string address)
    {
        var separator = address.LastIndexOf(':');
        return separator >= 0 && int.TryParse(address[(separator + 1)..], out var port) ? port : int.MaxValue;
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
            return entry;
        }

        return ((sender as FrameworkElement)?.Parent as ContextMenu)?.PlacementTarget is FrameworkElement target
            ? target.DataContext
            : null;
    }
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values) { collection.Clear(); foreach (var value in values) collection.Add(value); }
}
