using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenshotApp.DeveloperTools;

/// <summary>
/// 托管安装操作日志弹框：读取本地日志文件并以毛玻璃列表形式展示。
/// </summary>
public partial class ManagedLogWindow : Window
{
    private const int MaxShownEntries = 2000;

    private static readonly Regex EntryPattern =
        new(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\s?(.*)$", RegexOptions.Compiled);

    private readonly string _logPath;
    private readonly ObservableCollection<ManagedLogEntry> _entries = new();
    private int _totalLineCount;

    public ManagedLogWindow(string logPath)
    {
        InitializeComponent();
        _logPath = logPath;
        LogListBox.ItemsSource = _entries;
        LogPathText.Text = logPath;
        LogPathText.ToolTip = logPath;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        LogStateText.Text = "正在读取日志…";
        try
        {
            var snapshot = await Task.Run(() => ReadSnapshot(_logPath, MaxShownEntries));
            _totalLineCount = snapshot.TotalLines;

            _entries.Clear();
            var parsed = ParseEntries(snapshot.Lines);
            parsed.Reverse(); // 最新记录显示在最上方
            foreach (var entry in parsed)
            {
                _entries.Add(entry);
            }

            SummaryText.Text = snapshot.TotalLines > parsed.Count
                ? $"共 {snapshot.TotalLines} 行日志，当前显示最近 {parsed.Count} 条记录"
                : $"共 {snapshot.TotalLines} 行日志";
            EmptyStateText.Visibility = parsed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LogStateText.Text = parsed.Count == 0
                ? "尚无托管操作日志，可先执行一次安装或卸载操作"
                : "读取完成，最新记录在前";
            CopyButton.IsEnabled = parsed.Count > 0;
            OpenFileButton.IsEnabled = File.Exists(_logPath);
        }
        catch (Exception ex)
        {
            LogStateText.Text = $"读取日志失败：{ex.Message}";
            EmptyStateText.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private static LogSnapshot ReadSnapshot(string path, int maxLines)
    {
        var lines = new List<string>();
        var total = 0;
        if (File.Exists(path))
        {
            // 允许并发读写，避免安装任务正在写日志时读取失败。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var queue = new Queue<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                total++;
                queue.Enqueue(line);
                if (queue.Count > maxLines)
                {
                    queue.Dequeue();
                }
            }
            lines = queue.ToList();
        }
        return new LogSnapshot(lines, total);
    }

    private static List<ManagedLogEntry> ParseEntries(IEnumerable<string> lines)
    {
        var entries = new List<ManagedLogEntry>();
        ManagedLogEntry? current = null;
        foreach (var line in lines)
        {
            var match = EntryPattern.Match(line);
            if (match.Success)
            {
                current = new ManagedLogEntry(match.Groups[1].Value, match.Groups[2].Value);
                entries.Add(current);
            }
            else if (current is not null && line.Length > 0)
            {
                // 异常堆栈等续行合并到上一条记录，保留完整上下文。
                current.AppendLine(line);
            }
            else if (current is null && !string.IsNullOrWhiteSpace(line))
            {
                current = new ManagedLogEntry(string.Empty, line);
                entries.Add(current);
            }
        }
        return entries;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            builder.Append(entry.TimeText.Length > 0 ? $"{entry.TimeText} {entry.Message}" : entry.Message).AppendLine();
        }

        try
        {
            Clipboard.SetText(builder.ToString());
            LogStateText.Text = $"已复制 {_entries.Count} 条记录到剪贴板";
        }
        catch (Exception ex)
        {
            LogStateText.Text = $"复制失败：{ex.Message}";
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_logPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = _logPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogStateText.Text = $"无法打开日志文件：{ex.Message}";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(_logPath);
        if (directory is null)
        {
            return;
        }

        try
        {
            if (File.Exists(_logPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_logPath}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            LogStateText.Text = $"无法打开日志目录：{ex.Message}";
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // 拖动过程中可能触发重入，忽略即可。
        }
    }

    private sealed record LogSnapshot(List<string> Lines, int TotalLines);

    private enum LogLevel
    {
        Info,
        Success,
        Warning,
        Error
    }

    private sealed class ManagedLogEntry
    {
        private static readonly Brush InfoRow = CreateBrush("#7CFFFFFF");
        private static readonly Brush InfoBadge = CreateBrush("#D8EAF8");
        private static readonly Brush InfoBadgeText = CreateBrush("#33749F");
        private static readonly Brush SuccessRow = CreateBrush("#8FE9F6EF");
        private static readonly Brush SuccessBadge = CreateBrush("#D5EFE1");
        private static readonly Brush SuccessBadgeText = CreateBrush("#2E7A52");
        private static readonly Brush WarningRow = CreateBrush("#8FF9F1E2");
        private static readonly Brush WarningBadge = CreateBrush("#F4E5CA");
        private static readonly Brush WarningBadgeText = CreateBrush("#97681D");
        private static readonly Brush ErrorRow = CreateBrush("#92FAEAE9");
        private static readonly Brush ErrorBadge = CreateBrush("#F5DAD8");
        private static readonly Brush ErrorBadgeText = CreateBrush("#B23D35");

        private readonly StringBuilder _message = new();

        public string TimeText { get; }

        public string Message => _message.ToString();

        public string LevelText { get; }

        public Brush RowBackground { get; }

        public Brush LevelBackground { get; }

        public Brush LevelForeground { get; }

        public ManagedLogEntry(string time, string message)
        {
            TimeText = time;
            _message.Append(message);
            switch (ResolveLevel(message))
            {
                case LogLevel.Success:
                    LevelText = "成功";
                    RowBackground = SuccessRow;
                    LevelBackground = SuccessBadge;
                    LevelForeground = SuccessBadgeText;
                    break;
                case LogLevel.Warning:
                    LevelText = "警告";
                    RowBackground = WarningRow;
                    LevelBackground = WarningBadge;
                    LevelForeground = WarningBadgeText;
                    break;
                case LogLevel.Error:
                    LevelText = "错误";
                    RowBackground = ErrorRow;
                    LevelBackground = ErrorBadge;
                    LevelForeground = ErrorBadgeText;
                    break;
                default:
                    LevelText = "信息";
                    RowBackground = InfoRow;
                    LevelBackground = InfoBadge;
                    LevelForeground = InfoBadgeText;
                    break;
            }
        }

        public void AppendLine(string line) => _message.AppendLine(line);

        private static LogLevel ResolveLevel(string message)
        {
            if (message.Contains("失败", StringComparison.Ordinal) ||
                message.Contains("无法解析", StringComparison.Ordinal) ||
                message.Contains("拒绝", StringComparison.Ordinal))
            {
                return LogLevel.Error;
            }

            if (message.Contains("取消", StringComparison.Ordinal))
            {
                return LogLevel.Warning;
            }

            if (message.Contains("已安装", StringComparison.Ordinal) ||
                message.Contains("已卸载", StringComparison.Ordinal) ||
                message.Contains("已通过", StringComparison.Ordinal) ||
                message.Contains("已缓存", StringComparison.Ordinal))
            {
                return LogLevel.Success;
            }

            return LogLevel.Info;
        }

        private static SolidColorBrush CreateBrush(string hex) =>
            new((Color)ColorConverter.ConvertFromString(hex));
    }
}
