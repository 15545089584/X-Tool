using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.FileWorkbench;

/// <summary>文件工作台页面：搜索结果与待执行操作始终分离，避免误操作。</summary>
public partial class FileWorkbenchView : UserControl
{
    private readonly ObservableCollection<FileWorkbenchItem> _items = new();
    private readonly ObservableCollection<FileOperationPlan> _plans = new();
    private CancellationTokenSource? _searchCancellation;
    private bool _sortAscending = true;
    private FileSortField _selectedSortField = FileSortField.Name;

    public FileWorkbenchView()
    {
        InitializeComponent();
        FileResultsListBox.ItemsSource = _items;
        PreviewListBox.ItemsSource = _plans;
        UpdateSortColumnHeaders();
        Loaded += (_, _) => UpdateOperationControls();
    }

    private void SelectSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择需要搜索和整理的文件夹", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        SourceFolderTextBox.Text = Path.GetFullPath(dialog.SelectedPath);
        _ = SearchAsync();
    }

    private void OpenSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(SourceFolderTextBox.Text)) Process.Start(new ProcessStartInfo(SourceFolderTextBox.Text) { UseShellExecute = true });
    }

    private async void SearchFiles_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async Task SearchAsync()
    {
        var root = SourceFolderTextBox.Text.Trim();
        if (!Directory.Exists(root)) { SearchSummaryText.Text = "请选择有效文件夹"; return; }
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var cancellation = _searchCancellation;
        _items.Clear();
        _plans.Clear();
        SearchSummaryText.Text = "正在搜索…";
        SearchProgressPanel.Visibility = Visibility.Visible;
        SearchProgressBar.IsIndeterminate = true;
        SearchProgressSummaryText.Text = "正在准备本地文件搜索…";
        SearchProgressPathText.Text = root;
        try
        {
            var type = (TypeFilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";
            var days = (ModifiedFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            DateTime? modifiedAfter = int.TryParse(days, out var value) ? DateTime.Now.AddDays(-value) : null;
            // 必须在 UI 线程先读取筛选条件；后台扫描不能直接访问 WPF 控件。
            var keyword = KeywordTextBox.Text.Trim();
            var sortField = SelectedSortField;
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
                () => FileWorkbenchService.Search(root, keyword, type, modifiedAfter, sortField, _sortAscending, progress, cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            SearchProgressSummaryText.Text = "扫描完成，正在整理可显示结果…";
            _items.Clear();
            foreach (var item in searchResult.Items)
            {
                _items.Add(item);
            }
            _plans.Clear();
            SearchSummaryText.Text = searchResult.IsTruncated
                ? $"匹配 {searchResult.MatchedFiles:N0} 个文件；为保持流畅，显示前 {_items.Count:N0} 个"
                : $"找到 {_items.Count:N0} 个文件";
            BatchStatusText.Text = searchResult.IsTruncated
                ? "结果较多；可用关键词、类型或时间范围缩小搜索后再批处理"
                : "选择文件后生成操作预览";
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

        _sortAscending = _selectedSortField == requestedField
            ? !_sortAscending
            : requestedField is FileSortField.Name or FileSortField.Extension;
        _selectedSortField = requestedField;
        UpdateSortColumnHeaders();
        ApplyCurrentSort();
    }

    /// <summary>由存储分析显式跳转进来；页面显示后立即检索，已知的大文件直接定位，避免再次全盘扫描。</summary>
    public async Task OpenFolderFromStorageAsync(string folderPath, string? searchKeyword, string? knownFilePath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            SearchSummaryText.Text = "存储分析中的目录当前不可用";
            return;
        }

        SourceFolderTextBox.Text = Path.GetFullPath(folderPath);
        KeywordTextBox.Text = searchKeyword ?? string.Empty;
        TypeFilterComboBox.SelectedIndex = 0;
        _selectedSortField = FileSortField.Size;
        _sortAscending = false;
        UpdateSortColumnHeaders();

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        _items.Clear();
        _plans.Clear();
        SearchSummaryText.Text = "正在接收存储分析结果…";
        BatchStatusText.Text = "等待搜索结果…";
        SearchProgressPanel.Visibility = Visibility.Collapsed;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        if (!string.IsNullOrWhiteSpace(knownFilePath) && File.Exists(knownFilePath))
        {
            try
            {
                _items.Add(FileWorkbenchItem.Create(new FileInfo(knownFilePath)));
                SearchSummaryText.Text = "已从存储分析定位 1 个文件";
                BatchStatusText.Text = "选择文件后生成操作预览";
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
        RenamePrefixTextBox.Visibility = operation == FileBatchOperation.Rename ? Visibility.Visible : Visibility.Collapsed;
        RenameNumberRow.Visibility = operation == FileBatchOperation.Rename ? Visibility.Visible : Visibility.Collapsed;
        NewExtensionTextBox.Visibility = operation == FileBatchOperation.ChangeExtension ? Visibility.Visible : Visibility.Collapsed;
        DestinationRow.Visibility = operation is FileBatchOperation.Classify or FileBatchOperation.Move ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GeneratePreview_Click(object sender, RoutedEventArgs e)
    {
        var targets = FileResultsListBox.SelectedItems.Cast<FileWorkbenchItem>().ToArray();
        if (targets.Length == 0) targets = _items.ToArray();
        if (targets.Length == 0) { BatchStatusText.Text = "没有可处理的文件"; return; }
        var operation = SelectedOperation;
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
            BatchStatusText.Text = $"已生成 {_plans.Count} 项预览；确认后才会修改文件";
        }
        catch (Exception exception) { BatchStatusText.Text = $"无法生成预览：{exception.Message}"; }
    }

    private async void ExecuteBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_plans.Count == 0) { BatchStatusText.Text = "请先生成操作预览"; return; }
        var confirmation = MessageBox.Show($"将执行 {_plans.Count} 项文件操作。\n\n此操作会移动、重命名或修改后缀，是否继续？", "确认文件批处理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;
        BatchStatusText.Text = "正在执行…";
        var result = await Task.Run(() => FileWorkbenchService.Execute(_plans));
        BatchStatusText.Text = result.Failures.Count == 0 ? $"已完成 {result.CompletedCount} 项操作" : $"完成 {result.CompletedCount} 项，失败 {result.Failures.Count} 项";
        await SearchAsync();
    }

    private void ApplyCurrentSort()
    {
        if (_items.Count == 0)
        {
            return;
        }

        var sortedItems = FileWorkbenchService.SortResults(_items, _selectedSortField, _sortAscending);
        _items.Clear();
        foreach (var item in sortedItems)
        {
            _items.Add(item);
        }

        _plans.Clear();
        BatchStatusText.Text = "已按当前表头排序；选择文件后生成操作预览";
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
        indicator.Text = _selectedSortField == field ? (_sortAscending ? "↑" : "↓") : string.Empty;
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
    private FileSortField SelectedSortField => _selectedSortField;
    private static int ParsePositiveInt(string text, int fallback) => int.TryParse(text, out var value) && value > 0 ? value : fallback;
    private static bool TryParseNumberDigits(string text, out int value) => int.TryParse(text, out value) && value is >= 1 and <= 6;
    private void SourceFolderTextBox_TextChanged(object sender, TextChangedEventArgs e) { if (SearchSummaryText is not null && !Directory.Exists(SourceFolderTextBox.Text ?? string.Empty)) SearchSummaryText.Text = "请选择有效文件夹"; }
}
