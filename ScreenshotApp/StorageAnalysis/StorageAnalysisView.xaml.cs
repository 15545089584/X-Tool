using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenshotApp.StorageAnalysis;

/// <summary>存储分析页面只在用户打开指定卷后运行；已完成的结果在本次程序运行期间保留。</summary>
public partial class StorageAnalysisView : UserControl
{
    private readonly ObservableCollection<StorageDirectoryUsage> _directoryUsages = new();
    private readonly ObservableCollection<StorageLargeFile> _largeFiles = new();
    private readonly Stack<StorageAnalysisTarget> _treemapHistory = new();
    private CancellationTokenSource? _scanCancellation;
    private StorageAnalysisTarget? _target;
    private StorageAnalysisResult? _currentResult;
    private bool _isScanning;
    private StorageResultViewMode _viewMode = StorageResultViewMode.List;

    public StorageAnalysisView()
    {
        InitializeComponent();
        DirectoryUsageItems.ItemsSource = _directoryUsages;
        LargeFilesListBox.ItemsSource = _largeFiles;
        TreemapControl.ItemInvoked += TreemapControl_ItemInvoked;
        UpdateViewMode();
    }

    public event EventHandler? BackRequested;
    public event EventHandler<FileWorkbenchNavigationRequestedEventArgs>? FileWorkbenchRequested;

    public async Task StartAnalysisAsync(StorageAnalysisTarget target)
    {
        _treemapHistory.Clear();
        _target = target;
        _currentResult = null;
        await AnalyzeAsync(target, forceRefresh: false);
    }

    /// <summary>切页或关闭时停止尚未完成的扫描，但保留完成的结果供返回后查看。</summary>
    public void CancelActiveScan()
    {
        if (_isScanning)
        {
            _scanCancellation?.Cancel();
        }
    }

    /// <summary>返回概览时显式清空当前结果，释放列表占用的内存。</summary>
    public void CancelAndClear()
    {
        _scanCancellation?.Cancel();
        _scanCancellation = null;
        _isScanning = false;
        _target = null;
        _currentResult = null;
        _treemapHistory.Clear();
        _directoryUsages.Clear();
        _largeFiles.Clear();
        TreemapControl.SetItems(Array.Empty<StorageTreemapItem>());
        AnalysisResultsPanel.Visibility = Visibility.Collapsed;
        ScanProgressPanel.Visibility = Visibility.Visible;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressSummaryText.Text = "扫描已停止；再次选择本地卷可重新分析。";
        ScanCurrentPathText.Text = "不会建立后台索引或保留全量文件清单。";
        AnalysisStatusText.Text = "自动使用本机已有 Everything 索引；不可用时回退优化原生扫描。";
        AnalysisStateBadgeText.Text = "等待分析";
        AnalysisStateBadgeText.Foreground = System.Windows.Media.Brushes.SeaGreen;
        RescanButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
    }

    private async Task AnalyzeAsync(StorageAnalysisTarget target, bool forceRefresh)
    {
        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();
        var cancellation = _scanCancellation;
        _isScanning = true;
        _target = target;
        AnalysisTitleText.Text = $"{target.Title} 空间分析";
        AnalysisVolumeSummaryText.Text = $"{target.RootPath} · {target.CapacityText}";
        _directoryUsages.Clear();
        _largeFiles.Clear();
        AnalysisResultsPanel.Visibility = Visibility.Collapsed;
        ScanProgressPanel.Visibility = Visibility.Visible;
        ScanProgressBar.IsIndeterminate = true;
        ScanProgressSummaryText.Text = "正在扫描…";
        ScanCurrentPathText.Text = target.RootPath;
        AnalysisStatusText.Text = "优先使用可用索引；否则执行优化原生扫描，可随时取消。";
        AnalysisStateBadgeText.Text = "分析中";
        AnalysisStateBadgeText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(77, 124, 254));
        RescanButton.IsEnabled = false;
        CancelButton.IsEnabled = true;

        var progress = new Progress<StorageScanProgress>(snapshot =>
        {
            ScanProgressSummaryText.Text = snapshot.SummaryText;
            ScanCurrentPathText.Text = string.IsNullOrWhiteSpace(snapshot.CurrentPath) ? "正在准备下一项…" : snapshot.CurrentPath;
            AnalysisStateBadgeText.Text = snapshot.SourceText;
        });

