using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.FileWorkbench;

/// <summary>文件工作台页面：搜索结果与待执行操作始终分离，避免误操作。</summary>
public partial class FileWorkbenchView : UserControl
{
    /// <summary>最近一次扫描返回的完整结果池；筛选和排序均在此基础上即时完成。</summary>
    private readonly List<FileWorkbenchItem> _allSearchItems = new();
    /// <summary>当前筛选与排序后的完整结果池；界面只绑定其中一个分页，避免一次创建大量可视元素。</summary>
    private readonly List<FileWorkbenchItem> _resultItems = new();
    private readonly BulkObservableCollection<FileWorkbenchItem> _visibleItems = new();
    private readonly ObservableCollection<FileOperationPlan> _plans = new();
    private readonly ObservableCollection<DuplicateFileGroupViewModel> _duplicateGroups = new();
    private readonly List<FileSortDescriptor> _sortDescriptors = new();
    private const int ResultPageSize = 200;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _duplicateSearchCancellation;
    private int _currentResultPage;
    private long _lastMatchedFiles;
    private bool _lastSearchWasTruncated;
    private bool _lastMatchCountIsExact = true;
    private FileSearchBackend? _lastSearchBackend;
    private string? _lastSearchFallbackReason;
    private FileSearchMode? _lastSearchMode;
    private string? _lastSearchRoot;
    private string? _lastSearchKeyword;
    private string? _lastSearchTypeFilter;
    private DateTime? _lastSearchModifiedAfter;
    private DateTime? _lastSearchCompletedAtUtc;
    private bool _isDuplicateMode;
    private bool _fileDeleteSelectionMode;
    private string _duplicateSummaryText = "请选择文件夹";
    private PreviewContext _previewContext;

    public FileWorkbenchView()
    {
        InitializeComponent();
        FileResultsListBox.ItemsSource = _visibleItems;
        DuplicateResultsListBox.ItemsSource = _duplicateGroups;
        PreviewListBox.ItemsSource = _plans;
        UpdateSortColumnHeaders();
        UpdateResultPageControls();
        UpdateSearchBackendBadge();
        Loaded += (_, _) =>
        {
            SetWorkbenchMode(false);
            UpdateOperationControls();
        };
    }

