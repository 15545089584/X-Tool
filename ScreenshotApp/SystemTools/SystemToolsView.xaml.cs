using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenshotApp.SystemTools;

/// <summary>系统工具页面：所有高风险操作都保持在用户点击后的明确确认路径上。</summary>
public partial class SystemToolsView : UserControl
{
    private readonly ObservableCollection<PortEntry> _ports = new();
    private readonly ObservableCollection<ProcessListRow> _processes = new();
    private readonly ObservableCollection<ServiceEntry> _services = new();
    private readonly ObservableCollection<EnvironmentVariableEntry> _environmentVariables = new();
    private readonly ObservableCollection<PathEntry> _pathEntries = new();
    private IReadOnlyList<PortEntry> _allPorts = Array.Empty<PortEntry>();
    private IReadOnlyList<ProcessEntry> _allProcesses = Array.Empty<ProcessEntry>();
    private IReadOnlyList<ServiceEntry> _allServices = Array.Empty<ServiceEntry>();
    private IReadOnlyList<EnvironmentVariableEntry> _allEnvironmentVariables = Array.Empty<EnvironmentVariableEntry>();
    private bool _portsAscending = true;
    private bool _processesAscending = true;
    private bool _servicesAscending = true;
    private readonly HashSet<string> _expandedProcessGroups = new(StringComparer.OrdinalIgnoreCase);

    public SystemToolsView()
    {
        InitializeComponent();
        PortsListBox.ItemsSource = _ports;
        ProcessesListBox.ItemsSource = _processes;
        ServicesListBox.ItemsSource = _services;
        EnvironmentListBox.ItemsSource = _environmentVariables;
        PathEntriesListBox.ItemsSource = _pathEntries;
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
        var groups = filtered
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProcessGroup(group.Key, group.ToArray()));
        groups = SelectedTag(ProcessSortComboBox) switch
        {
            "Cpu" => Sort(groups, item => item.CpuPercent, _processesAscending),
            "Memory" => Sort(groups, item => item.MemoryBytes, _processesAscending),
            "Started" => Sort(groups, item => item.StartedAt ?? DateTime.MinValue, _processesAscending),
            _ => Sort(groups, item => item.Name, _processesAscending)
        };
        var rows = new List<ProcessListRow>();
        foreach (var group in groups)
        {
            var expanded = group.Items.Count > 1 && _expandedProcessGroups.Contains(group.Name);
            rows.Add(ProcessListRow.CreateGroup(group, expanded));
            if (!expanded) continue;

            var children = SelectedTag(ProcessSortComboBox) switch
            {
                "Cpu" => Sort(group.Items, item => item.CpuPercent, _processesAscending),
                "Memory" => Sort(group.Items, item => item.MemoryBytes, _processesAscending),
                "Started" => Sort(group.Items, item => item.StartedAt ?? DateTime.MinValue, _processesAscending),
                _ => Sort(group.Items, item => item.Name, _processesAscending)
            };
            rows.AddRange(children.Select(ProcessListRow.CreateChild));
        }

        Replace(_processes, rows);
        ProcessesSummaryText.Text = $"显示 {groups.Count():N0} 个应用 · {filtered.Count():N0} 个进程";
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

    private void ProcessRowToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProcessListRow { IsGroup: true, CanExpand: true } row) return;
        if (!_expandedProcessGroups.Add(row.Name)) _expandedProcessGroups.Remove(row.Name);
        ApplyProcessFilter();
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
    private sealed record ProcessListRow(string Name, bool IsGroup, bool IsExpanded, int Count, ProcessEntry? Process, double CpuPercent, long MemoryBytes, DateTime? StartedAt)
    {
        public bool CanExpand => IsGroup && Count > 1;
        public string ExpandGlyph => CanExpand ? (IsExpanded ? "▾" : "▸") : string.Empty;
        public string DisplayName => IsGroup && Count > 1 ? $"{Name} ({Count})" : Name;
        public string ProcessIdText => IsGroup ? "—" : Process?.ProcessId.ToString() ?? "—";
        public string CpuText => $"{CpuPercent:F1}%";
        public string MemoryText => $"{MemoryBytes / 1024d / 1024d:F1} MB";
        public string StartedAtText => StartedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—";
        public string Path => Process?.Path ?? string.Empty;

        public static ProcessListRow CreateGroup(ProcessGroup group, bool expanded)
            => new(group.Name, true, expanded, group.Items.Count, null, group.CpuPercent, group.MemoryBytes, group.StartedAt);

        public static ProcessListRow CreateChild(ProcessEntry process)
            => new(process.Name, false, false, 1, process, process.CpuPercent, process.MemoryBytes, process.StartedAt);
    }
}
