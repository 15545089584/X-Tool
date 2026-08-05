using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ScreenshotApp.ClipboardUi;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;

namespace ScreenshotApp.Converters;

/// <summary>二维码生成、识别与本地历史页面；全部处理都在本机完成。</summary>
public partial class QrCodeConverterView : UserControl
{
    private sealed record DecodeRow(string FileName, string? Content, string? Error);

    private BitmapSource? _currentBitmap;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _decodeCts;

    public QrCodeConverterView()
    {
        InitializeComponent();
        SizeValueText.Text = ((int)SizeSlider.Value).ToString();
        MarginValueText.Text = ((int)MarginSlider.Value).ToString();
        ShowContentType();
        LoadHistory();
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode })
        {
            return;
        }

        GeneratePanel.Visibility = mode == "Generate" ? Visibility.Visible : Visibility.Collapsed;
        DecodePanel.Visibility = mode == "Decode" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = mode == "History" ? Visibility.Visible : Visibility.Collapsed;
        if (mode == "History")
        {
            LoadHistory();
        }
    }

    private void ContentTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        ShowContentType();
        QueuePreview();
    }

    private void ShowContentType()
    {
        var type = ContentTypeComboBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "FreeText";
        FreeTextFields.Visibility = type == "FreeText" ? Visibility.Visible : Visibility.Collapsed;
        WifiFields.Visibility = type == "Wifi" ? Visibility.Visible : Visibility.Collapsed;
        VCardFields.Visibility = type == "VCard" ? Visibility.Visible : Visibility.Collapsed;
        EmailFields.Visibility = type == "Email" ? Visibility.Visible : Visibility.Collapsed;
        SmsFields.Visibility = type == "Sms" ? Visibility.Visible : Visibility.Collapsed;
        BatchFields.Visibility = type == "Batch" ? Visibility.Visible : Visibility.Collapsed;

        var isBatch = type == "Batch";
        SaveQrButton.Visibility = isBatch ? Visibility.Collapsed : Visibility.Visible;
        CopyQrButton.Visibility = isBatch ? Visibility.Collapsed : Visibility.Visible;
        SaveBatchButton.Visibility = isBatch ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ContentInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        QueuePreview();
    }

    private void Parameter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        SizeValueText.Text = ((int)SizeSlider.Value).ToString();
        MarginValueText.Text = ((int)MarginSlider.Value).ToString();
        QueuePreview();
    }

    private void ForegroundSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            ForegroundHexInput.Text = color;
            QueuePreview();
        }
    }

    private void BackgroundSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            BackgroundHexInput.Text = color;
            QueuePreview();
        }
    }

    private void ForegroundHexInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TryParseColor(ForegroundHexInput.Text, out var color))
        {
            ForegroundHexInput.Text = "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
            QueuePreview();
        }
        else
        {
            ForegroundHexInput.Text = "#000000";
        }
    }

    private void BackgroundHexInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TryParseColor(BackgroundHexInput.Text, out var color))
        {
            BackgroundHexInput.Text = "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
            QueuePreview();
        }
        else
        {
            BackgroundHexInput.Text = "#FFFFFF";
        }
    }

    private static bool TryParseColor(string text, out Color color)
    {
        color = Colors.Black;
        try
        {
            var value = text.Trim();
            if (!value.StartsWith('#'))
            {
                value = "#" + value;
            }
            var parsed = (Color)ColorConverter.ConvertFromString(value);
            color = Color.FromRgb(parsed.R, parsed.G, parsed.B);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string BuildContent()
    {
        var type = ContentTypeComboBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "FreeText";
        return type switch
        {
            "Wifi" => QrCodeService.BuildWifi(
                WifiSsidInput.Text.Trim(),
                WifiPasswordInput.Text,
                WifiAuthComboBox.SelectedItem is ComboBoxItem { Tag: string auth } ? auth : "WPA"),
            "VCard" => QrCodeService.BuildVCard(
                VCardNameInput.Text.Trim(),
                VCardPhoneInput.Text.Trim(),
                VCardEmailInput.Text.Trim(),
                VCardCompanyInput.Text.Trim(),
                VCardTitleInput.Text.Trim(),
                VCardUrlInput.Text.Trim(),
                string.Empty),
            "Email" => QrCodeService.BuildMailto(
                EmailToInput.Text.Trim(),
                EmailSubjectInput.Text,
                EmailBodyInput.Text),
            "Sms" => QrCodeService.BuildSms(SmsPhoneInput.Text.Trim(), SmsBodyInput.Text),
            "Batch" => BatchInput.Text,
            _ => FreeTextInput.Text
        };
    }

    private static ErrorCorrectionLevel ParseLevel(string? tag) => tag switch
    {
        "L" => ErrorCorrectionLevel.L,
        "Q" => ErrorCorrectionLevel.Q,
        "H" => ErrorCorrectionLevel.H,
        _ => ErrorCorrectionLevel.M
    };

    /// <summary>预览生成走后台线程并取消旧任务，避免滑块拖动或大尺寸时卡住界面。</summary>
    private void QueuePreview()
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        var content = BuildContent();
        var size = (int)SizeSlider.Value;
        var margin = (int)MarginSlider.Value;
        var level = ParseLevel(ErrorLevelComboBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : null);
        var foreground = TryParseColor(ForegroundHexInput.Text, out var fg) ? fg : Colors.Black;
        var background = TryParseColor(BackgroundHexInput.Text, out var bg) ? bg : Colors.White;

        if (string.IsNullOrWhiteSpace(content))
        {
            _currentBitmap = null;
            QrPreviewImage.Source = null;
            QrPreviewEmptyText.Visibility = Visibility.Visible;
            PreviewInfoText.Text = "等待输入内容";
            PreviewErrorText.Text = string.Empty;
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var bitmap = QrCodeService.Generate(content, size, margin, level, foreground, background);
                if (cts.IsCancellationRequested)
                {
                    return;
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (cts.IsCancellationRequested)
                        {
                            return;
                        }
                        _currentBitmap = bitmap;
                        QrPreviewImage.Source = bitmap;
                        QrPreviewEmptyText.Visibility = Visibility.Collapsed;
                        PreviewInfoText.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} 像素 · 内容 {QrCodeService.FriendlySize(content)} · {QrCodeService.CategoryText(QrCodeService.Classify(content))}";
                        PreviewErrorText.Text = string.Empty;
                    }
                    catch (Exception uiEx)
                    {
                        PreviewErrorText.Text = "预览显示失败：" + uiEx.Message;
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }
                    QrPreviewImage.Source = null;
                    QrPreviewEmptyText.Visibility = Visibility.Visible;
                    PreviewErrorText.Text = "生成失败：" + ex.Message;
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }, cts.Token);
    }

    private void SaveQr_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBitmap == null)
        {
            StatusText.Text = "请先输入内容生成二维码";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存二维码",
            Filter = "PNG 图片|*.png",
            FileName = "二维码_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            QrCodeService.SavePng(_currentBitmap, dialog.FileName);
            StatusText.Text = "已保存：" + dialog.FileName;
            AddHistory("Generated", BuildContent(), null, dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "保存失败：" + ex.Message, "保存二维码", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyQr_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBitmap == null)
        {
            StatusText.Text = "请先输入内容生成二维码";
            return;
        }

        try
        {
            ClipboardService.SetImage(_currentBitmap);
            StatusText.Text = "二维码图片已复制到剪贴板";
            AddHistory("Generated", BuildContent(), null, null);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "复制失败：" + ex.Message, "复制二维码", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SaveBatch_Click(object sender, RoutedEventArgs e)
    {
        var lines = BatchInput.Text
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count == 0)
        {
            StatusText.Text = "请先输入需要批量生成的内容";
            return;
        }

        var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择批量保存目录" };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }
        var directory = dialog.SelectedPath;

        var size = (int)SizeSlider.Value;
        var margin = (int)MarginSlider.Value;
        var level = ParseLevel(ErrorLevelComboBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : null);
        var foreground = TryParseColor(ForegroundHexInput.Text, out var fg) ? fg : Colors.Black;
        var background = TryParseColor(BackgroundHexInput.Text, out var bg) ? bg : Colors.White;

        SaveBatchButton.IsEnabled = false;
        StatusText.Text = "批量生成中…";
        var result = await Task.Run(() =>
        {
            var saved = 0;
            var failed = new List<string>();
            var taken = new HashSet<string>(
                Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path) ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < lines.Count; i++)
            {
                var (name, content) = SplitBatchLine(lines[i]);
                try
                {
                    var bitmap = QrCodeService.Generate(content, size, margin, level, foreground, background);
                    var baseName = string.IsNullOrWhiteSpace(name) ? "二维码" : QrCodeService.EscapeFileName(name);
                    var fileName = $"{baseName}_{i + 1:D3}.png";
                    var counter = 1;
                    while (taken.Contains(fileName))
                    {
                        fileName = $"{baseName}_{i + 1:D3}_{counter++}.png";
                    }
                    taken.Add(fileName);
                    QrCodeService.SavePng(bitmap, Path.Combine(directory, fileName));
                    saved++;
                }
                catch (Exception ex)
                {
                    failed.Add(lines[i] + "：" + ex.Message);
                }
            }
            return (Saved: saved, Failed: failed);
        });

        SaveBatchButton.IsEnabled = true;
        StatusText.Text = $"批量生成完成：成功 {result.Saved} 条" + (result.Failed.Count > 0 ? $"，失败 {result.Failed.Count} 条" : string.Empty);
        AddHistory("Generated", $"批量生成 {result.Saved} 条 → {directory}", QrContentCategory.Batch, null);

        var message = $"已生成 {result.Saved} 个二维码到：{directory}";
        if (result.Failed.Count > 0)
        {
            message += "\n\n失败 " + result.Failed.Count + " 条：" + Environment.NewLine + string.Join(Environment.NewLine, result.Failed.Take(5));
        }
        MessageBox.Show(Window.GetWindow(this), message, "批量生成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static (string Name, string Content) SplitBatchLine(string line)
    {
        var index = line.IndexOf('|');
        return index > 0 ? (line.Substring(0, index).Trim(), line.Substring(index + 1).Trim()) : (string.Empty, line);
    }

    private async void SelectImages_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择包含二维码的图片",
            Multiselect = true,
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var sources = dialog.FileNames
            .Select(file => (Name: Path.GetFileName(file), Load: (Func<BitmapSource>)(() => QrCodeService.DecodeBitmapFile(file))))
            .ToList();
        await RunDecodeCoreAsync(sources);
    }

    private async void DecodeClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Clipboard.ContainsImage())
        {
            var image = System.Windows.Clipboard.GetImage();
            if (image == null)
            {
                DecodeStatusText.Text = "剪贴板图片读取失败，请重新复制后重试";
                return;
            }
            // 剪贴板位图在 UI 线程创建，冻结后再交给后台解码线程读取。
            image = QrCodeService.FreezeForCrossThread(image);
            await RunDecodeCoreAsync(new List<(string, Func<BitmapSource>)> { ("剪贴板图片", () => image) });
        }
        else if (System.Windows.Clipboard.ContainsText())
        {
            DecodeStatusText.Text = "剪贴板当前是文本，可在「生成二维码」页使用";
        }
        else
        {
            DecodeStatusText.Text = "剪贴板中没有可识别的图片";
        }
    }

    private void CancelDecode_Click(object sender, RoutedEventArgs e) => _decodeCts?.Cancel();

    private async Task RunDecodeCoreAsync(IReadOnlyList<(string Name, Func<BitmapSource> Load)> sources)
    {
        _decodeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _decodeCts = cts;

        DecodeResultList.Items.Clear();
        DecodeEmptyText.Visibility = Visibility.Collapsed;
        SetDecodingState(true);
        var rows = new List<DecodeRow>();
        var found = 0;

        for (var i = 0; i < sources.Count; i++)
        {
            if (cts.IsCancellationRequested)
            {
                break;
            }
            var source = sources[i];
            DecodeStatusText.Text = $"正在识别 {source.Name}（{i + 1}/{sources.Count}）";
            try
            {
                var bitmap = await Task.Run(source.Load, cts.Token);
                var texts = await Task.Run(() => QrCodeService.Decode(bitmap), cts.Token);
                if (texts.Count == 0)
                {
                    rows.Add(new DecodeRow(source.Name, null, "未识别到二维码"));
                }
                foreach (var text in texts)
                {
                    rows.Add(new DecodeRow(source.Name, text, null));
                    found++;
                    AddHistory("Decoded", text, null, null);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                rows.Add(new DecodeRow(source.Name, null, "读取失败：" + ex.Message));
            }
        }

        if (cts.IsCancellationRequested)
        {
            DecodeStatusText.Text = "识别已取消";
        }
        else
        {
            DecodeStatusText.Text = found > 0 ? $"识别完成：{found} 个二维码" : "未识别到二维码";
        }
        SetDecodingState(false);
        foreach (var row in rows)
        {
            DecodeResultList.Items.Add(row);
        }
        RefreshDecodeRows();
        if (rows.Count == 0)
        {
            DecodeEmptyText.Visibility = Visibility.Visible;
        }
    }

    private void SetDecodingState(bool decoding)
    {
        SelectImagesButton.IsEnabled = !decoding;
        CancelDecodeButton.Visibility = decoding ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshDecodeRows()
    {
        for (var i = 0; i < DecodeResultList.Items.Count; i++)
        {
            if (DecodeResultList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter presenter ||
                DecodeResultList.Items[i] is not DecodeRow row)
            {
                continue;
            }
            var badgeHost = presenter.ContentTemplate.FindName("CategoryBadgeHost", presenter) as Border;
            var badgeText = presenter.ContentTemplate.FindName("BadgeText", presenter) as TextBlock;
            var contentText = presenter.ContentTemplate.FindName("ContentText", presenter) as TextBlock;
            var openButton = presenter.ContentTemplate.FindName("OpenResultButton", presenter) as Button;
            var category = row.Content == null ? QrContentCategory.PlainText : QrCodeService.Classify(row.Content);
            if (badgeHost != null && badgeText != null)
            {
                SetBadge(badgeHost, badgeText, QrCodeService.CategoryText(category));
            }
            if (contentText != null)
            {
                contentText.Text = row.Content != null
                    ? (row.FileName == "剪贴板图片" ? row.Content : $"[{row.FileName}] {row.Content}")
                    : (row.Error ?? string.Empty);
            }
            if (openButton != null)
            {
                openButton.Visibility = row.Content != null && category == QrContentCategory.Url
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private static void SetBadge(Border host, TextBlock text, string category)
    {
        var (bg, fg) = category switch
        {
            "网址" => (Color.FromRgb(220, 235, 255), Color.FromRgb(47, 107, 196)),
            "邮件" => (Color.FromRgb(237, 231, 255), Color.FromRgb(106, 84, 200)),
            "电话/短信" => (Color.FromRgb(223, 245, 242), Color.FromRgb(46, 138, 126)),
            "WiFi" => (Color.FromRgb(228, 246, 232), Color.FromRgb(46, 125, 79)),
            "名片" => (Color.FromRgb(255, 240, 228), Color.FromRgb(192, 106, 45)),
            "日历事件" => (Color.FromRgb(253, 234, 241), Color.FromRgb(183, 78, 124)),
            "批量" => (Color.FromRgb(238, 240, 243), Color.FromRgb(107, 118, 131)),
            _ => (Color.FromRgb(232, 241, 250), Color.FromRgb(74, 107, 140))
        };
        host.Background = new SolidColorBrush(bg);
        text.Text = category;
        text.Foreground = new SolidColorBrush(fg);
    }

    private void CopyDecoded_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DecodeRow { Content: { } content } })
        {
            TryCopyText(content);
        }
    }

    private void OpenDecoded_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DecodeRow { Content: { } content } })
        {
            OpenLink(content);
        }
    }

    private void TryCopyText(string content)
    {
        try
        {
            ClipboardService.SetText(content);
            StatusText.Text = "内容已复制到剪贴板";
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "复制失败：" + ex.Message, "复制内容", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>外部链接只在用户明确确认后打开。</summary>
    private void OpenLink(string content)
    {
        var window = Window.GetWindow(this);
        if (MessageBox.Show(window,
                "二维码内容指向外部链接：" + content + Environment.NewLine + Environment.NewLine + "确定在默认浏览器打开？",
                "打开链接",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            QrCodeService.OpenUrl(content);
        }
        catch (Exception ex)
        {
            MessageBox.Show(window, "打开失败：" + ex.Message, "打开链接", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void HistoryFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        ApplyHistoryFilter();
    }

    private void LoadHistory() => ApplyHistoryFilter();

    private void ApplyHistoryFilter()
    {
        var kind = HistoryTypeComboBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "All";
        var keyword = HistorySearchInput.Text.Trim();
        var entries = QrHistoryStore.Load()
            .Where(entry => kind == "All" || entry.Kind == kind)
            .Where(entry => keyword.Length == 0 || entry.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        HistoryList.Items.Clear();
        foreach (var entry in entries)
        {
            HistoryList.Items.Add(entry);
        }
        HistoryEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshHistoryRows();
    }

    private void RefreshHistoryRows()
    {
        for (var i = 0; i < HistoryList.Items.Count; i++)
        {
            if (HistoryList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter presenter ||
                HistoryList.Items[i] is not QrHistoryEntry entry)
            {
                continue;
            }
            var badgeHost = presenter.ContentTemplate.FindName("HistoryBadgeHost", presenter) as Border;
            var badgeText = presenter.ContentTemplate.FindName("HistoryBadgeText", presenter) as TextBlock;
            var timeText = presenter.ContentTemplate.FindName("HistoryTimeText", presenter) as TextBlock;
            var kindText = presenter.ContentTemplate.FindName("HistoryKindText", presenter) as TextBlock;
            var contentText = presenter.ContentTemplate.FindName("HistoryContentText", presenter) as TextBlock;
            if (badgeHost != null && badgeText != null)
            {
                SetBadge(badgeHost, badgeText, entry.Category);
            }
            if (timeText != null)
            {
                timeText.Text = entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");
            }
            if (kindText != null)
            {
                kindText.Text = entry.Kind == "Generated" ? "生成" : "识别";
            }
            if (contentText != null)
            {
                var content = entry.Content;
                if (content.Length > 200)
                {
                    content = content.Substring(0, 200) + "…";
                }
                contentText.Text = content;
            }
        }
    }

    private void AddHistory(string kind, string content, QrContentCategory? category, string? filePath)
    {
        if (SaveHistoryCheckBox.IsChecked != true || string.IsNullOrWhiteSpace(content))
        {
            return;
        }
        var resolved = category ?? QrCodeService.Classify(content);
        QrHistoryStore.Add(new QrHistoryEntry(
            Guid.NewGuid().ToString("N"),
            kind,
            QrCodeService.CategoryText(resolved),
            content,
            filePath,
            DateTime.Now));
    }

    private void CopyHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QrHistoryEntry entry })
        {
            TryCopyText(entry.Content);
        }
    }

    private void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QrHistoryEntry entry })
        {
            QrHistoryStore.Remove(entry.Id);
            ApplyHistoryFilter();
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (MessageBox.Show(window,
                "确定清空全部二维码历史？此操作不会删除已保存的图片文件。",
                "清空历史",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        QrHistoryStore.Clear();
        ApplyHistoryFilter();
        StatusText.Text = "历史已清空";
    }
}