        try
        {
            var result = await Task.Run(
                () => StorageAnalysisService.Analyze(target, StorageAnalysisService.DefaultLargeFileLimit, progress, cancellation.Token, forceRefresh),
                cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(cancellation, _scanCancellation))
            {
                return;
            }

            foreach (var usage in result.DirectoryUsages) _directoryUsages.Add(usage);
            foreach (var file in result.LargeFiles) _largeFiles.Add(file);
            _currentResult = result;
            UpdateTreemap(result);
            DirectoryUsageSummaryText.Text = $"{result.SummaryText} · 按已扫描文件的逻辑大小排序";
            ScannedBytesText.Text = result.ScannedBytesText;
            LargeFilesSummaryText.Text = result.LargeFiles.Count == 0 ? "未发现可读取的大文件" : "按文件大小降序；可交给文件工作台继续处理。";
            LargeFilesCountText.Text = $"{result.LargeFiles.Count:N0} 项";
            AnalysisNoticeText.Text = result.NoticeText;
            ScanProgressPanel.Visibility = Visibility.Collapsed;
            AnalysisResultsPanel.Visibility = Visibility.Visible;
            AnalysisStatusText.Text = result.SummaryText;
            AnalysisStateBadgeText.Text = "已完成";
            AnalysisStateBadgeText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            RescanButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!ReferenceEquals(cancellation, _scanCancellation))
            {
                return;
            }

