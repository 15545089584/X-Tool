using System.Windows.Media.Imaging;

namespace ScreenshotApp.History;

/// <summary>
/// 所有可回看的本地内容类型。图片、文本和录像都使用同一套历史页呈现。
/// </summary>
public enum HistoryEntryKind
{
    Screenshot,
    LongScreenshot,
    TextExtraction,
    Translation,
    ScreenRecording
}

/// <summary>
/// 历史页展示的一条本地记录。
/// </summary>
public sealed record ScreenshotHistoryItem(
    HistoryEntryKind Kind,
    string KindText,
    string FilePath,
    string FileName,
    DateTime CapturedAt,
    string CapturedAtText,
    string SizeText,
    string PreviewText,
    BitmapSource? Thumbnail)
{
    public bool HasThumbnail => Thumbnail is not null;
}

/// <summary>
/// 选择工具完成 OCR 或翻译后发出的本地持久化请求。
/// </summary>
public sealed class HistoryTextContent : EventArgs
{
    public HistoryTextContent(HistoryEntryKind kind, string content)
    {
        Kind = kind;
        Content = content;
    }

    public HistoryEntryKind Kind { get; }

    public string Content { get; }
}
