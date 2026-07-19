using System.ComponentModel;
using System.IO;

namespace ScreenshotApp.Converters;

internal sealed class PdfQueueItem : INotifyPropertyChanged
{
    private string _status = "等待转换";
    private string _details = string.Empty;
    private double _progress;

    public PdfQueueItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Extension = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        FileSize = FormatFileSize(new FileInfo(filePath).Length);
        Details = GetKindLabel(Extension);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FilePath { get; }
    public string FileName { get; }
    public string Extension { get; }
    public string FileSize { get; }

    public string Details
    {
        get => _details;
        set { _details = value; OnPropertyChanged(nameof(Details)); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); }
    }

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(nameof(Progress)); }
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string GetKindLabel(string extension) => extension switch
    {
        "PDF" => "已有 PDF，可与其他文件合并",
        "DOC" or "DOCX" or "RTF" or "ODT" => "文档 · 优先 Office，其次 WPS/LibreOffice",
        "XLS" or "XLSX" or "CSV" or "ODS" => "表格 · 优先 Office，其次 WPS/LibreOffice",
        "PPT" or "PPTX" or "ODP" => "演示文稿 · 优先 Office，其次 WPS/LibreOffice",
        _ => "图片 · 由 X-Tool 本地生成 PDF"
    };

    private static string FormatFileSize(long bytes) => bytes < 1024 * 1024
        ? $"{Math.Max(1, bytes / 1024d):0.#} KB"
        : $"{bytes / 1024d / 1024d:0.##} MB";
}