    private void SelectSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择需要搜索和整理的文件夹", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        SourceFolderTextBox.Text = Path.GetFullPath(dialog.SelectedPath);
        _ = StartCurrentModeAsync();
    }

    private void OpenSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(SourceFolderTextBox.Text)) Process.Start(new ProcessStartInfo(SourceFolderTextBox.Text) { UseShellExecute = true });
    }

    private void OpenFileInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var filePath = (sender as FrameworkElement)?.DataContext switch
        {
            FileWorkbenchItem item => item.FullPath,
            DuplicateFileEntryViewModel entry => entry.FullPath,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        if (!File.Exists(filePath))
        {
            ShowFileLocationStatus("文件已经不存在，无法在资源管理器中定位。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{Path.GetFullPath(filePath)}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowFileLocationStatus($"无法打开资源管理器：{exception.Message}");
        }
    }

    private void ShowFileLocationStatus(string message)
    {
        if (_isDuplicateMode)
        {
            DuplicateSelectionSummaryText.Text = message;
        }
        else
        {
            BatchStatusText.Text = message;
        }
    }

    private async void SearchFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_isDuplicateMode && _duplicateSearchCancellation is not null)
        {
            _duplicateSearchCancellation.Cancel();
            return;
        }

        await StartCurrentModeAsync();
    }

    private async void FullScanFiles_Click(object sender, RoutedEventArgs e) => await SearchAsync(FileSearchMode.FullRecursiveScan);

    private Task StartCurrentModeAsync() => _isDuplicateMode
        ? FindDuplicatesAsync()
        : SearchAsync(FileSearchMode.Smart);

    private async Task SearchAsync(FileSearchMode searchMode = FileSearchMode.Smart)
    {
        var root = SourceFolderTextBox.Text.Trim();
        if (!Directory.Exists(root)) { SearchSummaryText.Text = "请选择有效文件夹"; return; }
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var cancellation = _searchCancellation;
        ClearSearchResults();
        _plans.Clear();
        if (_fileDeleteSelectionMode)
        {
            UpdateFileDeletePreview();
        }
        SearchSummaryText.Text = "正在搜索…";
        SearchProgressPanel.Visibility = Visibility.Visible;
        SearchProgressBar.IsIndeterminate = true;
        SearchProgressSummaryText.Text = searchMode == FileSearchMode.FullRecursiveScan
            ? "正在准备完整递归扫描…"
            : "正在优先查询 Windows Search 系统索引…";
        SearchProgressPathText.Text = root;
        try
        {
            var type = SelectedTypeFilter;
            var modifiedAfter = SelectedModifiedAfter;
            // 必须在 UI 线程先读取筛选条件；后台扫描不能直接访问 WPF 控件。
            var keyword = KeywordTextBox.Text.Trim();
            var sortDescriptors = GetEffectiveSortDescriptors();
            var progress = new Progress<FileSearchProgress>(snapshot =>
            {
                if (!ReferenceEquals(cancellation, _searchCancellation))
                {
                    return;
                }

                SearchProgressSummaryText.Text = snapshot.SummaryText;
                SearchProgressPathText.Text = string.IsNullOrWhiteSpace(snapshot.CurrentPath) ? "正在准备下一项…" : snapshot.CurrentPath;
            });
            var searchResult = await Task.Run(
                () => FileWorkbenchService.Search(root, keyword, type, modifiedAfter, sortDescriptors, searchMode, progress, cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            SearchProgressSummaryText.Text = "扫描完成，正在整理可显示结果…";
            _lastMatchedFiles = searchResult.MatchedFiles;
            _lastSearchWasTruncated = searchResult.IsTruncated;
            _lastMatchCountIsExact = searchResult.IsMatchCountExact;
            _lastSearchBackend = searchResult.Backend;
            _lastSearchFallbackReason = searchResult.FallbackReason;
            _lastSearchMode = searchMode;
            _lastSearchRoot = Path.GetFullPath(root);
            _lastSearchKeyword = keyword;
            _lastSearchTypeFilter = type;
            _lastSearchModifiedAfter = modifiedAfter;
            _lastSearchCompletedAtUtc = DateTime.UtcNow;
            SetSearchResults(searchResult.Items);
            _plans.Clear();
            UpdateSearchBackendBadge();
            UpdateSearchSummary();
            if (_fileDeleteSelectionMode)
            {
                UpdateFileDeletePreview();
            }
            else
            {
                BatchStatusText.Text = searchResult.Backend == FileSearchBackend.RecursiveScan
                    ? searchMode == FileSearchMode.FullRecursiveScan
                        ? "已按你的要求完整递归扫描；Windows Search 未参与本次搜索"
                        : $"已自动回退到本地扫描：{searchResult.FallbackReason ?? "Windows Search 当前不可用"}"
                    : searchResult.IsTruncated
                    ? "结果较多；可用关键词、类型或时间范围缩小搜索后再批处理"
                    : "选择文件后生成操作预览";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { SearchSummaryText.Text = $"搜索失败：{exception.Message}"; }
        finally
        {
            if (ReferenceEquals(cancellation, _searchCancellation))
            {
                _searchCancellation = null;
                SearchProgressBar.IsIndeterminate = false;
                SearchProgressPanel.Visibility = Visibility.Collapsed;
            }

            cancellation.Dispose();
        }
    }

    private async Task FindDuplicatesAsync()
    {
        var root = SourceFolderTextBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            SearchSummaryText.Text = "请选择有效文件夹";
            return;
        }

        _duplicateSearchCancellation?.Cancel();
        _duplicateSearchCancellation?.Dispose();
        _duplicateSearchCancellation = new CancellationTokenSource();
        var cancellation = _duplicateSearchCancellation;
        _plans.Clear();
        _previewContext = PreviewContext.None;
        _duplicateGroups.Clear();
        UpdateDuplicateSelectionSummary();
        _duplicateSummaryText = "正在查找重复文件…";
        SearchSummaryText.Text = _duplicateSummaryText;
        SearchProgressPanel.Visibility = Visibility.Visible;
        SearchProgressBar.IsIndeterminate = true;
        SearchProgressSummaryText.Text = "正在枚举文件；重复查找不会使用 Windows Search 索引…";
        SearchProgressPathText.Text = root;
        PrimarySearchButtonText.Text = "取消扫描";
        try
        {
            var minimumSize = SelectedDuplicateMinimumSize;
            var reusableFiles = GetReusableFilesForDuplicateSearch(root);
            SearchProgressSummaryText.Text = reusableFiles is null
                ? "正在枚举文件；重复查找不会使用 Windows Search 索引…"
                : $"正在复用最近完整扫描的 {reusableFiles.Count:N0} 项文件清单…";
            var progress = new Progress<DuplicateFileSearchProgress>(snapshot =>
            {
                if (!ReferenceEquals(cancellation, _duplicateSearchCancellation))
                {
                    return;
                }

                SearchProgressSummaryText.Text = snapshot.SummaryText;
                SearchProgressPathText.Text = string.IsNullOrWhiteSpace(snapshot.CurrentPath)
                    ? "正在准备下一项…"
                    : snapshot.CurrentPath;
            });
            var result = await Task.Run(
                () => DuplicateFileFinderService.FindDuplicates(root, minimumSize, reusableFiles, progress, cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            foreach (var group in result.Groups)
            {
                _duplicateGroups.Add(new DuplicateFileGroupViewModel(group, DuplicateSelectionChanged));
            }

            _duplicateSummaryText = result.Groups.Count == 0
                ? $"已扫描 {result.ScannedFiles:N0} 个文件，未发现内容完全相同的文件"
                : $"发现 {result.Groups.Count:N0} 组 / {result.DuplicateFileCount:N0} 个重复文件，可释放 {FormatSize(result.ReclaimableSize)}";
            if (result.SkippedFiles > 0)
            {
                _duplicateSummaryText += $"；跳过 {result.SkippedFiles:N0} 个不可读取或已变化文件";
            }
            if (reusableFiles is not null)
            {
                _duplicateSummaryText += "；已复用最近完整扫描的文件清单";
            }

            SearchSummaryText.Text = _duplicateSummaryText;
            BatchStatusText.Text = result.Groups.Count == 0
                ? "当前没有可生成的清理预览"
                : "请确认每组保留项，再生成清理预览";
            UpdateDuplicateSelectionSummary();
        }
        catch (OperationCanceledException)
        {
            _duplicateSummaryText = "已取消重复文件扫描";
            SearchSummaryText.Text = _duplicateSummaryText;
            BatchStatusText.Text = "扫描已取消；未修改任何文件";
        }
        catch (Exception exception)
        {
            _duplicateSummaryText = $"重复文件扫描失败：{exception.Message}";
            SearchSummaryText.Text = _duplicateSummaryText;
            BatchStatusText.Text = "扫描失败；未修改任何文件";
        }
        finally
        {
            if (ReferenceEquals(cancellation, _duplicateSearchCancellation))
            {
                _duplicateSearchCancellation = null;
                SearchProgressBar.IsIndeterminate = false;
                SearchProgressPanel.Visibility = Visibility.Collapsed;
                PrimarySearchButton.IsEnabled = true;
                PrimarySearchButtonText.Text = _isDuplicateMode ? "查找重复" : "开始搜索";
            }

            cancellation.Dispose();
        }
    }

    private void FileSearchModeButton_Click(object sender, RoutedEventArgs e) => SetWorkbenchMode(false);

    private void DuplicateModeButton_Click(object sender, RoutedEventArgs e) => SetWorkbenchMode(true);

    private void SetWorkbenchMode(bool duplicateMode)
    {
        if (FileSearchModePanel is null || DuplicateModePanel is null)
        {
            return;
        }

        if (_isDuplicateMode != duplicateMode)
        {
            if (duplicateMode)
            {
                _searchCancellation?.Cancel();
            }
            else
            {
                _duplicateSearchCancellation?.Cancel();
            }

            _plans.Clear();
            _previewContext = PreviewContext.None;
        }

        _isDuplicateMode = duplicateMode;
        FileSearchModePanel.Visibility = duplicateMode ? Visibility.Collapsed : Visibility.Visible;
        DuplicateModePanel.Visibility = duplicateMode ? Visibility.Visible : Visibility.Collapsed;
        OperationCard.Visibility = duplicateMode ? Visibility.Collapsed : Visibility.Visible;
        WorkbenchMainColumn.Width = duplicateMode ? new GridLength(1, GridUnitType.Star) : new GridLength(1.15, GridUnitType.Star);
        WorkbenchGapColumn.Width = duplicateMode ? new GridLength(0) : new GridLength(16);
        WorkbenchOperationColumn.Width = duplicateMode ? new GridLength(0) : new GridLength(0.85, GridUnitType.Star);
        FullScanButton.Visibility = duplicateMode ? Visibility.Collapsed : Visibility.Visible;
        DuplicateMinimumSizePanel.Visibility = duplicateMode ? Visibility.Visible : Visibility.Collapsed;
        FullScanColumn.Width = duplicateMode ? new GridLength(230) : new GridLength(104);
        FullScanSpacerColumn.Width = duplicateMode ? new GridLength(12) : new GridLength(8);
        PrimarySearchButtonText.Text = duplicateMode && _duplicateSearchCancellation is not null
            ? "取消扫描"
            : duplicateMode ? "查找重复" : "开始搜索";
        OperationPanelTitleText.Text = "批处理";
        FileSearchModeButton.Style = (Style)FindResource(duplicateMode ? "FileButton" : "FileActionButton");
        DuplicateModeButton.Style = (Style)FindResource(duplicateMode ? "FileActionButton" : "FileButton");
        FileSearchModeButtonText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(duplicateMode ? "#405F7C" : "#FFFFFF"));
        DuplicateModeButtonText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(duplicateMode ? "#FFFFFF" : "#405F7C"));
        SearchSummaryText.Text = duplicateMode ? _duplicateSummaryText : GetCurrentSearchSummaryText();
        if (duplicateMode)
        {
            SetSearchBackendBadge(null, null);
            UpdateDuplicateSelectionSummary();
        }
        else
        {
            UpdateSearchBackendBadge();
            UpdateOperationControls();
        }
    }

    /// <summary>仅复用同目录、无筛选、完整且未截断的近期递归扫描；Windows Search 结果不具备完整覆盖保证。</summary>
    private IReadOnlyList<FileWorkbenchItem>? GetReusableFilesForDuplicateSearch(string root)
    {
        if (_lastSearchBackend != FileSearchBackend.RecursiveScan ||
            _lastSearchWasTruncated ||
            !_lastMatchCountIsExact ||
            !string.IsNullOrWhiteSpace(_lastSearchKeyword) ||
            !string.Equals(_lastSearchTypeFilter, "全部", StringComparison.Ordinal) ||
            _lastSearchModifiedAfter is not null ||
            _lastSearchCompletedAtUtc is null ||
            DateTime.UtcNow - _lastSearchCompletedAtUtc.Value > TimeSpan.FromMinutes(5) ||
            string.IsNullOrWhiteSpace(_lastSearchRoot) ||
            !string.Equals(Path.GetFullPath(root), _lastSearchRoot, StringComparison.OrdinalIgnoreCase) ||
            _allSearchItems.Count != _lastMatchedFiles)
        {
            return null;
        }

        return _allSearchItems.ToArray();
    }

    private void SelectDestinationFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择分类或移动目标文件夹", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath)) DestinationFolderTextBox.Text = Path.GetFullPath(dialog.SelectedPath);
    }

    private void BatchOperationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOperationControls();

    private void SortColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (!Enum.TryParse<FileSortField>((sender as FrameworkElement)?.Tag?.ToString(), out var requestedField))
        {
            return;
        }

        var index = _sortDescriptors.FindIndex(descriptor => descriptor.Field == requestedField);
        if (index < 0)
        {
            // 首次点击添加升序；后续点击在降序与重置之间循环，已存在列的优先级保持不变。
            _sortDescriptors.Add(new FileSortDescriptor(requestedField, true));
        }
        else if (_sortDescriptors[index].Ascending)
        {
            _sortDescriptors[index] = new FileSortDescriptor(requestedField, false);
        }
        else
        {
            _sortDescriptors.RemoveAt(index);
        }

        UpdateSortColumnHeaders();
        ApplyCurrentSort();
    }

    private void ResultFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _allSearchItems.Count == 0 || _searchCancellation is not null)
        {
            return;
        }

        ApplyCurrentFiltersAndSort();
        UpdateSearchSummary();
        if (_fileDeleteSelectionMode)
        {
            UpdateFileDeletePreview();
        }
        else
        {
            _plans.Clear();
            BatchStatusText.Text = _resultItems.Count == 0
                ? "当前已加载结果中没有符合筛选条件的文件"
                : "已按当前条件筛选；选择文件后生成操作预览";
        }
    }

    /// <summary>由存储分析显式跳转进来；页面显示后立即检索，已知的大文件直接定位，避免再次全盘扫描。</summary>
    public async Task OpenFolderFromStorageAsync(string folderPath, string? searchKeyword, string? knownFilePath)
    {
        SetWorkbenchMode(false);
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            SearchSummaryText.Text = "存储分析中的目录当前不可用";
            return;
        }

        SourceFolderTextBox.Text = Path.GetFullPath(folderPath);
        KeywordTextBox.Text = searchKeyword ?? string.Empty;
        TypeFilterComboBox.SelectedIndex = 0;
        _sortDescriptors.Clear();
        _sortDescriptors.Add(new FileSortDescriptor(FileSortField.Size, false));
        UpdateSortColumnHeaders();

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        ClearSearchResults();
        _plans.Clear();
        SearchSummaryText.Text = "正在接收存储分析结果…";
        BatchStatusText.Text = "等待搜索结果…";
        SearchProgressPanel.Visibility = Visibility.Collapsed;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        if (!string.IsNullOrWhiteSpace(knownFilePath) && File.Exists(knownFilePath))
        {
            try
            {
                _lastMatchedFiles = 1;
                _lastSearchWasTruncated = false;
                _lastMatchCountIsExact = true;
                _lastSearchBackend = null;
                _lastSearchFallbackReason = null;
                _lastSearchMode = null;
                SetSearchResults(new[] { FileWorkbenchItem.Create(new FileInfo(knownFilePath)) });
                SetSearchBackendBadge("存储分析定位", "结果由存储空间分析直接定位，无需再次搜索。");
                SearchSummaryText.Text = "已从存储分析定位 1 个文件";
                if (_fileDeleteSelectionMode)
                {
                    UpdateFileDeletePreview();
                }
                else
                {
                    BatchStatusText.Text = "选择文件后生成操作预览";
                }
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SearchSummaryText.Text = $"定位文件失败：{exception.Message}";
                BatchStatusText.Text = "可修改条件后重新搜索";
                return;
            }
        }

        await SearchAsync();
    }

    private void UpdateOperationControls()
    {
        if (!IsLoaded || RenamePrefixTextBox is null || RenameNumberRow is null ||
            NewExtensionTextBox is null || DestinationRow is null)
        {
            return;
        }

        var operation = SelectedOperation;
        var deleteSelectionMode = operation == FileBatchOperation.PermanentDelete;
        if (_fileDeleteSelectionMode != deleteSelectionMode)
        {
            foreach (var item in _allSearchItems)
            {
                item.IsSelectedForDeletion = false;
            }

            FileResultsListBox.SelectedItems.Clear();
            _plans.Clear();
            _previewContext = PreviewContext.None;
            _fileDeleteSelectionMode = deleteSelectionMode;
        }

        FileResultsListBox.Tag = deleteSelectionMode;
        FileDeleteHeaderSpacer.Visibility = deleteSelectionMode ? Visibility.Visible : Visibility.Collapsed;
        RenamePrefixTextBox.Visibility = operation == FileBatchOperation.Rename ? Visibility.Visible : Visibility.Collapsed;
        RenameNumberRow.Visibility = operation == FileBatchOperation.Rename ? Visibility.Visible : Visibility.Collapsed;
        NewExtensionTextBox.Visibility = operation == FileBatchOperation.ChangeExtension ? Visibility.Visible : Visibility.Collapsed;
        DestinationRow.Visibility = operation is FileBatchOperation.Classify or FileBatchOperation.Move ? Visibility.Visible : Visibility.Collapsed;
        GeneratePreviewButton.Visibility = deleteSelectionMode ? Visibility.Collapsed : Visibility.Visible;
        ExecuteBatchButton.Style = (Style)FindResource(deleteSelectionMode ? "FileDangerButton" : "FileActionButton");
        ExecuteBatchButtonText.Text = deleteSelectionMode ? "删除文件" : "确认执行";
        if (deleteSelectionMode)
        {
            UpdateFileDeletePreview();
        }
        else
        {
            _plans.Clear();
            _previewContext = PreviewContext.None;
            ExecuteBatchButton.IsEnabled = true;
            BatchStatusText.Text = "选择文件后生成操作预览";
        }
    }

    private void GeneratePreview_Click(object sender, RoutedEventArgs e)
    {
        var targets = FileResultsListBox.SelectedItems.Cast<FileWorkbenchItem>().ToArray();
        var operation = SelectedOperation;
        if (operation == FileBatchOperation.PermanentDelete)
        {
            UpdateFileDeletePreview();
            return;
        }

        if (targets.Length == 0) targets = _resultItems.ToArray();
        if (targets.Length == 0)
        {
            BatchStatusText.Text = "没有可处理的文件";
            return;
        }

        var destination = DestinationFolderTextBox.Text.Trim();
        if (operation is FileBatchOperation.Classify or FileBatchOperation.Move)
        {
            if (!Directory.Exists(destination)) { BatchStatusText.Text = "请先选择有效目标文件夹"; return; }
        }
        if (operation == FileBatchOperation.Rename && string.IsNullOrWhiteSpace(RenamePrefixTextBox.Text)) { BatchStatusText.Text = "请填写命名前缀"; return; }
        var numberDigits = 3;
        if (operation == FileBatchOperation.Rename && !TryParseNumberDigits(NumberDigitsTextBox.Text, out numberDigits))
        {
            BatchStatusText.Text = "编号位数请输入 1-6；例如 3 会生成 001、002。";
            return;
        }
        try
        {
            _plans.Clear();
            foreach (var plan in FileWorkbenchService.CreatePlans(targets, operation, RenamePrefixTextBox.Text.Trim(), ParsePositiveInt(StartNumberTextBox.Text, 1), numberDigits, NewExtensionTextBox.Text, destination)) _plans.Add(plan);
            _previewContext = PreviewContext.StandardBatch;
            BatchStatusText.Text = $"已生成 {_plans.Count} 项预览；确认后才会修改文件";
        }
        catch (Exception exception) { BatchStatusText.Text = $"无法生成预览：{exception.Message}"; }
    }

    private void FileDeleteSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_fileDeleteSelectionMode)
        {
            UpdateFileDeletePreview();
        }
    }

    /// <summary>只把用户通过玻璃复选框明确勾选的文件加入永久删除预览。</summary>
    private void UpdateFileDeletePreview()
    {
        if (!_fileDeleteSelectionMode)
        {
            return;
        }

        var selectedItems = _allSearchItems.Where(item => item.IsSelectedForDeletion).ToArray();
        _plans.Clear();
        foreach (var plan in FileWorkbenchService.CreatePlans(
                     selectedItems,
                     FileBatchOperation.PermanentDelete,
                     string.Empty,
                     1,
                     1,
                     string.Empty,
                     string.Empty))
        {
            _plans.Add(plan);
        }

        _previewContext = selectedItems.Length == 0 ? PreviewContext.None : PreviewContext.StandardBatch;
        ExecuteBatchButton.IsEnabled = selectedItems.Length > 0;
        BatchStatusText.Text = selectedItems.Length == 0
            ? "请在左侧勾选要永久删除的文件；未勾选的文件不会被处理"
            : $"已勾选 {selectedItems.Length:N0} 个文件，共 {FormatSize(selectedItems.Sum(item => item.Size))}；右侧为永久删除预览";
    }

    private async void DeleteSelectedDuplicates_Click(object sender, RoutedEventArgs e)
    {
        var invalidGroup = _duplicateGroups.FirstOrDefault(group =>
            group.Entries.Count > 0 && group.Entries.All(entry => entry.IsSelectedForDeletion));
        if (invalidGroup is not null)
        {
            DuplicateSelectionSummaryText.Text = "每组至少保留一个文件；当前有一组被全部选中。";
            return;
        }

        var selectedEntries = _duplicateGroups
            .SelectMany(group => group.Entries)
            .Where(entry => entry.IsSelectedForDeletion)
            .ToArray();
        if (selectedEntries.Length == 0)
        {
            DuplicateSelectionSummaryText.Text = "请先勾选需要永久删除的重复副本。";
            return;
        }

        var selectedSize = selectedEntries.Sum(entry => entry.Size);
        var confirmation = MessageBox.Show(
            $"将永久删除 {selectedEntries.Length:N0} 个重复文件，合计 {FormatSize(selectedSize)}。\n\n这些文件不会进入回收站，删除后无法通过 X-Tool 恢复。是否继续？",
            "确认永久删除重复文件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var items = selectedEntries.Select(entry => new FileWorkbenchItem(
            entry.FullPath,
            entry.FileName,
            Path.GetExtension(entry.FullPath),
            entry.Size,
            entry.ModifiedAt));
        var plans = FileWorkbenchService.CreatePlans(
            items,
            FileBatchOperation.PermanentDelete,
            string.Empty,
            1,
            1,
            string.Empty,
            string.Empty);
        DuplicateDeleteButton.IsEnabled = false;
        DuplicateSelectionSummaryText.Text = $"正在永久删除 {plans.Count:N0} 个文件…";
        var result = await Task.Run(() => FileWorkbenchService.Execute(plans));
        RemoveCompletedDeletedFiles(result.CompletedPaths);
        DuplicateDeleteButton.IsEnabled = _duplicateGroups
            .SelectMany(group => group.Entries)
            .Any(entry => entry.IsSelectedForDeletion);
        DuplicateSelectionSummaryText.Text = result.Failures.Count == 0
            ? $"已永久删除 {result.CompletedCount:N0} 个文件，释放 {FormatSize(selectedSize)}。"
            : $"已永久删除 {result.CompletedCount:N0} 个文件，失败 {result.Failures.Count:N0} 个：{string.Join("；", result.Failures.Take(3))}";
    }

    private void KeepDuplicateEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DuplicateFileEntryViewModel entry)
        {
            entry.Group.SelectAllExcept(entry);
        }
    }

    private async void ExecuteBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_plans.Count == 0) { BatchStatusText.Text = _fileDeleteSelectionMode ? "请先勾选要删除的文件" : "请先生成操作预览"; return; }
        var isDelete = _plans.All(plan => plan.Operation == FileBatchOperation.PermanentDelete);
        var confirmationText = isDelete
            ? $"将永久删除 {_plans.Count:N0} 个文件，合计 {FormatSize(_plans.Sum(plan => plan.Size))}。\n\n这些文件不会进入回收站，删除后无法通过 X-Tool 恢复。是否继续？"
            : $"将执行 {_plans.Count} 项文件操作。\n\n此操作会移动、重命名或修改后缀，是否继续？";
        var confirmation = MessageBox.Show(confirmationText, isDelete ? "确认永久删除文件" : "确认文件批处理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;
        BatchStatusText.Text = isDelete ? "正在永久删除…" : "正在执行…";
        ExecuteBatchButton.IsEnabled = false;
        var executingPlans = _plans.ToArray();
        var result = await Task.Run(() => FileWorkbenchService.Execute(executingPlans));
        if (isDelete)
        {
            RemoveCompletedDeletedFiles(result.CompletedPaths);
            UpdateFileDeletePreview();
            BatchStatusText.Text = result.Failures.Count == 0
                ? $"已永久删除 {result.CompletedCount:N0} 个文件"
                : $"已永久删除 {result.CompletedCount:N0} 个文件，失败 {result.Failures.Count:N0} 个：{string.Join("；", result.Failures.Take(3))}";
            return;
        }

        ExecuteBatchButton.IsEnabled = true;
        BatchStatusText.Text = result.Failures.Count == 0
            ? $"已完成 {result.CompletedCount} 项操作"
            : $"完成 {result.CompletedCount} 项，失败 {result.Failures.Count} 项：{string.Join("；", result.Failures.Take(3))}";

        await SearchAsync();
    }

    private void ApplyCurrentSort()
    {
        if (_allSearchItems.Count == 0)
        {
            return;
        }

        ApplyCurrentFiltersAndSort();

        if (_fileDeleteSelectionMode)
        {
            UpdateFileDeletePreview();
        }
        else
        {
            _plans.Clear();
            BatchStatusText.Text = "已按当前表头排序；选择文件后生成操作预览";
        }
    }

    private void DuplicateSelectionChanged()
    {
        _plans.Clear();
        _previewContext = PreviewContext.None;
        UpdateDuplicateSelectionSummary();
        BatchStatusText.Text = "选择已更新；请重新生成清理预览";
    }

    private void UpdateDuplicateSelectionSummary()
    {
        if (DuplicateSelectionSummaryText is null)
        {
            return;
        }

        var selectedEntries = _duplicateGroups
            .SelectMany(group => group.Entries)
            .Where(entry => entry.IsSelectedForDeletion)
            .ToArray();
        DuplicateSelectionSummaryText.Text = selectedEntries.Length == 0
            ? "默认不选择任何文件；请先确认每组需要保留的副本。"
            : $"已选择 {selectedEntries.Length:N0} 个文件，永久删除后可释放 {FormatSize(selectedEntries.Sum(entry => entry.Size))}";
        DuplicateDeleteButton.IsEnabled = selectedEntries.Length > 0 && _duplicateSearchCancellation is null;
    }

    private void RemoveCompletedDeletedFiles(IReadOnlyList<string> completedPaths)
    {
        if (completedPaths.Count == 0)
        {
            return;
        }

        var completed = new HashSet<string>(completedPaths, StringComparer.OrdinalIgnoreCase);
        _allSearchItems.RemoveAll(item => completed.Contains(item.FullPath));
        if (_allSearchItems.Count > 0 || !_isDuplicateMode)
        {
            ApplyCurrentFiltersAndSort();
            UpdateSearchSummary();
        }

        foreach (var group in _duplicateGroups.ToArray())
        {
            group.RemovePaths(completed);
            if (group.Entries.Count < 2)
            {
                _duplicateGroups.Remove(group);
            }
        }

        if (_isDuplicateMode)
        {
            var remainingFiles = _duplicateGroups.Sum(group => group.Entries.Count);
            var remainingReclaimable = _duplicateGroups.Sum(group => group.ReclaimableSize);
            _duplicateSummaryText = _duplicateGroups.Count == 0
                ? "已清理所选副本，当前结果中不再有重复组"
                : $"剩余 {_duplicateGroups.Count:N0} 组 / {remainingFiles:N0} 个重复文件，可释放 {FormatSize(remainingReclaimable)}";
            SearchSummaryText.Text = _duplicateSummaryText;
            UpdateDuplicateSelectionSummary();
        }
    }

    /// <summary>替换最近一次扫描的结果后，按当前类型、时间与多列排序规则刷新可见分页。</summary>
    private void SetSearchResults(IEnumerable<FileWorkbenchItem> items)
    {
        _allSearchItems.Clear();
        _allSearchItems.AddRange(items);
        ApplyCurrentFiltersAndSort();
    }

    private void ApplyCurrentFiltersAndSort()
    {
        var typeFilter = SelectedTypeFilter;
        var modifiedAfter = SelectedModifiedAfter;
        var filteredItems = _allSearchItems.Where(item =>
            (string.Equals(typeFilter, "全部", StringComparison.Ordinal) ||
             string.Equals(FileWorkbenchService.GetCategory(item.Extension), typeFilter, StringComparison.Ordinal)) &&
            (modifiedAfter is null || item.ModifiedAt >= modifiedAfter.Value));
        var sortedItems = FileWorkbenchService.SortResults(filteredItems, GetEffectiveSortDescriptors());
        _resultItems.Clear();
        _resultItems.AddRange(sortedItems);
        _currentResultPage = 0;
        RenderCurrentResultPage();
    }

    private void ClearSearchResults()
    {
        _allSearchItems.Clear();
        _resultItems.Clear();
        _currentResultPage = 0;
        _lastMatchedFiles = 0;
        _lastSearchWasTruncated = false;
        _lastMatchCountIsExact = true;
        _lastSearchBackend = null;
        _lastSearchFallbackReason = null;
        _lastSearchMode = null;
        _lastSearchRoot = null;
        _lastSearchKeyword = null;
        _lastSearchTypeFilter = null;
        _lastSearchModifiedAfter = null;
        _lastSearchCompletedAtUtc = null;
        _visibleItems.ReplaceWith(Array.Empty<FileWorkbenchItem>());
        UpdateResultPageControls();
        UpdateSearchBackendBadge();
    }

    private void PreviousResultPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentResultPage <= 0)
        {
            return;
        }

        _currentResultPage--;
        RenderCurrentResultPage();
    }

    private void NextResultPage_Click(object sender, RoutedEventArgs e)
    {
        var pageCount = GetResultPageCount();
        if (_currentResultPage >= pageCount - 1)
        {
            return;
        }

        _currentResultPage++;
        RenderCurrentResultPage();
    }

    private void RenderCurrentResultPage()
    {
        var pageCount = GetResultPageCount();
        _currentResultPage = Math.Clamp(_currentResultPage, 0, Math.Max(pageCount - 1, 0));
        var pageStart = _currentResultPage * ResultPageSize;
        _visibleItems.ReplaceWith(_resultItems.Skip(pageStart).Take(ResultPageSize));
        if (_visibleItems.FirstOrDefault() is { } firstItem)
        {
            FileResultsListBox.ScrollIntoView(firstItem);
        }
        UpdateResultPageControls();
    }

    private int GetResultPageCount() => _resultItems.Count == 0
        ? 0
        : (int)Math.Ceiling(_resultItems.Count / (double)ResultPageSize);

    private void UpdateResultPageControls()
    {
        if (ResultPaginationPanel is null)
        {
            return;
        }

        var pageCount = GetResultPageCount();
        var start = _resultItems.Count == 0 ? 0 : _currentResultPage * ResultPageSize + 1;
        var end = Math.Min((_currentResultPage + 1) * ResultPageSize, _resultItems.Count);
        ResultPaginationPanel.Visibility = _resultItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ResultPageSummaryText.Text = _resultItems.Count == 0 ? string.Empty : $"显示 {start:N0}–{end:N0} / {_resultItems.Count:N0}";
        PreviousResultPageButton.IsEnabled = _currentResultPage > 0;
        NextResultPageButton.IsEnabled = _currentResultPage < pageCount - 1;
    }

    private void UpdateSortColumnHeaders()
    {
        UpdateSortIndicator(NameSortIndicatorText, FileSortField.Name);
        UpdateSortIndicator(ExtensionSortIndicatorText, FileSortField.Extension);
        UpdateSortIndicator(SizeSortIndicatorText, FileSortField.Size);
        UpdateSortIndicator(ModifiedSortIndicatorText, FileSortField.Modified);
    }

    private void UpdateSortIndicator(TextBlock indicator, FileSortField field)
    {
        var index = _sortDescriptors.FindIndex(descriptor => descriptor.Field == field);
        indicator.Text = index < 0
            ? string.Empty
            : $"{(_sortDescriptors[index].Ascending ? "↑" : "↓")}{index + 1}";
    }

    private void UpdateSearchSummary()
    {
        SearchSummaryText.Text = GetCurrentSearchSummaryText();
    }

    private string GetCurrentSearchSummaryText()
    {
        if (!Directory.Exists(SourceFolderTextBox?.Text ?? string.Empty) && _allSearchItems.Count == 0)
        {
            return "请选择文件夹";
        }

        if (_lastSearchWasTruncated)
        {
            var matchedText = _lastMatchCountIsExact
                ? $"匹配 {_lastMatchedFiles:N0} 个文件"
                : $"匹配至少 {_lastMatchedFiles:N0} 个文件";
            return $"{matchedText}；当前筛选显示 {_resultItems.Count:N0} / {_allSearchItems.Count:N0}";
        }

        return _allSearchItems.Count == 0
            ? "未找到符合条件的文件"
            : $"当前筛选显示 {_resultItems.Count:N0} 个文件";
    }

    private void UpdateSearchBackendBadge()
    {
        if (_lastSearchBackend is null)
        {
            SetSearchBackendBadge(null, null);
            return;
        }

        var isFullScan = _lastSearchMode == FileSearchMode.FullRecursiveScan;
        var text = _lastSearchBackend == FileSearchBackend.WindowsSearch
            ? "Windows Search"
            : isFullScan ? "完整递归扫描" : "本地递归扫描";
        var toolTip = _lastSearchBackend == FileSearchBackend.WindowsSearch
            ? "结果来自 Windows Search 已建立的系统索引。"
            : isFullScan
                ? "按你的要求绕过 Windows Search，递归扫描当前文件夹中可访问的文件。"
                : $"已自动回退到本地递归扫描：{_lastSearchFallbackReason ?? "Windows Search 当前不可用"}";
        SetSearchBackendBadge(text, toolTip);
    }

    private void SetSearchBackendBadge(string? text, string? toolTip)
    {
        if (SearchBackendBadge is null || SearchBackendText is null)
        {
            return;
        }

        SearchBackendBadge.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        SearchBackendBadge.ToolTip = toolTip;
        SearchBackendText.Text = text ?? string.Empty;
    }

    private void KeywordTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SearchAsync();
        }
    }

    private void KeywordTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (KeywordPlaceholderText is not null)
        {
            KeywordPlaceholderText.Visibility = string.IsNullOrWhiteSpace(KeywordTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private FileBatchOperation SelectedOperation => Enum.TryParse<FileBatchOperation>((BatchOperationComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var operation) ? operation : FileBatchOperation.Rename;
    private string SelectedTypeFilter => (TypeFilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";
    private DateTime? SelectedModifiedAfter => int.TryParse((ModifiedFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var days)
        ? DateTime.Now.AddDays(-days)
        : null;
    private long SelectedDuplicateMinimumSize => long.TryParse(
        (DuplicateMinimumSizeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
        out var minimumSize)
        ? Math.Max(0, minimumSize)
        : 1024L * 1024;
    private IReadOnlyList<FileSortDescriptor> GetEffectiveSortDescriptors() => _sortDescriptors.Count == 0
        ? new[] { new FileSortDescriptor(FileSortField.Name, true) }
        : _sortDescriptors.ToArray();
    private static int ParsePositiveInt(string text, int fallback) => int.TryParse(text, out var value) && value > 0 ? value : fallback;
    private static bool TryParseNumberDigits(string text, out int value) => int.TryParse(text, out value) && value is >= 1 and <= 6;
    private static string FormatSize(long size) => size >= 1024L * 1024 * 1024
        ? $"{size / 1024d / 1024d / 1024d:F2} GB"
        : size >= 1024L * 1024
            ? $"{size / 1024d / 1024d:F2} MB"
            : size >= 1024
                ? $"{size / 1024d:F1} KB"
                : $"{size:N0} B";
    private void SourceFolderTextBox_TextChanged(object sender, TextChangedEventArgs e) { if (SearchSummaryText is not null && !Directory.Exists(SourceFolderTextBox.Text ?? string.Empty)) SearchSummaryText.Text = "请选择有效文件夹"; }
}

internal enum PreviewContext
{
    None,
    StandardBatch
}

internal sealed class DuplicateFileGroupViewModel : INotifyPropertyChanged
{
    private readonly Action _selectionChanged;

    internal DuplicateFileGroupViewModel(DuplicateFileGroup group, Action selectionChanged)
    {
        _selectionChanged = selectionChanged;
        Entries = new ObservableCollection<DuplicateFileEntryViewModel>(
            group.Entries.Select(entry => new DuplicateFileEntryViewModel(entry, this, selectionChanged)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<DuplicateFileEntryViewModel> Entries { get; }
    public string TitleText => $"{Entries.Count:N0} 个内容完全相同的文件";
    public long ReclaimableSize => Entries.Count <= 1 ? 0 : (Entries.Count - 1) * Entries[0].Size;
    public string SummaryText => Entries.Count == 0
        ? string.Empty
        : $"单个 {FormatSize(Entries[0].Size)} · 最多释放 {FormatSize(ReclaimableSize)}";

    internal void SelectAllExcept(DuplicateFileEntryViewModel entryToKeep)
    {
        foreach (var entry in Entries)
        {
            entry.SetSelectedWithoutNotification(!ReferenceEquals(entry, entryToKeep));
        }

        _selectionChanged();
    }

    internal void RemovePaths(ISet<string> paths)
    {
        var removed = false;
        foreach (var entry in Entries.Where(entry => paths.Contains(entry.FullPath)).ToArray())
        {
            Entries.Remove(entry);
            removed = true;
        }

        if (removed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReclaimableSize)));
        }
    }

    private static string FormatSize(long size) => size >= 1024L * 1024 * 1024
        ? $"{size / 1024d / 1024d / 1024d:F2} GB"
        : size >= 1024L * 1024
            ? $"{size / 1024d / 1024d:F2} MB"
            : $"{size / 1024d:F1} KB";
}

internal sealed class DuplicateFileEntryViewModel : INotifyPropertyChanged
{
    private readonly Action _selectionChanged;
    private bool _isSelectedForDeletion;

    internal DuplicateFileEntryViewModel(
        DuplicateFileEntry entry,
        DuplicateFileGroupViewModel group,
        Action selectionChanged)
    {
        FullPath = entry.FullPath;
        FileName = entry.FileName;
        Size = entry.Size;
        ModifiedAt = entry.ModifiedAt;
        FileIcon = FileTypeIconProvider.GetIcon(entry.FullPath);
        Group = group;
        _selectionChanged = selectionChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public DuplicateFileGroupViewModel Group { get; }
    public string FullPath { get; }
    public string FileName { get; }
    public long Size { get; }
    public DateTime ModifiedAt { get; }
    public ImageSource? FileIcon { get; }
    public string DetailText => $"{FullPath} · {ModifiedAt:yyyy-MM-dd HH:mm}";
    public string SizeText => Size >= 1024L * 1024 * 1024
        ? $"{Size / 1024d / 1024d / 1024d:F2} GB"
        : Size >= 1024L * 1024
            ? $"{Size / 1024d / 1024d:F2} MB"
            : $"{Size / 1024d:F1} KB";
    public string ModifiedText => ModifiedAt.ToString("yyyy-MM-dd");

    public bool IsSelectedForDeletion
    {
        get => _isSelectedForDeletion;
        set
        {
            if (_isSelectedForDeletion == value)
            {
                return;
            }

            _isSelectedForDeletion = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedForDeletion)));
            _selectionChanged();
        }
    }

    internal void SetSelectedWithoutNotification(bool value)
    {
        if (_isSelectedForDeletion == value)
        {
            return;
        }

        _isSelectedForDeletion = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedForDeletion)));
    }
}

/// <summary>一次性替换可见分页，避免逐项通知触发大量 WPF 布局与绑定计算。</summary>
internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceWith(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
