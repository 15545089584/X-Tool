using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.Converters;

public partial class VideoConverterView : UserControl
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".webm", ".flv", ".wmv"
    };

    private readonly ObservableCollection<MediaQueueItem> _items = new();
    private readonly MediaConversionService _service = new();
    private CancellationTokenSource? _conversionCancellation;
    private string? _outputDirectory;

    public VideoConverterView()
    {
        InitializeComponent();
        FileList.ItemsSource = _items;
        EngineStatusText.Text = _service.AvailabilityMessage;
        EngineStatusText.Foreground = _service.IsAvailable
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 155, 105))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(194, 126, 58));
        UpdateModeControls();
    }

    private void SelectFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择需要处理的视频",
            Multiselect = true,
            Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.flv;*.wmv"
        };
        if (dialog.ShowDialog() == true)
        {
            _ = AddFilesAsync(dialog.FileNames);
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _ = AddFilesAsync(files);
        }
    }

    private async Task AddFilesAsync(IEnumerable<string> files)
    {
        var accepted = files.Where(file => File.Exists(file) && SupportedExtensions.Contains(Path.GetExtension(file)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(file => _items.All(item => !string.Equals(item.FilePath, file, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        foreach (var file in accepted)
        {
            var item = new MediaQueueItem(file);
            _items.Add(item);
            if (!_service.IsAvailable)
            {
                item.MarkFailed(_service.AvailabilityMessage);
                continue;
            }

            try
            {
                item.ApplyProbe(await _service.ProbeAsync(file), isVideo: true);
            }
            catch (Exception exception)
            {
                item.MarkFailed($"探测失败：{exception.Message}");
            }
        }

        StatusText.Text = accepted.Length == 0 ? "没有发现受支持的新视频文件" : $"已加入 {accepted.Length} 个视频文件";
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (OutputFormatCombo is not null)
        {
            UpdateModeControls();
        }
    }

    private void UpdateModeControls()
    {
        var mode = GetOperation();
        OutputFormatCombo.Items.Clear();
        var formats = mode switch
        {
            VideoOperation.ExtractAudio => new[] { "mp3", "m4a", "wav" },
            VideoOperation.Gif => new[] { "gif" },
            _ => new[] { "mp4", "mov", "mkv", "webm" }
        };
        foreach (var format in formats)
        {
            OutputFormatCombo.Items.Add(new ComboBoxItem { Content = format.ToUpperInvariant(), Tag = format });
        }

        OutputFormatCombo.SelectedIndex = 0;
        QualityCombo.IsEnabled = mode is VideoOperation.Convert or VideoOperation.Compress;
        VideoAdvancedCard.Visibility = mode is VideoOperation.Convert or VideoOperation.Compress or VideoOperation.Gif
            ? Visibility.Visible
            : Visibility.Collapsed;
        GifSettingsCard.Visibility = mode == VideoOperation.Gif ? Visibility.Visible : Visibility.Collapsed;
        CodecCombo.IsEnabled = mode != VideoOperation.Gif;
        VideoBitRateText.IsEnabled = mode != VideoOperation.Gif;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择本次视频任务的输出位置",
            UseDescriptionForTitle = true,
            SelectedPath = _outputDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        _outputDirectory = Path.GetFullPath(dialog.SelectedPath);
        OutputFolderText.Text = _outputDirectory;
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_outputDirectory) || !Directory.Exists(_outputDirectory))
        {
            StatusText.Text = "请先选择有效的输出位置";
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", _outputDirectory) { UseShellExecute = true });
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            StatusText.Text = "请先添加视频文件";
            return;
        }

        if (string.IsNullOrWhiteSpace(_outputDirectory))
        {
            StatusText.Text = "请为本次任务选择输出位置";
            return;
        }

        if (!_service.IsAvailable)
        {
            StatusText.Text = _service.AvailabilityMessage;
            return;
        }

        VideoConversionOptions options;
        try
        {
            options = CreateOptions();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            return;
        }

        Directory.CreateDirectory(_outputDirectory);
        _conversionCancellation = new CancellationTokenSource();
        SetRunningState(true);
        try
        {
            foreach (var item in _items)
            {
                _conversionCancellation.Token.ThrowIfCancellationRequested();
                if (item.Probe is null)
                {
                    item.MarkFailed("缺少可用的媒体信息");
                    continue;
                }

                item.Status = "处理中";
                item.Progress = 0;
                var outputPath = CreateUniqueOutputPath(_outputDirectory, item.FilePath, options.OutputFormat);
                var progress = new Progress<MediaConversionProgress>(value =>
                {
                    item.Progress = value.Percent;
                    item.Status = value.Percent >= 100 ? "已完成" : $"{value.Percent:0}%";
                });
                try
                {
                    await _service.ConvertVideoAsync(item.FilePath, outputPath, item.Probe, options, progress, _conversionCancellation.Token);
                    item.Status = "已完成";
                    item.Progress = 100;
                    item.Details = $"输出：{Path.GetFileName(outputPath)}";
                }
                catch (OperationCanceledException)
                {
                    item.Status = "已取消";
                    throw;
                }
                catch (Exception exception)
                {
                    item.MarkFailed(exception.Message);
                }
            }

            StatusText.Text = "视频队列处理完成，可打开输出位置查看结果";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消视频转换";
        }
        finally
        {
            SetRunningState(false);
            _conversionCancellation.Dispose();
            _conversionCancellation = null;
        }
    }

    private VideoConversionOptions CreateOptions()
    {
        var operation = GetOperation();
        var outputFormat = (OutputFormatCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mp4";
        var quality = (QualityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "标准";
        var codec = (CodecCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "H.264";
        var start = ParseOptionalTime(GifStartText.Text, "开始时间");
        var end = ParseOptionalTime(GifEndText.Text, "结束时间");
        if (start is not null && end is not null && end <= start)
        {
            throw new InvalidOperationException("GIF 结束时间必须晚于开始时间");
        }

        return new VideoConversionOptions(
            operation,
            outputFormat,
            quality,
            ReadNullableInt(WidthCombo),
            int.TryParse(VideoBitRateText.Text, out var bitRate) && bitRate > 0 ? bitRate : null,
            codec,
            start,
            end,
            ReadNullableInt(GifFpsCombo) ?? 15);
    }

    private VideoOperation GetOperation() => ExtractModeRadio?.IsChecked == true ? VideoOperation.ExtractAudio
        : GifModeRadio?.IsChecked == true ? VideoOperation.Gif
        : CompressModeRadio?.IsChecked == true ? VideoOperation.Compress
        : VideoOperation.Convert;

    private static TimeSpan? ParseOptionalTime(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (TimeSpan.TryParse(text, out var value) && value >= TimeSpan.Zero)
        {
            return value;
        }

        throw new InvalidOperationException($"{label}格式应为 00:00:00");
    }

    private static int? ReadNullableInt(ComboBox comboBox) =>
        int.TryParse((comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var value) ? value : null;

    private static string CreateUniqueOutputPath(string directory, string sourcePath, string extension)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var candidate = Path.Combine(directory, $"{baseName}_converted.{extension}");
        for (var index = 2; File.Exists(candidate); index++)
        {
            candidate = Path.Combine(directory, $"{baseName}_converted_{index}.{extension}");
        }

        return candidate;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _conversionCancellation?.Cancel();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_conversionCancellation is not null)
        {
            return;
        }

        _items.Clear();
        StatusText.Text = string.Empty;
    }

    private void SetRunningState(bool isRunning)
    {
        StartButton.IsEnabled = !isRunning;
        CancelButton.IsEnabled = isRunning;
    }
}
