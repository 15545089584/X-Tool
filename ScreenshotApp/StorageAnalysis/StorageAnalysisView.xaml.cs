using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ScreenshotApp.StorageAnalysis;

/// <summary>存储分析页面只在用户打开指定卷后运行；已完成的结果在本次程序运行期间保留。</summary>
public partial class StorageAnalysisView : UserControl
{
    private readonly ObservableCollection<StorageDirectoryUsage> _directoryUsages = new();
    private readonly ObservableCollection<StorageLargeFile> _largeFiles = new();
    private CancellationTokenSource? _scanCancellation;
    private StorageAnalysisTarget? _target;
    private bool _isScanning;

    public StorageAnalysisView()
    {
        InitializeComponent();
        DirectoryUsageItems.ItemsSource = _directoryUsages;
        LargeFilesListBox.ItemsSource = _largeFiles;
    }

    public event EventHandler? BackRequested;
    public event EventHandler<FileWorkbenchNavigationRequestedEventArgs>? FileWorkbenchRequested;

    public async Task StartAnalysisAsync(StorageAnalysisTarget target)
    {
        _target = target;
        AnalysisTitleText.Text = $"{target.Title} 空间分析";
        AnalysisVolumeSummaryText.Text = $"{target.RootPath} · {target.CapacityText}";
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
        _directoryUsages.Clear();
        _largeFiles.Clear();
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
