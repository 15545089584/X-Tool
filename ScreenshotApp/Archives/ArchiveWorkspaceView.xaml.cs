using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ScreenshotApp.Archives;

public partial class ArchiveWorkspaceView : UserControl
{
    private readonly ArchiveService _archiveService = new();
    private readonly ObservableCollection<ArchiveSourceItem> _sourceItems = new();
    private readonly ObservableCollection<ArchivePreviewItem> _previewItems = new();
    private readonly Stopwatch _operationStopwatch = new();
    private CancellationTokenSource? _operationCancellation;
    private string? _selectedArchivePath;
    private string? _lastOperationResultPath;
    private bool _operationIsRunning;

    public ArchiveWorkspaceView()
    {
        InitializeComponent();
        CreateSourceList.ItemsSource = _sourceItems;
        ArchivePreviewList.ItemsSource = _previewItems;
        UpdateCreateSourceState();
        UpdatePreviewState();
    }

    private void CreateModeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        CreatePanel.Visibility = Visibility.Visible;
        ExtractPanel.Visibility = Visibility.Collapsed;
    }

    private void ExtractModeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        CreatePanel.Visibility = Visibility.Collapsed;
        ExtractPanel.Visibility = Visibility.Visible;
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择需要压缩的文件",
            Multiselect = true,
            Filter = "所有文件|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            AddSources(dialog.FileNames);
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择需要压缩的文件夹",
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            AddSources(new[] { dialog.FolderName });
        }
    }

    private void AddSources(IEnumerable<string> paths)
    {
        var existing = _sourceItems
            .Select(item => item.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (!existing.Add(fullPath))
            {
                continue;
            }

            if (File.Exists(fullPath))
            {
                var info = new FileInfo(fullPath);
                _sourceItems.Add(new ArchiveSourceItem(
                    fullPath,
                    info.Name,
                    false,
                    info.Length));
            }
            else if (Directory.Exists(fullPath))
            {
                var info = new DirectoryInfo(fullPath);
                _sourceItems.Add(new ArchiveSourceItem(
                    fullPath,
                    info.Name,
                    true,
                    null));
            }
        }

        if (string.IsNullOrWhiteSpace(ArchiveOutputPathTextBox.Text) && _sourceItems.Count > 0)
        {
            ArchiveOutputPathTextBox.Text = SuggestArchiveOutputPath();
        }

        UpdateCreateSourceState();
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path })
        {
            return;
        }

        var item = _sourceItems.FirstOrDefault(
            candidate => string.Equals(candidate.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            _sourceItems.Remove(item);
            UpdateCreateSourceState();
        }
    }

    private void ClearSources_Click(object sender, RoutedEventArgs e)
    {
        _sourceItems.Clear();
        ArchiveOutputPathTextBox.Clear();
        UpdateCreateSourceState();
    }

    private void UpdateCreateSourceState()
    {
        CreateEmptyState.Visibility = _sourceItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CreateSourceList.Visibility = _sourceItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var knownBytes = _sourceItems.Where(item => item.Size.HasValue).Sum(item => item.Size ?? 0);
        var folderCount = _sourceItems.Count(item => item.IsDirectory);
        CreateSourceSummaryText.Text = _sourceItems.Count == 0
            ? "尚未添加文件或文件夹"
            : $"{_sourceItems.Count} 个来源 · 已知 {ArchiveSizeFormatter.Format(knownBytes)}" +
              (folderCount > 0 ? $" · {folderCount} 个文件夹将在执行前扫描" : string.Empty);
    }

    private void ArchiveFormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var format = GetSelectedOutputFormat();
        CompressionPresetComboBox.IsEnabled = format != ArchiveOutputFormat.SevenZip;
        if (format == ArchiveOutputFormat.SevenZip)
        {
            CompressionPresetComboBox.SelectedIndex = 2;
            CompressionPresetComboBox.ToolTip = "当前 7z 引擎使用稳定的 LZMA2 默认参数";
        }
        else
        {
            CompressionPresetComboBox.ToolTip = null;
        }

        if (!string.IsNullOrWhiteSpace(ArchiveOutputPathTextBox.Text))
        {
            ArchiveOutputPathTextBox.Text = ChangeArchiveExtension(
                ArchiveOutputPathTextBox.Text,
                format);
        }
    }

    private void ChooseArchiveOutput_Click(object sender, RoutedEventArgs e)
    {
        ChooseArchiveOutputPath();
    }

    private bool ChooseArchiveOutputPath()
    {
        var format = GetSelectedOutputFormat();
        var extension = GetArchiveExtension(format);
        var filter = format switch
        {
            ArchiveOutputFormat.SevenZip => "7z 压缩包|*.7z",
            ArchiveOutputFormat.TarGZip => "TAR.GZ 压缩包|*.tar.gz",
            _ => "ZIP 压缩包|*.zip"
        };
        var suggested = string.IsNullOrWhiteSpace(ArchiveOutputPathTextBox.Text)
            ? SuggestArchiveOutputPath()
            : ChangeArchiveExtension(ArchiveOutputPathTextBox.Text, format);
        var dialog = new SaveFileDialog
        {
            Title = "保存压缩包",
            Filter = filter,
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = Path.GetFileName(suggested),
            InitialDirectory = Path.GetDirectoryName(suggested)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return false;
        }

        ArchiveOutputPathTextBox.Text = dialog.FileName;
        return true;
    }

    private async void StartCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceItems.Count == 0)
        {
            ShowError("请先添加需要压缩的文件或文件夹。");
            return;
        }

        if (string.IsNullOrWhiteSpace(ArchiveOutputPathTextBox.Text) && !ChooseArchiveOutputPath())
        {
            return;
        }

        var outputPath = ChangeArchiveExtension(
            ArchiveOutputPathTextBox.Text,
            GetSelectedOutputFormat());
        ArchiveOutputPathTextBox.Text = outputPath;
        await RunOperationAsync(
            "正在准备压缩内容",
            async (progress, cancellationToken) => await _archiveService.CreateAsync(
                _sourceItems.Select(item => item.FullPath).ToArray(),
                outputPath,
                GetSelectedOutputFormat(),
                GetSelectedCompressionPreset(),
                IncludeTopLevelCheckBox.IsChecked == true,
                progress,
                cancellationToken),
            result => $"压缩完成 · {result.ProcessedFiles} 个文件 · {ArchiveSizeFormatter.Format(result.ProcessedBytes)}");
    }

    private void SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择需要解压的压缩包",
            Multiselect = false,
            Filter = ArchiveService.OpenArchiveFilter
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _ = LoadArchivePreviewAsync(dialog.FileName);
        }
    }

    private async void ReloadPreview_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_selectedArchivePath))
        {
            await LoadArchivePreviewAsync(_selectedArchivePath);
        }
    }

    private async Task LoadArchivePreviewAsync(string archivePath)
    {
        if (_operationIsRunning)
        {
            return;
        }

        _selectedArchivePath = Path.GetFullPath(archivePath);
        SelectedArchivePathTextBox.Text = _selectedArchivePath;
        ExtractDestinationTextBox.Text = SuggestExtractDestination(_selectedArchivePath);
        _previewItems.Clear();
        UpdatePreviewState("正在读取压缩包目录…");
        SetOperationRunning(true, "正在读取压缩包目录");
        _operationCancellation = new CancellationTokenSource();
        try
        {
            var inspection = await _archiveService.InspectAsync(
                _selectedArchivePath,
                ArchivePasswordBox.Password,
                _operationCancellation.Token);
            foreach (var entry in inspection.PreviewEntries)
            {
                _previewItems.Add(entry);
            }

            var encryptedText = inspection.IsEncrypted ? " · 含加密条目" : string.Empty;
            var truncatedText = inspection.PreviewTruncated ? " · 列表仅展示前 5,000 项" : string.Empty;
            UpdatePreviewState(
                $"{inspection.FormatName} · {inspection.TotalEntryCount:N0} 个条目 · " +
                $"展开后约 {ArchiveSizeFormatter.Format(inspection.TotalUncompressedBytes)}{encryptedText}{truncatedText}");
            HideOperationPanel();
        }
        catch (OperationCanceledException)
        {
            UpdatePreviewState("已取消读取压缩包目录");
            ShowOperationMessage("已取消预览", string.Empty, false);
        }
        catch (Exception exception)
        {
            UpdatePreviewState("无法读取压缩包；如已加密，请输入密码后刷新预览");
            ShowOperationMessage("读取失败", GetFriendlyErrorMessage(exception), false);
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetOperationRunning(false);
        }
    }

    private void UpdatePreviewState(string? summary = null)
    {
        PreviewEmptyState.Visibility = _previewItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ArchivePreviewList.Visibility = _previewItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(summary))
        {
            ArchivePreviewSummaryText.Text = summary;
        }
    }

    private void ChooseExtractFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择解压目录",
            Multiselect = false,
            InitialDirectory = Directory.Exists(ExtractDestinationTextBox.Text)
                ? ExtractDestinationTextBox.Text
                : Path.GetDirectoryName(_selectedArchivePath) ?? string.Empty
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            ExtractDestinationTextBox.Text = dialog.FolderName;
        }
    }

    private async void StartExtract_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedArchivePath) || !File.Exists(_selectedArchivePath))
        {
            ShowError("请先选择需要解压的压缩包。");
            return;
        }

        if (string.IsNullOrWhiteSpace(ExtractDestinationTextBox.Text))
        {
            ExtractDestinationTextBox.Text = SuggestExtractDestination(_selectedArchivePath);
        }

        await RunOperationAsync(
            "正在准备解压",
            async (progress, cancellationToken) => await _archiveService.ExtractAsync(
                _selectedArchivePath,
                ExtractDestinationTextBox.Text,
                ArchivePasswordBox.Password,
                GetSelectedConflictPolicy(),
                progress,
                cancellationToken),
            result => $"解压完成 · {result.ProcessedFiles} 个文件" +
                      (result.SkippedFiles > 0 ? $" · 跳过 {result.SkippedFiles} 个" : string.Empty));
    }

    private async Task RunOperationAsync(
        string initialStatus,
        Func<IProgress<ArchiveProgressInfo>, CancellationToken, Task<ArchiveOperationResult>> operation,
        Func<ArchiveOperationResult, string> successMessage)
    {
        if (_operationIsRunning)
        {
            return;
        }

        _operationCancellation = new CancellationTokenSource();
        _operationStopwatch.Restart();
        SetOperationRunning(true, initialStatus);
        var progress = new Progress<ArchiveProgressInfo>(UpdateOperationProgress);
        try
        {
            var result = await operation(progress, _operationCancellation.Token);
            _lastOperationResultPath = result.OutputPath;
            OperationProgressBar.Value = 100;
            OperationProgressText.Text = "100%";
            ShowOperationMessage(successMessage(result), result.OutputPath, true);
        }
        catch (OperationCanceledException)
        {
            ShowOperationMessage("任务已取消", "未完成的临时文件已清理", false);
        }
        catch (Exception exception)
        {
            ShowOperationMessage("任务失败", GetFriendlyErrorMessage(exception), false);
        }
        finally
        {
            _operationStopwatch.Stop();
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetOperationRunning(false);
        }
    }

    private void UpdateOperationProgress(ArchiveProgressInfo progress)
    {
        OperationPanel.Visibility = Visibility.Visible;
        OperationStatusText.Text = progress.Phase;
        OperationCurrentFileText.Text = progress.CurrentEntry.Replace('/', '\\');
        OperationProgressBar.IsIndeterminate = progress.TotalBytes <= 0;
        OperationProgressBar.Value = progress.Percent;
        var elapsedSeconds = Math.Max(_operationStopwatch.Elapsed.TotalSeconds, 0.1);
        var speed = progress.ProcessedBytes / elapsedSeconds;
        OperationProgressText.Text = progress.TotalBytes <= 0
            ? ArchiveSizeFormatter.Format(progress.ProcessedBytes)
            : $"{progress.Percent:0}% · {ArchiveSizeFormatter.Format(progress.ProcessedBytes)} / " +
              $"{ArchiveSizeFormatter.Format(progress.TotalBytes)} · {ArchiveSizeFormatter.Format((long)speed)}/s";
    }

    private void SetOperationRunning(bool running, string? status = null)
    {
        _operationIsRunning = running;
        CreatePanel.IsEnabled = !running;
        ExtractPanel.IsEnabled = !running;
        CreateModeButton.IsEnabled = !running;
        ExtractModeButton.IsEnabled = !running;
        if (running)
        {
            OperationPanel.Visibility = Visibility.Visible;
            OperationStatusText.Text = status ?? "正在处理";
            OperationCurrentFileText.Text = string.Empty;
            OperationProgressText.Text = string.Empty;
            OperationProgressBar.Value = 0;
            OperationProgressBar.IsIndeterminate = true;
            CancelOperationButton.IsEnabled = true;
            CancelOperationButton.Visibility = Visibility.Visible;
            OpenResultButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowOperationMessage(string status, string detail, bool canOpenResult)
    {
        OperationPanel.Visibility = Visibility.Visible;
        OperationProgressBar.IsIndeterminate = false;
        OperationStatusText.Text = status;
        OperationCurrentFileText.Text = detail;
        CancelOperationButton.Visibility = Visibility.Collapsed;
        OpenResultButton.Visibility = canOpenResult ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideOperationPanel()
    {
        OperationPanel.Visibility = Visibility.Collapsed;
        OperationProgressBar.IsIndeterminate = false;
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        CancelOperationButton.IsEnabled = false;
        OperationStatusText.Text = "正在取消…";
    }

    private void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOperationResultPath))
        {
            return;
        }

        var path = _lastOperationResultPath;
        if (File.Exists(path))
        {
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            startInfo.ArgumentList.Add($"/select,{path}");
            Process.Start(startInfo);
        }
        else if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    private void ArchiveWorkspace_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ArchiveWorkspace_Drop(object sender, DragEventArgs e)
    {
        if (_operationIsRunning || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        if (ExtractModeButton.IsChecked == true)
        {
            var archive = paths.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(archive))
            {
                _ = LoadArchivePreviewAsync(archive);
            }

            return;
        }

        AddSources(paths);
    }

    private ArchiveOutputFormat GetSelectedOutputFormat()
    {
        var tag = (ArchiveFormatComboBox.SelectedItem as FrameworkElement)?.Tag?.ToString();
        return Enum.TryParse(tag, out ArchiveOutputFormat format) ? format : ArchiveOutputFormat.Zip;
    }

    private ArchiveCompressionPreset GetSelectedCompressionPreset()
    {
        var tag = (CompressionPresetComboBox.SelectedItem as FrameworkElement)?.Tag?.ToString();
        return Enum.TryParse(tag, out ArchiveCompressionPreset preset) ? preset : ArchiveCompressionPreset.Balanced;
    }

    private ArchiveConflictPolicy GetSelectedConflictPolicy()
    {
        var tag = (ConflictPolicyComboBox.SelectedItem as FrameworkElement)?.Tag?.ToString();
        return Enum.TryParse(tag, out ArchiveConflictPolicy policy) ? policy : ArchiveConflictPolicy.Rename;
    }

    private string SuggestArchiveOutputPath()
    {
        var first = _sourceItems.FirstOrDefault();
        var format = GetSelectedOutputFormat();
        if (first is null)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"新建压缩包{GetArchiveExtension(format)}");
        }

        var parent = first.IsDirectory
            ? Path.GetDirectoryName(first.FullPath)
            : Path.GetDirectoryName(first.FullPath);
        parent = string.IsNullOrWhiteSpace(parent)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : parent;
        var baseName = Path.GetFileNameWithoutExtension(first.DisplayName);
        return Path.Combine(parent, $"{baseName}{GetArchiveExtension(format)}");
    }

    private static string SuggestExtractDestination(string archivePath)
    {
        var directory = Path.GetDirectoryName(archivePath)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var fileName = Path.GetFileName(archivePath);
        var baseName = fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^7]
            : fileName.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^8]
                : Path.GetFileNameWithoutExtension(fileName);
        return Path.Combine(directory, baseName);
    }

    private static string ChangeArchiveExtension(string path, ArchiveOutputFormat format)
    {
        var withoutExtension = path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? path[..^7]
            : Path.Combine(
                Path.GetDirectoryName(path) ?? string.Empty,
                Path.GetFileNameWithoutExtension(path));
        return withoutExtension + GetArchiveExtension(format);
    }

    private static string GetArchiveExtension(ArchiveOutputFormat format)
    {
        return format switch
        {
            ArchiveOutputFormat.SevenZip => ".7z",
            ArchiveOutputFormat.TarGZip => ".tar.gz",
            _ => ".zip"
        };
    }

    private static string GetFriendlyErrorMessage(Exception exception)
    {
        if (exception is UnauthorizedAccessException)
        {
            return "没有访问所选文件或目录的权限。";
        }

        if (exception is IOException && exception.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return "密码错误，或该加密方式暂不受支持。";
        }

        return exception.Message;
    }

    private void ShowError(string message)
    {
        MessageBox.Show(
            Window.GetWindow(this),
            message,
            "压缩包工作台",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
