using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PdfSharp.Pdf;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.Converters;

public partial class PdfConverterView : UserControl
{
    private readonly ObservableCollection<PdfQueueItem> _items = new();
    private readonly PdfConversionService _service = new();
    private CancellationTokenSource? _conversionCancellation;
    private string? _outputDirectory;

    public PdfConverterView()
    {
        InitializeComponent();
        FileList.ItemsSource = _items;
        RefreshEngineStatus();
    }

    private void RefreshEngineStatus()
    {
        var status = _service.GetEngineStatus();
        EngineStatusText.Text = status.Summary;
        EngineDetailText.Text = status.HasMicrosoftOffice || status.HasWps || status.HasLibreOffice
            ? "办公文档按 Microsoft Office → WPS Office → LibreOffice 的顺序尝试导出。图片和已有 PDF 不依赖这些软件。"
            : "未检测到 Office、WPS 或 LibreOffice。图片和已有 PDF 仍可直接处理；办公文档请先安装 LibreOffice 后重试。";
    }

    private void SelectFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择需要转 PDF 的文件",
            Multiselect = true,
            Filter = "支持的文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.pdf;*.doc;*.docx;*.rtf;*.odt;*.xls;*.xlsx;*.csv;*.ods;*.ppt;*.pptx;*.odp"
        };
        if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
    }

    private void AddFiles(IEnumerable<string> files)
    {
        var accepted = files.Where(file => File.Exists(file) && PdfConversionService.SupportedExtensions.Contains(Path.GetExtension(file)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(file => _items.All(item => !string.Equals(item.FilePath, file, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        foreach (var file in accepted) _items.Add(new PdfQueueItem(file));
        StatusText.Text = accepted.Length == 0 ? "没有发现受支持的新文件" : $"已加入 {accepted.Length} 个文件";
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择本次 PDF 转换的输出位置", UseDescriptionForTitle = true, SelectedPath = _outputDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        _outputDirectory = Path.GetFullPath(dialog.SelectedPath);
        OutputFolderText.Text = _outputDirectory;
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_outputDirectory) || !Directory.Exists(_outputDirectory)) { StatusText.Text = "请先选择有效的输出位置"; return; }
        Process.Start(new ProcessStartInfo("explorer.exe", _outputDirectory) { UseShellExecute = true });
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) { StatusText.Text = "请先添加文件"; return; }
        if (string.IsNullOrWhiteSpace(_outputDirectory)) { StatusText.Text = "请为本次任务选择输出位置"; return; }
        Directory.CreateDirectory(_outputDirectory);
        _conversionCancellation = new CancellationTokenSource();
        SetRunningState(true);
        try
        {
            if (MergeRadio.IsChecked == true) await ConvertMergedAsync(_conversionCancellation.Token);
            else await ConvertSeparatelyAsync(_conversionCancellation.Token);
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消 PDF 转换；已完成的文件会保留在输出位置"; }
        finally { SetRunningState(false); _conversionCancellation.Dispose(); _conversionCancellation = null; RefreshEngineStatus(); }
    }

    private async Task ConvertMergedAsync(CancellationToken cancellationToken)
    {
        using var document = new PdfDocument();
        var completed = 0;
        foreach (var item in _items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.Status = "转换中"; item.Progress = 20;
            try
            {
                var engine = await _service.AppendSourceAsync(item.FilePath, document, cancellationToken);
                item.Details = $"已添加 · {engine}"; item.Status = "已完成"; item.Progress = 100; completed++;
            }
            catch (Exception exception) { item.Status = "失败"; item.Details = exception.Message; item.Progress = 0; }
        }
        if (completed == 0) { StatusText.Text = "没有成功转换的文件，请查看每项失败原因"; return; }
        var output = CreateUniqueOutputPath("XTool_合并.pdf");
        document.Save(output);
        StatusText.Text = $"已合并 {completed} 个文件：{Path.GetFileName(output)}";
    }

    private async Task ConvertSeparatelyAsync(CancellationToken cancellationToken)
    {
        var completed = 0;
        foreach (var item in _items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.Status = "转换中"; item.Progress = 20;
            try
            {
                using var document = new PdfDocument();
                var engine = await _service.AppendSourceAsync(item.FilePath, document, cancellationToken);
                var output = CreateUniqueOutputPath(Path.GetFileNameWithoutExtension(item.FilePath) + "_converted.pdf");
                document.Save(output);
                item.Details = $"输出：{Path.GetFileName(output)} · {engine}"; item.Status = "已完成"; item.Progress = 100; completed++;
            }
            catch (Exception exception) { item.Status = "失败"; item.Details = exception.Message; item.Progress = 0; }
        }
        StatusText.Text = completed == 0 ? "没有成功转换的文件，请查看每项失败原因" : $"已输出 {completed} 个 PDF 文件";
    }

    private string CreateUniqueOutputPath(string fileName)
    {
        var directory = _outputDirectory!;
        var candidate = Path.Combine(directory, fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        for (var index = 2; File.Exists(candidate); index++) candidate = Path.Combine(directory, $"{baseName}_{index}.pdf");
        return candidate;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _conversionCancellation?.Cancel();
    private void Clear_Click(object sender, RoutedEventArgs e) { if (_conversionCancellation is null) { _items.Clear(); StatusText.Text = string.Empty; } }
    private void SetRunningState(bool isRunning) { StartButton.IsEnabled = !isRunning; CancelButton.IsEnabled = isRunning; }
}
