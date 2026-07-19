using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.FileWorkbench;

/// <summary>文件工作台页面：搜索结果与待执行操作始终分离，避免误操作。</summary>
public partial class FileWorkbenchView : UserControl
{
    private readonly ObservableCollection<FileWorkbenchItem> _items = new();
    private readonly ObservableCollection<FileOperationPlan> _plans = new();
    private CancellationTokenSource? _searchCancellation;
    private bool _sortAscending = true;

    public FileWorkbenchView()
    {
        InitializeComponent();
        FileResultsListBox.ItemsSource = _items;
        PreviewListBox.ItemsSource = _plans;
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
        SearchSummaryText.Text = "正在搜索…";
        try
        {
            var type = (TypeFilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";
            var days = (ModifiedFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            DateTime? modifiedAfter = int.TryParse(days, out var value) ? DateTime.Now.AddDays(-value) : null;
            // 必须在 UI 线程先读取筛选条件；后台扫描不能直接访问 WPF 控件。
            var keyword = KeywordTextBox.Text.Trim();
            var sortField = SelectedSortField;
            var results = await Task.Run(
                () => FileWorkbenchService.Search(root, keyword, type, modifiedAfter, sortField, _sortAscending, cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            _items.Clear(); foreach (var item in results) _items.Add(item);
            _plans.Clear();
            SearchSummaryText.Text = $"找到 {_items.Count:N0} 个文件";
            BatchStatusText.Text = "选择文件后生成操作预览";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { SearchSummaryText.Text = $"搜索失败：{exception.Message}"; }
    }

    private void SelectDestinationFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择分类或移动目标文件夹", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath)) DestinationFolderTextBox.Text = Path.GetFullPath(dialog.SelectedPath);
    }

    private void BatchOperationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOperationControls();

    private void SortFieldComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && Directory.Exists(SourceFolderTextBox.Text)) _ = SearchAsync();
    }

    private void SortDirectionButton_Click(object sender, RoutedEventArgs e)
    {
        _sortAscending = !_sortAscending;
        SortDirectionButton.Content = _sortAscending ? "↑" : "↓";
        SortDirectionButton.ToolTip = _sortAscending ? "当前从小到大，点击改为从大到小" : "当前从大到小，点击改为从小到大";
        if (Directory.Exists(SourceFolderTextBox.Text)) _ = SearchAsync();
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

    private FileBatchOperation SelectedOperation => Enum.TryParse<FileBatchOperation>((BatchOperationComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var operation) ? operation : FileBatchOperation.Rename;
    private FileSortField SelectedSortField => Enum.TryParse<FileSortField>((SortFieldComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var field) ? field : FileSortField.Name;
    private static int ParsePositiveInt(string text, int fallback) => int.TryParse(text, out var value) && value > 0 ? value : fallback;
    private static bool TryParseNumberDigits(string text, out int value) => int.TryParse(text, out value) && value is >= 1 and <= 6;
    private void SourceFolderTextBox_TextChanged(object sender, TextChangedEventArgs e) { if (SearchSummaryText is not null && !Directory.Exists(SourceFolderTextBox.Text ?? string.Empty)) SearchSummaryText.Text = "请选择有效文件夹"; }
}
