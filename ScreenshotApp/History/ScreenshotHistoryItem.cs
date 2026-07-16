using System.Windows.Media.Imaging;

namespace ScreenshotApp.History;

/// <summary>
/// 历史页展示的一条本地截图记录。
/// </summary>
public sealed record ScreenshotHistoryItem(
    string FilePath,
    string FileName,
    string CapturedAtText,
    string SizeText,
    BitmapSource Thumbnail);