            ScanProgressBar.IsIndeterminate = false;
            ScanProgressSummaryText.Text = "已取消扫描，未保留不完整的分析结果。";
            ScanCurrentPathText.Text = "可返回概览或重新分析。";
            AnalysisStatusText.Text = "扫描已取消；未建立后台任务。";
            AnalysisStateBadgeText.Text = "已取消";
            AnalysisStateBadgeText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 168, 71));
            RescanButton.IsEnabled = _target is not null;
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(cancellation, _scanCancellation))
            {
                return;
            }

            ScanProgressBar.IsIndeterminate = false;
            ScanProgressSummaryText.Text = $"分析失败：{exception.Message}";
            ScanCurrentPathText.Text = "请返回概览刷新本地卷后重试。";
            AnalysisStatusText.Text = "未完成扫描，不会保存不完整结果。";
            AnalysisStateBadgeText.Text = "读取失败";
            AnalysisStateBadgeText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(225, 102, 112));
            RescanButton.IsEnabled = _target is not null;
        }
        finally
        {
            if (ReferenceEquals(cancellation, _scanCancellation))
            {
                _isScanning = false;
                _scanCancellation = null;
                CancelButton.IsEnabled = false;
            }

            cancellation.Dispose();
        }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (!_isScanning && _target is not null)
        {
            await AnalyzeAsync(_target, forceRefresh: true);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        CancelAndClear();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DirectoryOpenInFileWorkbench_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StorageDirectoryUsage usage || !usage.CanOpenInFileWorkbench)
        {
            return;
        }

        FileWorkbenchRequested?.Invoke(this, new FileWorkbenchNavigationRequestedEventArgs(usage.FullPath, searchKeyword: null));
    }

    private void LargeFileOpenInFileWorkbench_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StorageLargeFile file || !Directory.Exists(file.ParentDirectory))
        {
            return;
        }

        FileWorkbenchRequested?.Invoke(this, new FileWorkbenchNavigationRequestedEventArgs(file.ParentDirectory, file.FileName, file.FullPath));
    }

    private void ListViewButton_Click(object sender, RoutedEventArgs e)
    {
        _viewMode = StorageResultViewMode.List;
        UpdateViewMode();
    }

    private void TreemapViewButton_Click(object sender, RoutedEventArgs e)
    {
        _viewMode = StorageResultViewMode.Treemap;
        UpdateViewMode();
    }

    private void LargeFilesViewButton_Click(object sender, RoutedEventArgs e)
    {
        _viewMode = StorageResultViewMode.LargeFiles;
        UpdateViewMode();
    }

    private void UpdateViewMode()
    {
        if (ListResultsPanel is null || TreemapResultsPanel is null || LargeFilesResultsPanel is null) return;
        ListResultsPanel.Visibility = _viewMode == StorageResultViewMode.List ? Visibility.Visible : Visibility.Collapsed;
        TreemapResultsPanel.Visibility = _viewMode == StorageResultViewMode.Treemap ? Visibility.Visible : Visibility.Collapsed;
        LargeFilesResultsPanel.Visibility = _viewMode == StorageResultViewMode.LargeFiles ? Visibility.Visible : Visibility.Collapsed;
        ListHeaderSummaryPanel.Visibility = _viewMode == StorageResultViewMode.List ? Visibility.Visible : Visibility.Collapsed;
        SetModeButtonState(ListViewButton, _viewMode == StorageResultViewMode.List);
        SetModeButtonState(TreemapViewButton, _viewMode == StorageResultViewMode.Treemap);
        SetModeButtonState(LargeFilesViewButton, _viewMode == StorageResultViewMode.LargeFiles);
    }

    private static void SetModeButtonState(Button button, bool active)
    {
        button.Tag = active ? "Active" : "Inactive";
    }

    private void UpdateTreemap(StorageAnalysisResult result)
    {
        var total = Math.Max(0, result.ScannedBytes);
        var items = new List<StorageTreemapItem>();
        foreach (var usage in result.DirectoryUsages.Where(item => !item.IsRootFiles && item.LogicalBytes > 0))
        {
            items.Add(new StorageTreemapItem(
                usage.Name,
                usage.FullPath,
                usage.LogicalBytes,
                total <= 0 ? 0 : usage.LogicalBytes / (double)total,
                usage.Accent,
                IsDirectory: true));
        }

        var normalizedRoot = NormalizeDirectory(result.Target.RootPath);
        var rootUsage = result.DirectoryUsages.FirstOrDefault(item => item.IsRootFiles);
        var directLargeFiles = result.LargeFiles
            .Where(file => string.Equals(NormalizeDirectory(file.ParentDirectory), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            .Where(file => total <= 0 || file.Size / (double)total >= 0.001)
            .Take(18)
            .ToArray();
        foreach (var file in directLargeFiles)
        {
            items.Add(new StorageTreemapItem(
                file.FileName,
                file.FullPath,
                file.Size,
                total <= 0 ? 0 : file.Size / (double)total,
                AccentForFile(file.FullPath),
                IsDirectory: false));
        }

        var remainingRootFiles = Math.Max(0, (rootUsage?.LogicalBytes ?? 0) - directLargeFiles.Sum(file => file.Size));
        if (remainingRootFiles > 0)
        {
            items.Add(new StorageTreemapItem(
                "其他当前目录文件",
                result.Target.RootPath,
                remainingRootFiles,
                total <= 0 ? 0 : remainingRootFiles / (double)total,
                "#8298B5",
                IsDirectory: false,
                IsAggregate: true));
        }

        TreemapControl.SetItems(items);
        TreemapUpButton.Visibility = _treemapHistory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TreemapUpButton.IsEnabled = _treemapHistory.Count > 0;
    }

    private static string AccentForFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" => "#A66CE5",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => "#EF7EA8",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "#F3A847",
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" => "#16B99B",
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" => "#E16670",
            _ => "#4D9CB5"
        };
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private async void TreemapControl_ItemInvoked(object? sender, StorageTreemapItemEventArgs e)
    {
        if (e.Item.IsDirectory)
        {
            await DrillIntoTreemapAsync(e.Item);
            return;
        }
        if (e.Item.CanOpenInFileWorkbench)
        {
            OpenTreemapItemInFileWorkbench(e.Item);
        }
    }

    private async Task DrillIntoTreemapAsync(StorageTreemapItem item)
    {
        if (_isScanning || _target is null || !Directory.Exists(item.FullPath)) return;
        var previous = _target;
        _treemapHistory.Push(previous);
        var title = Path.GetFileName(item.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var target = new StorageAnalysisTarget(item.FullPath, string.IsNullOrWhiteSpace(title) ? item.FullPath : title, previous.TotalBytes, previous.FreeBytes);
        await AnalyzeAsync(target, forceRefresh: false);
    }

    private async void TreemapUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning || _treemapHistory.Count == 0) return;
        var parent = _treemapHistory.Pop();
        await AnalyzeAsync(parent, forceRefresh: false);
    }

    private void OpenTreemapItemInFileWorkbench(StorageTreemapItem item)
    {
        if (!item.CanOpenInFileWorkbench) return;
        if (item.IsDirectory)
        {
            FileWorkbenchRequested?.Invoke(this, new FileWorkbenchNavigationRequestedEventArgs(item.FullPath, searchKeyword: null));
            return;
        }

        var parent = Path.GetDirectoryName(item.FullPath);
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
        {
            FileWorkbenchRequested?.Invoke(this, new FileWorkbenchNavigationRequestedEventArgs(parent, Path.GetFileName(item.FullPath), item.FullPath));
        }
    }
}

internal enum StorageResultViewMode
{
    List,
    Treemap,
    LargeFiles
}

/// <summary>从存储分析跳转到文件工作台时保留目录与可选文件名，不直接修改任何文件。</summary>
public sealed class FileWorkbenchNavigationRequestedEventArgs : EventArgs
{
    public FileWorkbenchNavigationRequestedEventArgs(string folderPath, string? searchKeyword, string? knownFilePath = null)
    {
        FolderPath = folderPath;
        SearchKeyword = searchKeyword;
        KnownFilePath = knownFilePath;
    }

    public string FolderPath { get; }
    public string? SearchKeyword { get; }
    public string? KnownFilePath { get; }
}
