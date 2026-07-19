using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.Converters;

public partial class AudioConverterView : UserControl
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma"
    };

    private readonly ObservableCollection<MediaQueueItem> _items = new();
    private readonly MediaConversionService _service = new();
    private CancellationTokenSource? _conversionCancellation;
    private string? _outputDirectory;

    public AudioConverterView()
    {
        InitializeComponent();
        FileList.ItemsSource = _items;
        EngineStatusText.Text = _service.AvailabilityMessage;
        EngineStatusText.Foreground = _service.IsAvailable
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 155, 105))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(194, 126, 58));
    }

    private void SelectFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择需要转换的音频",
            Multiselect = true,
            Filter = "音频文件|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma"
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
                item.ApplyProbe(await _service.ProbeAsync(file), isVideo: false);
            }
            catch (Exception exception)
            {
                item.MarkFailed($"探测失败：{exception.Message}");
            }
        }

        StatusText.Text = accepted.Length == 0 ? "没有发现受支持的新音频文件" : $"已加入 {accepted.Length} 个音频文件";
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择本次音频转换的输出位置",
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
            StatusText.Text = "请先添加音频文件";
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

        Directory.CreateDirectory(_outputDirectory);
        _conversionCancellation = new CancellationTokenSource();
        SetRunningState(true);
        var options = CreateOptions();
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

                item.Status = "转换中";
                item.Progress = 0;
                var outputPath = CreateUniqueOutputPath(_outputDirectory, item.FilePath, options.OutputFormat);
                var progress = new Progress<MediaConversionProgress>(value =>
                {
                    item.Progress = value.Percent;
                    item.Status = value.Percent >= 100 ? "已完成" : $"{value.Percent:0}%";
                });
                try
                {
                    await _service.ConvertAudioAsync(item.FilePath, outputPath, item.Probe, options, progress, _conversionCancellation.Token);
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

            StatusText.Text = "音频队列处理完成，可打开输出位置查看结果";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消音频转换";
        }
        finally
        {
            SetRunningState(false);
            _conversionCancellation.Dispose();
            _conversionCancellation = null;
        }
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

    private AudioConversionOptions CreateOptions()
    {
        var format = (OutputFormatCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mp3";
        var preset = HighQualityRadio.IsChecked == true ? AudioQualityPreset.High
            : SmallQualityRadio.IsChecked == true ? AudioQualityPreset.Small
            : VoiceQualityRadio.IsChecked == true ? AudioQualityPreset.Voice
            : AudioQualityPreset.Standard;
        return new AudioConversionOptions(
            format,
            preset,
            ReadNullableInt(BitRateCombo),
            ReadNullableInt(SampleRateCombo),
            ReadNullableInt(ChannelsCombo),
            PreserveMetadataCheck.IsChecked == true);
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

    private void SetRunningState(bool isRunning)
    {
        StartButton.IsEnabled = !isRunning;
        CancelButton.IsEnabled = isRunning;
    }
}
