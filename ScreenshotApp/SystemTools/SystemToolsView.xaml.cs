using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using WpfShapes = System.Windows.Shapes;
using System.Windows.Threading;
using ScreenshotApp.StorageAnalysis;

namespace ScreenshotApp.SystemTools;

/// <summary>系统工具页面：所有高风险操作都保持在用户点击后的明确确认路径上。</summary>
public partial class SystemToolsView : UserControl
{
    /// <summary>显示静态硬件信息与环境变量，用于一级“系统工具”入口。</summary>
    public bool EnvironmentOnly { get; set; }
    /// <summary>存储分析只负责发现空间来源，文件操作统一交给文件工作台执行。</summary>
    public event EventHandler<FileWorkbenchNavigationRequestedEventArgs>? FileWorkbenchRequested;
    private readonly ObservableCollection<PortEntry> _ports = new();
    private readonly ObservableCollection<ProcessListRow> _processes = new();
    private readonly ObservableCollection<ServiceEntry> _services = new();
    private readonly ObservableCollection<SystemRelationshipEntry> _relationships = new();
    private readonly ObservableCollection<EnvironmentVariableEntry> _environmentVariables = new();
    private readonly ObservableCollection<PathEntry> _pathEntries = new();
    private readonly ObservableCollection<DeviceDriverCategoryGroup> _driverCategories = new();
    private readonly ObservableCollection<StorageVolumeEntry> _storageVolumes = new();
    private readonly ObservableCollection<PhysicalStorageEntry> _physicalStorage = new();
    private readonly ObservableCollection<SystemDiagnosticGroup> _diagnosticGroups = new();
    private IReadOnlyList<PortEntry> _allPorts = Array.Empty<PortEntry>();
    private IReadOnlyList<ProcessEntry> _allProcesses = Array.Empty<ProcessEntry>();
    private IReadOnlyList<ServiceEntry> _allServices = Array.Empty<ServiceEntry>();
    private IReadOnlyList<SystemRelationshipEntry> _allRelationships = Array.Empty<SystemRelationshipEntry>();
    private IReadOnlyList<EnvironmentVariableEntry> _allEnvironmentVariables = Array.Empty<EnvironmentVariableEntry>();
    private IReadOnlyList<DeviceDriverEntry> _allDrivers = Array.Empty<DeviceDriverEntry>();
    private IReadOnlyList<SystemDiagnosticGroup> _allDiagnosticGroups = Array.Empty<SystemDiagnosticGroup>();
    private IReadOnlyList<SystemDiagnosticTimelineBucket> _diagnosticTimelineBuckets = Array.Empty<SystemDiagnosticTimelineBucket>();
    private IReadOnlyList<DiskHistoryPoint> _diskHistoryPoints = Array.Empty<DiskHistoryPoint>();
    private int _diskHistoryDays = 30;
    private string? _historyVolumeId;
    private SystemDiagnosticSnapshot? _diagnosticSnapshot;
    private int _selectedDiagnosticBucketIndex = -1;
    private CancellationTokenSource? _diagnosticCancellation;
    private bool _portsAscending = true;
    private string _portSortKey = "Port";
    private bool _portHeaderSortActive;
    private readonly DispatcherTimer _portAutoRefreshTimer;
    private readonly DispatcherTimer _diskHistoryAutoCaptureTimer;
    private bool _isRefreshingPorts;
    private bool _isRefreshingRelationships;
    private bool _isRefreshingDeviceInfo;
    private bool _isRefreshingStorage;
    private bool _isCapturingDiskHistory;
    private bool _isStorageSectionActive;
    private bool _processesAscending = true;
    private bool _servicesAscending = true;
    private string _processSortKey = "Name";
    private string _serviceSortKey = "Name";
    private bool _processHeaderSortActive;
    private bool _serviceHeaderSortActive;
    private bool _relationshipsAscending = true;
    private string _relationshipSortKey = "Memory";
    private bool _relationshipHeaderSortActive;
    private readonly Dictionary<TextBlock, (string Key, string Title)> _processHeaders = new();
    private readonly Dictionary<TextBlock, (string Key, string Title)> _serviceHeaders = new();
    private readonly Dictionary<TextBlock, (string Key, string Title)> _relationshipHeaders = new();
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
        DriverCategoryItems.ItemsSource = _driverCategories;
        StorageVolumesItems.ItemsSource = _storageVolumes;
        PhysicalStorageItems.ItemsSource = _physicalStorage;
        DiagnosticResultsListBox.ItemsSource = _diagnosticGroups;
        var diagnosticResultsView = CollectionViewSource.GetDefaultView(_diagnosticGroups);
        diagnosticResultsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SystemDiagnosticGroup.SeverityDisplay)));
        DiagnosticReliabilityChart.CellSelected += DiagnosticReliabilityChart_CellSelected;
        StorageAnalysisView.BackRequested += StorageAnalysisView_BackRequested;
        StorageAnalysisView.FileWorkbenchRequested += StorageAnalysisView_FileWorkbenchRequested;
        PathEntriesListBox.PreviewMouseLeftButtonDown += PathEntriesListBox_PreviewMouseLeftButtonDown;
        _portAutoRefreshTimer = new DispatcherTimer();
        _portAutoRefreshTimer.Tick += AutoRefreshPorts_Tick;
        _diskHistoryAutoCaptureTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _diskHistoryAutoCaptureTimer.Tick += DiskHistoryAutoCaptureTimer_Tick;
        EnvironmentScopeComboBox.SelectedIndex = 0;
        AutoRefreshIntervalComboBox.SelectedIndex = 1;
        UpdatePortAutoRefreshInterval();
        RelationshipBubbleFilterTextBox.HorizontalContentAlignment = HorizontalAlignment.Left;
        RelationshipBubbleFilterTextBox.TextAlignment = TextAlignment.Left;
        PortsListBox.ItemContainerGenerator.StatusChanged += (_, _) => ConfigurePortColumns();
        ProcessesListBox.ItemContainerGenerator.StatusChanged += (_, _) => ConfigureProcessColumns();
        RelationsBubbleListBox.ItemContainerGenerator.StatusChanged += (_, _) => ConfigureRelationshipColumns();
        SetActiveTab("Ports");
        Loaded += async (_, _) =>
        {
            ConfigureModuleMode();
            ConfigureInteractiveHeaders();
            ConfigurePortColumns();
            ConfigureProcessColumns();
            if (EnvironmentOnly)
            {
                StartDiskHistoryAutoCapture();
                await RefreshDeviceInfoAsync();
                RefreshEnvironment();
            }
            else await RefreshPortsAsync();
        };
        Unloaded += (_, _) =>
        {
            _portAutoRefreshTimer.Stop();
            _diskHistoryAutoCaptureTimer.Stop();
            StorageAnalysisView.CancelActiveScan();
            CancelActiveDiagnostics();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                StorageAnalysisView.CancelActiveScan();
                CancelActiveDiagnostics();
            }
        };
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
        try { _allProcesses = await Task.Run(SystemToolsService.GetProcesses); ApplyProcessFilter(); ConfigureProcessColumns(); }
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

    /// <summary>通过 Windows 的公开只读接口刷新静态设备与 PnP 驱动信息，无需管理员权限。</summary>
    private async Task RefreshDeviceInfoAsync()
    {
        if (_isRefreshingDeviceInfo) return;
        _isRefreshingDeviceInfo = true;
        DriverSummaryText.Text = "正在读取设备与驱动信息…";
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var overview = SystemToolsService.GetHardwareOverview();
                var drivers = SystemToolsService.GetDeviceDrivers();
                return (Overview: overview, Drivers: drivers);
            });
            OverviewInfoItems.ItemsSource = snapshot.Overview.Items;
            _allDrivers = snapshot.Drivers;
            ApplyDriverFilter();
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, "读取系统信息失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _isRefreshingDeviceInfo = false; }
    }

    /// <summary>读取 Windows 已挂载卷与物理磁盘的容量信息；保持只读，不接入实时传感器。</summary>
    private async Task RefreshStorageAsync()
    {
        if (_isRefreshingStorage) return;
        _isRefreshingStorage = true;
        StorageSummaryText.Text = "正在读取本地卷与物理磁盘…";
        try
        {
            var overview = await Task.Run(SystemToolsService.GetStorageOverview);
            StorageOverviewPanel.DataContext = overview;
            Replace(_storageVolumes, overview.Volumes);
            Replace(_physicalStorage, overview.PhysicalDisks);
            StorageSummaryText.Text = overview.SummaryText;
            StorageVolumeEmptyState.Visibility = _storageVolumes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PhysicalStorageEmptyState.Visibility = _physicalStorage.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            await LoadDiskHistoryAsync();
        }
        catch (Exception exception)
        {
            StorageSummaryText.Text = $"读取失败：{exception.Message}";
        }
        finally
        {
            _isRefreshingStorage = false;
        }
    }

    /// <summary>系统工具打开期间低频补采每日快照；同一天重复执行不会产生重复记录。</summary>
    private void StartDiskHistoryAutoCapture()
    {
        if (!EnvironmentOnly)
        {
            return;
        }

        _diskHistoryAutoCaptureTimer.Start();
        _ = CaptureTodayDiskHistoryAsync();
    }

    private async void DiskHistoryAutoCaptureTimer_Tick(object? sender, EventArgs e)
    {
        await CaptureTodayDiskHistoryAsync();
    }

    private async Task CaptureTodayDiskHistoryAsync()
    {
        if (_isCapturingDiskHistory)
        {
            return;
        }

        _isCapturingDiskHistory = true;
        try
        {
            await DiskHistoryStore.EnsureTodaySnapshotAsync();
            if (IsLoaded && _isStorageSectionActive && !_isRefreshingStorage)
            {
                await LoadDiskHistoryAsync();
            }
        }
        catch
        {
            // 后台补采失败不打断系统工具；用户仍可通过“立即快照”查看明确错误。
        }
        finally
        {
            _isCapturingDiskHistory = false;
        }
    }

    /// <summary>补采今日快照并刷新用量历史图表。</summary>
    private async Task LoadDiskHistoryAsync()
    {
        try
        {
            await DiskHistoryStore.EnsureTodaySnapshotAsync();
            PopulateHistoryVolumeCombo();
            var volumeId = (HistoryVolumeComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
            if (string.IsNullOrEmpty(volumeId))
            {
                _diskHistoryPoints = Array.Empty<DiskHistoryPoint>();
                DrawDiskHistoryChart();
                DiskHistorySummaryText.Text = "未找到可记录的固定卷，无法建立用量历史。";
                return;
            }

            _historyVolumeId = volumeId;
            _diskHistoryPoints = await DiskHistoryStore.LoadHistoryAsync(volumeId, _diskHistoryDays);
            DrawDiskHistoryChart();
            UpdateHistoryRangeStates();
            DiskHistorySummaryText.Text = BuildDiskHistorySummary();
        }
        catch (Exception exception)
        {
            DiskHistorySummaryText.Text = "用量历史读取失败：" + exception.Message;
        }
    }

    private void PopulateHistoryVolumeCombo()
    {
        var previous = _historyVolumeId;
        HistoryVolumeComboBox.Items.Clear();
        foreach (var volume in _storageVolumes)
        {
            var label = string.IsNullOrWhiteSpace(volume.VolumeLabel) ? string.Empty : " " + volume.VolumeLabel;
            HistoryVolumeComboBox.Items.Add(new ComboBoxItem { Tag = volume.DriveName, Content = volume.DriveName.TrimEnd('\\') + label });
        }
        if (HistoryVolumeComboBox.Items.Count > 0)
        {
            var match = HistoryVolumeComboBox.Items
                .Cast<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, previous, StringComparison.OrdinalIgnoreCase));
            HistoryVolumeComboBox.SelectedItem = match ?? HistoryVolumeComboBox.Items[0];
        }
    }

    private void HistoryVolume_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || HistoryVolumeComboBox.SelectedItem is not ComboBoxItem { Tag: string volumeId })
        {
            return;
        }
        _historyVolumeId = volumeId;
        _ = RefreshDiskHistoryAsync();
    }

    private void HistoryRange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string days } && int.TryParse(days, out var parsed))
        {
            _diskHistoryDays = parsed;
            UpdateHistoryRangeStates();
            _ = RefreshDiskHistoryAsync();
        }
    }

    private async Task RefreshDiskHistoryAsync()
    {
        try
        {
            _diskHistoryPoints = _historyVolumeId == null
                ? Array.Empty<DiskHistoryPoint>()
                : await DiskHistoryStore.LoadHistoryAsync(_historyVolumeId, _diskHistoryDays);
            DrawDiskHistoryChart();
            DiskHistorySummaryText.Text = BuildDiskHistorySummary();
        }
        catch (Exception exception)
        {
            DiskHistorySummaryText.Text = "用量历史读取失败：" + exception.Message;
        }
    }

    private async void CaptureDiskSnapshot_Click(object sender, RoutedEventArgs e)
    {
        CaptureSnapshotButton.IsEnabled = false;
        try
        {
            await DiskHistoryStore.EnsureTodaySnapshotAsync();
            await RefreshDiskHistoryAsync();
            DiskHistorySummaryText.Text = "已记录今日快照。" + BuildDiskHistorySummary();
        }
        catch (Exception exception)
        {
            DiskHistorySummaryText.Text = "快照失败：" + exception.Message;
        }
        finally
        {
            CaptureSnapshotButton.IsEnabled = true;
        }
    }

    private async void ClearDiskHistory_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (MessageBox.Show(window,
                "确定清空全部磁盘用量历史？此操作不会影响磁盘上的任何文件。",
                "清理用量历史",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            await DiskHistoryStore.ClearAsync();
            _diskHistoryPoints = Array.Empty<DiskHistoryPoint>();
            DrawDiskHistoryChart();
            DiskHistorySummaryText.Text = "用量历史已清空，程序运行期间会自动重新开始记录。";
        }
        catch (Exception exception)
        {
            DiskHistorySummaryText.Text = "清理失败：" + exception.Message;
        }
    }

    private void UpdateHistoryRangeStates()
    {
        SetRangeButtonState(HistoryRange7Button, _diskHistoryDays == 7);
        SetRangeButtonState(HistoryRange30Button, _diskHistoryDays == 30);
        SetRangeButtonState(HistoryRange90Button, _diskHistoryDays == 90);
    }

    private static void SetRangeButtonState(Button button, bool selected)
    {
        button.Background = new SolidColorBrush(selected ? Color.FromRgb(222, 236, 255) : Color.FromArgb(134, 255, 255, 255));
        button.BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(102, 158, 255) : Color.FromArgb(166, 209, 232, 247));
    }

    private string BuildDiskHistorySummary()
    {
        if (_diskHistoryPoints.Count == 0)
        {
            return "暂无记录，点击“立即快照”可立即建立今日记录；X-Tool 运行期间会每天自动补采。";
        }

        var latest = _diskHistoryPoints[^1];
        var first = _diskHistoryPoints[0];
        var usedDelta = latest.UsedBytes - first.UsedBytes;
        var sign = usedDelta >= 0 ? "增加" : "减少";
        return $"{_diskHistoryDays} 天内有 {_diskHistoryPoints.Count} 条记录 · 当前已用 {FormatCapacity(latest.UsedBytes)} / 总 {FormatCapacity(latest.TotalBytes)} · 区间内{sign} {FormatCapacity(Math.Abs(usedDelta))} · 程序未运行日期无法补采";
    }

    private static string FormatCapacity(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024 / 1024:0.0} GB";
        }
        return $"{bytes / 1024.0 / 1024:0.0} MB";
    }

    private void DiskHistoryCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded)
        {
            DrawDiskHistoryChart();
        }
    }

    /// <summary>在画布上自绘容量趋势：已用面积、已用折线、总容量参考线与日期刻度。</summary>
    private void DrawDiskHistoryChart()
    {
        DiskHistoryCanvas.Children.Clear();
        var width = Math.Max(DiskHistoryCanvas.ActualWidth, 320);
        const double height = 190;
        var left = 8.0;
        var right = width - 58;
        var top = 10.0;
        var bottom = height - 24;
        var plotWidth = right - left;
        var plotHeight = bottom - top;

        var points = _diskHistoryPoints;
        if (points.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "暂无用量记录",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(138, 154, 172))
            };
            Canvas.SetLeft(empty, width / 2 - 40);
            Canvas.SetTop(empty, height / 2 - 10);
            DiskHistoryCanvas.Children.Add(empty);
            return;
        }

        var maxTotal = points.Max(point => point.TotalBytes);
        var yTop = Math.Max(maxTotal, 1L);

        // 网格线（0 / 50% / 100%）。
        for (var i = 0; i <= 2; i++)
        {
            var y = top + plotHeight * i / 2.0;
            DiskHistoryCanvas.Children.Add(new WpfShapes.Line
            {
                X1 = left,
                X2 = right,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(50, 112, 146, 178)),
                StrokeThickness = 1
            });
        }

        // 总容量参考虚线。
        DiskHistoryCanvas.Children.Add(new WpfShapes.Line
        {
            X1 = left,
            X2 = right,
            Y1 = top,
            Y2 = top,
            Stroke = new SolidColorBrush(Color.FromRgb(22, 185, 155)),
            StrokeThickness = 1.2,
            StrokeDashArray = new DoubleCollection { 5, 4 }
        });

        // 右轴容量刻度。
        AddAxisLabel(width - 54, top - 7, FormatCapacity(maxTotal));
        AddAxisLabel(width - 54, top + plotHeight / 2 - 7, FormatCapacity(maxTotal / 2));
        AddAxisLabel(width - 54, bottom - 7, "0");

        var scaleX = plotWidth / Math.Max(points.Count - 1, 1);
        var usedPolyline = new PointCollection();
        var areaPolygon = new PointCollection { new Point(left, bottom) };
        for (var i = 0; i < points.Count; i++)
        {
            var x = left + scaleX * i;
            var y = bottom - plotHeight * points[i].UsedBytes / yTop;
            usedPolyline.Add(new Point(x, y));
            areaPolygon.Add(new Point(x, y));
        }
        areaPolygon.Add(new Point(right, bottom));

        DiskHistoryCanvas.Children.Add(new WpfShapes.Polygon
        {
            Points = areaPolygon,
            Fill = new SolidColorBrush(Color.FromArgb(42, 77, 124, 254))
        });
        DiskHistoryCanvas.Children.Add(new WpfShapes.Polyline
        {
            Points = usedPolyline,
            Stroke = new SolidColorBrush(Color.FromRgb(77, 124, 254)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        });

        if (points.Count == 1)
        {
            DiskHistoryCanvas.Children.Add(new WpfShapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(Color.FromRgb(77, 124, 254)),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1.5
            });
            Canvas.SetLeft(DiskHistoryCanvas.Children[^1], usedPolyline[0].X - 3.5);
            Canvas.SetTop(DiskHistoryCanvas.Children[^1], usedPolyline[0].Y - 3.5);
        }

        // X 轴日期标签（首 / 中 / 尾）。
        var first = points[0].Date;
        var last = points[^1].Date;
        var middle = points[points.Count / 2].Date;
        AddAxisLabel(left - 14, bottom + 5, first.ToString("MM-dd"));
        AddAxisLabel(left + plotWidth / 2 - 14, bottom + 5, middle.ToString("MM-dd"));
        AddAxisLabel(right - 24, bottom + 5, last.ToString("MM-dd"));
    }

    private void AddAxisLabel(double x, double y, string text)
    {
        DiskHistoryCanvas.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 144, 166))
        });
        Canvas.SetLeft(DiskHistoryCanvas.Children[^1], x);
        Canvas.SetTop(DiskHistoryCanvas.Children[^1], y);
    }

    /// <summary>按需读取可靠性记录，并用分段事件日志补充诊断细节；全过程只读。</summary>
    private async Task RefreshDiagnosticsAsync()
    {
        if (_diagnosticCancellation is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _diagnosticCancellation = cancellation;
        DiagnosticScanButtonText.Text = "取消诊断";
        DiagnosticProgressText.Text = "正在准备 Windows 可靠性诊断…";
        DiagnosticProgressDetailText.Text = "可靠性记录为主，System 与 Application 日志补充；可随时取消";
        var progress = new Progress<SystemDiagnosticProgress>(snapshot =>
        {
            if (!ReferenceEquals(cancellation, _diagnosticCancellation))
            {
                return;
            }

            DiagnosticProgressText.Text = snapshot.SummaryText;
            DiagnosticProgressDetailText.Text = snapshot.Detail;
        });

        try
        {
            var query = new SystemDiagnosticQuery(TimeSpan.FromDays(SelectedDiagnosticDays));
            var snapshot = await Task.Run(
                () => SystemDiagnosticService.Scan(query, progress, cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _diagnosticSnapshot = snapshot;
            _allDiagnosticGroups = snapshot.Groups;
            _selectedDiagnosticBucketIndex = -1;
            DiagnosticProgressText.Text = $"诊断完成 · {snapshot.CompletedAt:yyyy-MM-dd HH:mm:ss}";
            DiagnosticProgressDetailText.Text = snapshot.Failures.Count == 0
                ? "已完成可靠性记录与 System、Application 日志的只读扫描"
                : string.Join("；", snapshot.Failures.Select(failure => $"{failure.LogName}：{failure.Reason}").Take(2));
            ApplyDiagnosticFilters();
        }
        catch (OperationCanceledException)
        {
            DiagnosticProgressText.Text = "诊断已取消";
            DiagnosticProgressDetailText.Text = "未修改任何系统设置或事件日志";
        }
        catch (Exception exception)
        {
            DiagnosticProgressText.Text = $"诊断失败：{exception.Message}";
            DiagnosticProgressDetailText.Text = "未修改任何系统设置；可稍后重试";
        }
        finally
        {
            if (ReferenceEquals(cancellation, _diagnosticCancellation))
            {
                _diagnosticCancellation = null;
                DiagnosticScanButtonText.Text = "开始诊断";
            }

            cancellation.Dispose();
        }
    }

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        var section = (sender as FrameworkElement)?.Tag?.ToString() ?? "Ports";
        _isStorageSectionActive = section == "Storage";
        if (section != "Storage")
        {
            StorageAnalysisView.CancelActiveScan();
        }
        if (section != "Diagnostics")
        {
            CancelActiveDiagnostics();
        }
        PortsPanel.Visibility = section == "Ports" ? Visibility.Visible : Visibility.Collapsed;
        ProcessesPanel.Visibility = section == "Processes" ? Visibility.Visible : Visibility.Collapsed;
        ServicesPanel.Visibility = section == "Services" ? Visibility.Visible : Visibility.Collapsed;
        RelationsBubblePanel.Visibility = section == "Relations" ? Visibility.Visible : Visibility.Collapsed;
        OverviewPanel.Visibility = section == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        StoragePanel.Visibility = section == "Storage" ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentPanel.Visibility = section == "Environment" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = section == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        SetActiveTab(section);
        if (section == "Processes") _ = RefreshProcessesAsync();
        else if (section == "Services") _ = RefreshServicesAsync();
        else if (section == "Relations") _ = RefreshRelationshipsAsync();
        else if (section == "Overview") _ = RefreshDeviceInfoAsync();
        else if (section == "Storage") _ = RefreshStorageAsync();
        else if (section == "Environment") RefreshEnvironment();
    }

    /// <summary>资源管理保留观察与关联功能；系统工具承载设备、存储与环境变量功能。</summary>
    private void ConfigureModuleMode()
    {
        if (EnvironmentOnly)
        {
            PortsTabButton.Visibility = Visibility.Collapsed;
            ProcessesTabButton.Visibility = Visibility.Collapsed;
            ServicesTabButton.Visibility = Visibility.Collapsed;
            RelationsTabButton.Visibility = Visibility.Collapsed;
            OverviewTabButton.Visibility = Visibility.Visible;
            StorageTabButton.Visibility = Visibility.Visible;
            DiagnosticsTabButton.Visibility = Visibility.Visible;
            Grid.SetColumn(OverviewTabButton, 0);
            Grid.SetColumn(StorageTabButton, 2);
            Grid.SetColumn(EnvironmentTabButton, 4);
            Grid.SetColumn(DiagnosticsTabButton, 6);
            AutoRefreshHostPanel.Visibility = Visibility.Collapsed;
            PortsPanel.Visibility = Visibility.Collapsed;
            ProcessesPanel.Visibility = Visibility.Collapsed;
            ServicesPanel.Visibility = Visibility.Collapsed;
            RelationsBubblePanel.Visibility = Visibility.Collapsed;
            EnvironmentPanel.Visibility = Visibility.Collapsed;
            StoragePanel.Visibility = Visibility.Collapsed;
            DiagnosticsPanel.Visibility = Visibility.Collapsed;
            StorageOverviewContentPanel.Visibility = Visibility.Visible;
            StorageAnalysisView.Visibility = Visibility.Collapsed;
            OverviewPanel.Visibility = Visibility.Visible;
            SetActiveTab("Overview");
            SetPageHeading("系统工具", "查看设备信息、存储空间、环境变量与系统诊断；所有写入操作都会在执行前明确确认。");
            return;
        }

        OverviewTabButton.Visibility = Visibility.Collapsed;
        StorageTabButton.Visibility = Visibility.Collapsed;
        EnvironmentTabButton.Visibility = Visibility.Collapsed;
        DiagnosticsTabButton.Visibility = Visibility.Collapsed;
        OverviewPanel.Visibility = Visibility.Collapsed;
        StoragePanel.Visibility = Visibility.Collapsed;
        DiagnosticsPanel.Visibility = Visibility.Collapsed;
        HideStorageAnalysis();
        AutoRefreshHostPanel.Visibility = Visibility.Visible;
        EnvironmentPanel.Visibility = Visibility.Collapsed;
        SetPageHeading("资源管理", "查看本机端口、进程、服务与关联关系；系统级操作会在执行时明确提示权限要求。");
    }

    private void SetPageHeading(string title, string subtitle)
    {
        PageTitleText.Text = title;
        PageSubtitleText.Text = subtitle;
    }

    private void SetActiveTab(string section)
    {
        foreach (var (button, name) in new[] { (PortsTabButton, "Ports"), (ProcessesTabButton, "Processes"), (ServicesTabButton, "Services"), (RelationsTabButton, "Relations"), (OverviewTabButton, "Overview"), (StorageTabButton, "Storage"), (EnvironmentTabButton, "Environment"), (DiagnosticsTabButton, "Diagnostics") })
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
    private async void RefreshDeviceInfo_Click(object sender, RoutedEventArgs e) => await RefreshDeviceInfoAsync();
    private async void RefreshStorage_Click(object sender, RoutedEventArgs e) => await RefreshStorageAsync();

    private async void DiagnosticScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosticCancellation is not null)
        {
            CancelActiveDiagnostics();
            return;
        }

        await RefreshDiagnosticsAsync();
    }

    private void DiagnosticTimeRangeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _diagnosticSnapshot is null || _diagnosticCancellation is not null)
        {
            return;
        }

        DiagnosticProgressText.Text = "时间范围已更改";
        DiagnosticProgressDetailText.Text = "点击“开始诊断”后按新范围重新读取日志；当前结果仍保留";
    }

    private void DiagnosticFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || DiagnosticResultsListBox is null)
        {
            return;
        }

        ApplyDiagnosticFilters();
    }

    private void ApplyDiagnosticFilters()
    {
        if (DiagnosticResultsListBox is null)
        {
            return;
        }

        var categoryTag = SelectedTag(DiagnosticCategoryComboBox);
        var severityTag = SelectedTag(DiagnosticSeverityComboBox);
        UpdateDiagnosticReliabilityGrid(categoryTag, severityTag);
        var filteredEvents = SelectedDiagnosticTimelineEvents(categoryTag, severityTag).ToArray();
        var selectedGroupKeys = filteredEvents.Select(item => item.GroupKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filtered = _allDiagnosticGroups
            .Where(group => selectedGroupKeys.Contains(group.GroupKey))
            .OrderBy(group => group.SeverityPriority)
            .ThenByDescending(group => group.Count)
            .ThenByDescending(group => group.LastSeen)
            .ToArray();
        var previousKey = (DiagnosticResultsListBox.SelectedItem as SystemDiagnosticGroup)?.GroupKey;
        Replace(_diagnosticGroups, filtered);
        DiagnosticFilteredCountText.Text = _diagnosticSnapshot is null
            ? string.Empty
            : $"{filtered.Length:N0} 组 · {filteredEvents.Length:N0} 条事件";
        DiagnosticEmptyState.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_diagnosticSnapshot is null)
        {
            DiagnosticEmptyTitleText.Text = "尚未生成诊断结果";
            DiagnosticEmptyDescriptionText.Text = "点击“开始诊断”读取最近的系统与应用事件";
        }
        else if (filtered.Length == 0)
        {
            DiagnosticEmptyTitleText.Text = _allDiagnosticGroups.Count == 0 ? "本次范围内未发现异常记录" : "所选时间格没有匹配事件";
            DiagnosticEmptyDescriptionText.Text = _allDiagnosticGroups.Count == 0 ? "关键、错误和警告事件均未形成诊断结果" : "可点击其他日期列，或调整顶部筛选";
        }

        UpdateDiagnosticSelectionSummary(filteredEvents.Length);

        DiagnosticResultsListBox.SelectedItem = filtered.FirstOrDefault(group => group.GroupKey == previousKey) ?? filtered.FirstOrDefault();
        UpdateDiagnosticDetails();
    }

    /// <summary>按当前筛选构建类似可靠性监视器的可点击时间格。</summary>
    private void UpdateDiagnosticReliabilityGrid(string categoryTag, string severityTag)
    {
        if (_diagnosticSnapshot is null)
        {
            _diagnosticTimelineBuckets = Array.Empty<SystemDiagnosticTimelineBucket>();
            DiagnosticReliabilityChart.Buckets = _diagnosticTimelineBuckets;
            DiagnosticTimelineSummaryText.Text = "完成诊断后按时间与事件级别展示异常分布";
            DiagnosticSelectedCellText.Text = "尚未选择时间格";
            return;
        }

        var snapshot = _diagnosticSnapshot;
        var events = snapshot.TimelineEvents.Where(item =>
            (categoryTag == "All" || string.Equals(item.Category.ToString(), categoryTag, StringComparison.OrdinalIgnoreCase)) &&
            (severityTag == "All" || string.Equals(item.Severity.ToString(), severityTag, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        // 24 小时视图按小时展示；日期范围严格保持一列对应一天。
        var bucketCount = snapshot.TimeRange.TotalDays <= 1.1
            ? 24
            : Math.Max(1, (int)Math.Round(snapshot.TimeRange.TotalDays));
        var end = snapshot.CompletedAt;
        var start = end - snapshot.TimeRange;
        var bucketDuration = TimeSpan.FromTicks(snapshot.TimeRange.Ticks / bucketCount);
        var buckets = new SystemDiagnosticTimelineBucket[bucketCount];
        for (var index = 0; index < bucketCount; index++)
        {
            var bucketStart = start + TimeSpan.FromTicks(bucketDuration.Ticks * index);
            var bucketEnd = index == bucketCount - 1 ? end : bucketStart + bucketDuration;
            var label = snapshot.TimeRange.TotalDays <= 1.1 ? bucketStart.ToString("HH:mm") : bucketStart.ToString("M/d");
            var bucketEvents = events.Where(item =>
                item.OccurredAt >= bucketStart &&
                (index == bucketCount - 1 ? item.OccurredAt <= bucketEnd : item.OccurredAt < bucketEnd))
                .ToArray();
            buckets[index] = new SystemDiagnosticTimelineBucket(
                bucketStart,
                bucketEnd,
                label,
                bucketEvents.Count(item => item.Severity == SystemDiagnosticSeverity.Critical),
                bucketEvents.Count(item => item.Severity == SystemDiagnosticSeverity.Error),
                bucketEvents.Count(item => item.Severity == SystemDiagnosticSeverity.Warning));
        }

        _diagnosticTimelineBuckets = buckets;
        DiagnosticReliabilityChart.Buckets = buckets;
        var intervalText = snapshot.TimeRange.TotalDays <= 1.1 ? "每小时" : "每天";
        DiagnosticTimelineSummaryText.Text = $"当前筛选共 {events.Length:N0} 条事件 · {intervalText}汇总 · 红色关键、橙色错误、蓝色警告" +
            (snapshot.WasTruncated ? " · 已达到读取上限，图中为本次已读取样本" : string.Empty);

        if (_selectedDiagnosticBucketIndex < 0 || _selectedDiagnosticBucketIndex >= buckets.Length)
        {
            _selectedDiagnosticBucketIndex = Array.FindLastIndex(buckets, bucket => bucket.TotalCount > 0);
            if (_selectedDiagnosticBucketIndex < 0)
            {
                _selectedDiagnosticBucketIndex = buckets.Length - 1;
            }
        }

        DiagnosticReliabilityChart.SelectCell(_selectedDiagnosticBucketIndex, null, false);
    }

    private IEnumerable<SystemDiagnosticTimelineEvent> SelectedDiagnosticTimelineEvents(string categoryTag, string severityTag)
    {
        if (_diagnosticSnapshot is null || _selectedDiagnosticBucketIndex < 0 || _selectedDiagnosticBucketIndex >= _diagnosticTimelineBuckets.Count)
        {
            return Enumerable.Empty<SystemDiagnosticTimelineEvent>();
        }

        var bucket = _diagnosticTimelineBuckets[_selectedDiagnosticBucketIndex];
        return _diagnosticSnapshot.TimelineEvents.Where(item =>
            item.OccurredAt >= bucket.Start &&
            (_selectedDiagnosticBucketIndex == _diagnosticTimelineBuckets.Count - 1 ? item.OccurredAt <= bucket.End : item.OccurredAt < bucket.End) &&
            (categoryTag == "All" || string.Equals(item.Category.ToString(), categoryTag, StringComparison.OrdinalIgnoreCase)) &&
            (severityTag == "All" || string.Equals(item.Severity.ToString(), severityTag, StringComparison.OrdinalIgnoreCase)));
    }

    private void DiagnosticReliabilityChart_CellSelected(object? sender, SystemDiagnosticTimeCellSelectedEventArgs e)
    {
        _selectedDiagnosticBucketIndex = e.BucketIndex;
        ApplyDiagnosticFilters();
    }

    private void UpdateDiagnosticSelectionSummary(int eventCount)
    {
        if (_diagnosticSnapshot is null || _selectedDiagnosticBucketIndex < 0 || _selectedDiagnosticBucketIndex >= _diagnosticTimelineBuckets.Count)
        {
            DiagnosticSelectedCellText.Text = "尚未选择时间格";
            DiagnosticSummaryText.Text = _diagnosticSnapshot?.SummaryText ?? "先运行诊断，再点击上方日期或事件格";
            return;
        }

        var bucket = _diagnosticTimelineBuckets[_selectedDiagnosticBucketIndex];
        var severityText = SelectedTag(DiagnosticSeverityComboBox) switch
        {
            "Critical" => "关键事件",
            "Error" => "错误事件",
            "Warning" => "警告事件",
            _ => "全部级别"
        };
        var rangeText = bucket.Start.Date == bucket.End.Date
            ? $"{bucket.Start:yyyy-MM-dd HH:mm} - {bucket.End:HH:mm}"
            : $"{bucket.Start:yyyy-MM-dd} 至 {bucket.End:yyyy-MM-dd}";
        DiagnosticSelectedCellText.Text = $"{bucket.Label} · {severityText} · {eventCount:N0} 条";
        DiagnosticSummaryText.Text = $"{rangeText} · {severityText}；已按危险等级从高到低排列";
    }

    private void DiagnosticResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDiagnosticDetails();

    private void UpdateDiagnosticDetails()
    {
        var selected = DiagnosticResultsListBox?.SelectedItem as SystemDiagnosticGroup;
        DiagnosticDetailContent.DataContext = selected;
        DiagnosticDetailContent.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        DiagnosticDetailEmptyState.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyDiagnosticDetails_Click(object sender, RoutedEventArgs e)
    {
        if (DiagnosticResultsListBox.SelectedItem is not SystemDiagnosticGroup selected)
        {
            return;
        }

        try
        {
            Clipboard.SetText(selected.CopyText);
            DiagnosticProgressText.Text = "诊断详情已复制";
            DiagnosticProgressDetailText.Text = "已包含事件摘要、判断信息与 Windows 事件描述";
        }
        catch (Exception exception)
        {
            MessageBox.Show($"复制诊断详情失败：{exception.Message}", "系统诊断", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenPrimaryDiagnosticAction_Click(object sender, RoutedEventArgs e)
    {
        if (DiagnosticResultsListBox.SelectedItem is not SystemDiagnosticGroup selected)
        {
            return;
        }

        try
        {
            if (selected.Category == SystemDiagnosticCategory.Application)
            {
                var executablePath = selected.ApplicationExecutablePath;
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    MessageBox.Show(
                        "Windows 原始记录没有提供可访问的程序路径，暂时无法在资源管理器中定位该程序。",
                        "系统诊断",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var safePath = executablePath.Replace("\"", string.Empty, StringComparison.Ordinal);
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{safePath}\"") { UseShellExecute = true });
                return;
            }

            Process.Start(new ProcessStartInfo("eventvwr.msc", $"/c:{selected.LogName}") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            var actionName = selected.Category == SystemDiagnosticCategory.Application ? "程序位置" : "Windows 事件查看器";
            MessageBox.Show($"无法打开{actionName}：{exception.Message}", "系统诊断", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelActiveDiagnostics()
    {
        if (_diagnosticCancellation is null || _diagnosticCancellation.IsCancellationRequested)
        {
            return;
        }

        DiagnosticProgressText.Text = "正在取消诊断…";
        DiagnosticProgressDetailText.Text = "等待当前 Windows 日志读取操作结束";
        _diagnosticCancellation.Cancel();
    }

    private async void AnalyzeStorageVolume_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StorageVolumeEntry volume)
        {
            return;
        }

        var rootPath = Path.GetPathRoot(volume.DriveName);
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            MessageBox.Show("所选本地卷当前不可用，请刷新存储信息后重试。", "无法分析本地卷", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StorageOverviewContentPanel.Visibility = Visibility.Collapsed;
        StorageAnalysisView.Visibility = Visibility.Visible;
        await StorageAnalysisView.StartAnalysisAsync(new StorageAnalysisTarget(rootPath, volume.Title, volume.TotalBytes, volume.FreeBytes));
    }

    private void StorageAnalysisView_BackRequested(object? sender, EventArgs e) => ShowStorageOverview();

    private void StorageAnalysisView_FileWorkbenchRequested(object? sender, FileWorkbenchNavigationRequestedEventArgs e)
        => FileWorkbenchRequested?.Invoke(this, e);

    private void ShowStorageOverview()
    {
        StorageAnalysisView.CancelAndClear();
        StorageAnalysisView.Visibility = Visibility.Collapsed;
        StorageOverviewContentPanel.Visibility = Visibility.Visible;
    }

    private void HideStorageAnalysis()
    {
        StorageAnalysisView.CancelActiveScan();
        StorageAnalysisView.Visibility = Visibility.Collapsed;
        StorageOverviewContentPanel.Visibility = Visibility.Visible;
    }

    private void DriverFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) ApplyDriverFilter();
    }
    private void DriverScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyDriverFilter();
    }
    private void ToggleDriverCategory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceDriverCategoryGroup category)
        {
            category.IsExpanded = !category.IsExpanded;
        }
    }
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

    private void RelationshipColumnHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { Tag: string key }) return;
        _relationshipsAscending = string.Equals(_relationshipSortKey, key, StringComparison.OrdinalIgnoreCase) ? !_relationshipsAscending : true;
        _relationshipSortKey = key;
        _relationshipHeaderSortActive = true;
        UpdateRelationshipHeaderIndicators();
        ApplyRelationshipFilter();
        e.Handled = true;
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
        filtered = _relationshipSortKey switch
        {
            "Process" => Sort(filtered, item => item.ProcessName, _relationshipsAscending),
            "Pid" => Sort(filtered, item => item.ProcessId, _relationshipsAscending),
            "Ports" => Sort(filtered, item => item.NetworkEntries.Count, _relationshipsAscending),
            "Services" => Sort(filtered, item => item.Services.Count, _relationshipsAscending),
            "Network" => Sort(filtered, item => item.NetworkEntries.Count, _relationshipsAscending),
            "Cpu" => Sort(filtered, item => item.CpuPercent, _relationshipsAscending),
            _ => Sort(filtered, item => item.MemoryBytes, _relationshipsAscending)
        };
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

    /// <summary>设备页默认突出常用类别，同时保留异常项，避免把大量系统组件淹没在首屏。</summary>
    private void ApplyDriverFilter()
    {
        var keyword = DriverFilterTextBox?.Text.Trim() ?? string.Empty;
        var scope = SelectedTag(DriverScopeComboBox);
        var drivers = _allDrivers.Where(item => scope switch
            {
                "Key" => item.IsKeyDevice || item.HasIssue,
                "Issues" => item.HasIssue,
                _ => true
            })
            .Where(item => string.IsNullOrWhiteSpace(keyword) || item.SearchText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var expandMatches = !string.IsNullOrWhiteSpace(keyword) || string.Equals(scope, "Issues", StringComparison.OrdinalIgnoreCase);
        var categories = drivers
            .GroupBy(item => item.Category)
            .OrderBy(group => group.Min(item => item.CategoryOrder))
            .Select(group => new DeviceDriverCategoryGroup(group.Key, group.ToArray(), expandMatches))
            .ToArray();
        Replace(_driverCategories, categories);
        var issueCount = _allDrivers.Count(item => item.HasIssue);
        DriverSummaryText.Text = _allDrivers.Count == 0
            ? "未读取到 Windows PnP 驱动信息"
            : $"显示 {categories.Length:N0} 类 / {drivers.Length:N0} 项 · {issueCount:N0} 项需要注意";
        DriverEmptyState.Visibility = drivers.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenProcessDirectory_Click(object sender, RoutedEventArgs e)
    {
        var path = GetEntryFromMenu(sender) switch { ProcessListRow row => row.Path, ProcessEntry process => process.Path, PortEntry port => port.ProcessPath, _ => string.Empty };
        if (!SystemToolsService.TryOpenProcessDirectory(path, out var error)) MessageBox.Show(error, "无法打开目录", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void EndProcess_Click(object sender, RoutedEventArgs e)
    {
        var entry = GetEntryFromMenu(sender);
        var processes = entry switch
        {
            ProcessListRow row => row.Processes,
            ProcessEntry process => new[] { process },
            _ => Array.Empty<ProcessEntry>()
        };
        var processIds = entry is PortEntry port ? new[] { port.ProcessId } : processes.Select(process => process.ProcessId).Where(processId => processId > 0).Distinct().ToArray();
        var name = entry switch { ProcessListRow row => row.DisplayName, ProcessEntry process => process.Name, PortEntry portEntry => portEntry.ProcessName, _ => "该进程" };
        if (processIds.Length == 0)
        {
            MessageBox.Show("未能读取该行对应的 PID，请刷新列表后重试。", "无法结束进程", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targetDescription = processIds.Length == 1 ? $"PID {processIds[0]}" : $"组内 {processIds.Length} 个进程";
        if (MessageBox.Show($"确定结束 {name}（{targetDescription}）吗？\n\n未保存的数据可能丢失；权限不足时将请求管理员授权。", "确认结束进程", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var result = await Task.Run(() => SystemToolsService.TryEndProcessesWithElevation(processIds, out var error) ? null : error);
        if (!string.IsNullOrWhiteSpace(result))
        {
            MessageBox.Show(result, "结束进程失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show($"已结束 {name}（{targetDescription}）。", "进程管理", MessageBoxButton.OK, MessageBoxImage.Information);
        }

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
        var result = await Task.Run(() => SystemToolsService.TryControlServiceWithElevation(service.Name, start, out var error) ? null : error);
        if (!string.IsNullOrWhiteSpace(result))
        {
            MessageBox.Show(result, $"{verb}服务失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show($"服务“{service.DisplayName}”已{verb}。", "服务管理", MessageBoxButton.OK, MessageBoxImage.Information);
        }

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

    private async void SaveEnvironmentVariable_Click(object sender, RoutedEventArgs e)
    {
        var name = EnvironmentNameTextBox.Text;
        var value = IsPathEditorActive ? string.Join(";", _pathEntries.Select(item => item.Value.Trim()).Where(item => !string.IsNullOrWhiteSpace(item))) : EnvironmentValueTextBox.Text;
        var target = SelectedEnvironmentScope;
        if (target == EnvironmentVariableTarget.Machine && !SystemToolsService.IsRunningAsAdministrator())
        {
            var confirmation = MessageBox.Show(
                "保存系统环境变量需要 Windows 管理员授权。X-Tool 将仅为本次保存请求授权，是否继续？",
                "需要管理员权限",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var saveButton = sender as Button;
        var saveButtonText = saveButton?.Content as TextBlock;
        if (saveButton is not null) saveButton.IsEnabled = false;
        if (saveButtonText is not null) saveButtonText.Text = "保存中…";
        EnvironmentConfigurationResult result;
        try
        {
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = value };
            result = await SystemToolsService.ApplyEnvironmentConfigurationAsync(target, Array.Empty<string>(), variables);
        }
        finally
        {
            if (saveButton is not null) saveButton.IsEnabled = true;
            if (saveButtonText is not null) saveButtonText.Text = "保存变量";
        }
        var saved = result.Succeeded;
        var error = result.Error;
        if (!saved)
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
        var entry = new PathEntry(dialog.SelectedPath);
        _pathEntries.Add(entry);
        SelectAndRevealPathEntry(entry);
    }

    private void AddPathEntry_Click(object sender, RoutedEventArgs e)
    {
        var entry = new PathEntry(string.Empty);
        _pathEntries.Add(entry);
        SelectAndRevealPathEntry(entry);
    }

    private void MovePathEntryUp_Click(object sender, RoutedEventArgs e) => MovePathEntry(-1);
    private void MovePathEntryDown_Click(object sender, RoutedEventArgs e) => MovePathEntry(1);

    private void MovePathEntry(int offset)
    {
        var index = PathEntriesListBox.SelectedIndex;
        var nextIndex = index + offset;
        if (index < 0)
        {
            MessageBox.Show("请先选中要移动的 Path 条目。", "Path 编辑", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (nextIndex < 0 || nextIndex >= _pathEntries.Count) return;
        var entry = _pathEntries[index];
        _pathEntries.Move(index, nextIndex);
        SelectAndRevealPathEntry(entry);
    }

    private void RemovePathEntry_Click(object sender, RoutedEventArgs e)
    {
        var index = PathEntriesListBox.SelectedIndex;
        if (index < 0)
        {
            MessageBox.Show("请先选中要移除的 Path 条目。", "Path 编辑", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _pathEntries.RemoveAt(index);
        if (_pathEntries.Count > 0)
        {
            SelectAndRevealPathEntry(_pathEntries[Math.Min(index, _pathEntries.Count - 1)]);
        }
    }

    private void PathEntriesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void PathEntriesListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(PathEntriesListBox, source) is ListBoxItem item)
        {
            item.IsSelected = true;
        }
    }

    private void SelectAndRevealPathEntry(PathEntry entry)
    {
        PathEntriesListBox.SelectedItem = entry;
        Dispatcher.BeginInvoke(
            new Action(() => PathEntriesListBox.ScrollIntoView(entry)),
            DispatcherPriority.Loaded);
    }

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
    private int SelectedDiagnosticDays => int.TryParse((DiagnosticTimeRangeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var days) ? Math.Clamp(days, 1, 30) : 7;

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
        RegisterHeaders(RelationsBubblePanel, new[] { "Process", "Pid", "Ports", "Services", "Network", "Cpu", "Memory" }, _relationshipHeaders, RelationshipColumnHeader_MouseLeftButtonUp);
        UpdateProcessHeaderIndicators();
        UpdateServiceHeaderIndicators();
        UpdateRelationshipHeaderIndicators();
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

    private void UpdateRelationshipHeaderIndicators()
    {
        foreach (var (header, data) in _relationshipHeaders)
        {
            header.Text = data.Title + (_relationshipHeaderSortActive && string.Equals(data.Key, _relationshipSortKey, StringComparison.OrdinalIgnoreCase) ? (_relationshipsAscending ? " ↑" : " ↓") : string.Empty);
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

    /// <summary>让进程表的固定列共享一致的中心线，并为右侧滚动条保留精确宽度。</summary>
    private void ConfigureProcessColumns()
    {
        var headerGrid = FindHeaderGrid(ProcessesPanel, 7);
        if (headerGrid is not null)
        {
            headerGrid.Margin = new Thickness(6, 0, 15, 0);
            AlignProcessGridText(headerGrid);
        }

        for (var index = 0; index < ProcessesListBox.Items.Count; index++)
        {
            if (ProcessesListBox.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject item) continue;
            var rowGrid = FindGridWithColumnCount(item, 7);
            AlignProcessGridText(rowGrid);
        }
    }

    private static void AlignProcessGridText(Grid? grid)
    {
        if (grid is null) return;
        foreach (var text in grid.Children.OfType<TextBlock>())
        {
            var column = Grid.GetColumn(text);
            if (column is >= 1 and <= 5) text.HorizontalAlignment = HorizontalAlignment.Center;
            else if (column == 6) text.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private static Grid? FindGridWithColumnCount(DependencyObject parent, int columnCount)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Grid grid && grid.ColumnDefinitions.Count == columnCount) return grid;
            var descendant = FindGridWithColumnCount(child, columnCount);
            if (descendant is not null) return descendant;
        }

        return null;
    }

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
        static object? Normalize(object? value) => value switch
        {
            ProcessListRow processRow => processRow,
            ProcessEntry process => process,
            PortEntry port => port,
            _ => null
        };

        var menuItem = sender as MenuItem;
        var directEntry = Normalize(menuItem?.DataContext);
        if (directEntry is not null) return directEntry;

        var contextMenu = menuItem is null ? null : ItemsControl.ItemsControlFromItemContainer(menuItem) as ContextMenu;
        if (contextMenu?.PlacementTarget is FrameworkElement target)
        {
            return Normalize(target.DataContext);
        }

        return null;
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
    private sealed record ProcessListRow(string Name, bool IsGroup, bool IsExpanded, int Count, ProcessEntry? Process, IReadOnlyList<ProcessEntry> Processes, double CpuPercent, long MemoryBytes, double DiskBytesPerSecond, double NetworkBitsPerSecond, DateTime? StartedAt)
    {
        public bool CanExpand => IsGroup && Count > 1;
        public string ExpandGlyph => CanExpand ? (IsExpanded ? "▾" : "▸") : string.Empty;
        public string DisplayName => IsGroup && Count > 1 ? $"{Name} ({Count})" : Name;
        public string ProcessIdText => Process?.ProcessId.ToString() ?? "—";
        public string CpuText => CpuPercent > 0 && CpuPercent < 0.1 ? "<0.1%" : $"{CpuPercent:F1}%";
        public string MemoryText => $"{MemoryBytes / 1024d / 1024d:F1} MB";
        public string DiskText => $"{DiskBytesPerSecond / 1024d / 1024d:F2}";
        public string NetworkText => $"{NetworkBitsPerSecond / 1_000_000d:F2}";
        public string StartedAtText => StartedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—";
        public string Path => Process?.Path ?? string.Empty;

        public static ProcessListRow CreateGroup(ProcessGroup group, bool expanded)
            => new(group.Name, true, expanded, group.Items.Count, group.Items.Count == 1 ? group.Items[0] : null, group.Items, group.CpuPercent, group.MemoryBytes, group.DiskBytesPerSecond, group.NetworkBitsPerSecond, group.StartedAt);

        public static ProcessListRow CreateChild(ProcessEntry process)
            => new(process.Name, false, false, 1, process, new[] { process }, process.CpuPercent, process.MemoryBytes, process.DiskBytesPerSecond, process.NetworkBitsPerSecond, process.StartedAt);
    }
}
