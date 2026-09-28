using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScreenshotApp.ClipboardUi;

namespace ScreenshotApp.DesktopPet;

/// <summary>贴在主动轮盘外侧的会话文件暂存面板。</summary>
public partial class PetShelfPanelWindow : Window
{
    private readonly PetSessionShelfService _shelfService;
    private readonly Func<bool> _isCommandWheelActive;
    private readonly DispatcherTimer _clearConfirmationTimer;
    private readonly DispatcherTimer _dragHoldTimer;
    private Button? _dragSource;
    private bool _isDragging;
    private bool _isClearing;
    private bool _suppressOpenClick;
    private IReadOnlyList<PetShelfPanelItem> _visibleItems = Array.Empty<PetShelfPanelItem>();
    private bool _clearConfirmationArmed;
    private bool _entranceCompleted;
    private ScaleTransform? _entranceScale;
    private TranslateTransform? _entranceTranslate;

    internal PetShelfPanelWindow(PetSessionShelfService shelfService, Func<bool> isCommandWheelActive)
    {
        InitializeComponent();
        _shelfService = shelfService;
        _isCommandWheelActive = isCommandWheelActive;
        _clearConfirmationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _clearConfirmationTimer.Tick += (_, _) => ResetClearConfirmation();
        _dragHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _dragHoldTimer.Tick += (_, _) => BeginSelectedFilesDrag();
        _shelfService.ItemsChanged += ShelfService_ItemsChanged;
        Closed += (_, _) =>
        {
            _clearConfirmationTimer.Stop();
            CancelPendingDrag();
            _shelfService.ItemsChanged -= ShelfService_ItemsChanged;
        };
        Loaded += async (_, _) =>
        {
            RefreshItems();
            PlayOpenAnimation();
            // 轮盘窗口与面板交接焦点时会短暂触发 Deactivated，入场完成前不应因此关闭面板。
            await Task.Delay(740);
            _entranceCompleted = true;
        };
    }

    internal event Action? DismissRequested;

    internal void UpdatePlacement(Rect bounds)
    {
        Left = bounds.Left;
        Top = bounds.Top;
    }

    /// <summary>在窗口显示前，把完整面板压缩到轮盘小卡片所在的位置，确保首帧不会闪成完整尺寸。</summary>
    internal void PrepareEntrance(Point anchorOnScreen, Rect bounds)
    {
        const double rootMargin = 12;
        const double collapsedWidth = 142;
        const double collapsedHeight = 58;
        var rootWidth = Math.Max(1, Width - rootMargin * 2);
        var rootHeight = Math.Max(1, Height - rootMargin * 2);
        var panelCenter = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

        PanelRoot.RenderTransformOrigin = new Point(0.5, 0.5);
        _entranceScale = new ScaleTransform(
            Math.Clamp(collapsedWidth / rootWidth, 0.1, 1),
            Math.Clamp(collapsedHeight / rootHeight, 0.1, 1));
        _entranceTranslate = new TranslateTransform(
            anchorOnScreen.X - panelCenter.X,
            anchorOnScreen.Y - panelCenter.Y);
        PanelRoot.RenderTransform = new TransformGroup
        {
            Children = { _entranceScale, _entranceTranslate }
        };
        PanelRoot.Opacity = 0.94;
    }

    private void ShelfService_ItemsChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshItems();
        }
        else
        {
            Dispatcher.BeginInvoke(RefreshItems);
        }
    }

    private void RefreshItems()
    {
        CancelPendingDrag();
        var selectedIds = _visibleItems.Where(item => item.IsSelected).Select(item => item.Item.Id).ToHashSet();
        _visibleItems = _shelfService.Items
            .Select(item => new PetShelfPanelItem(item, selectedIds.Contains(item.Id)))
            .ToArray();
        ShelfItemsControl.ItemsSource = _visibleItems;
        EmptyState.Visibility = _visibleItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemsScroller.Visibility = _visibleItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SummaryText.Text = $"{_visibleItems.Count} 项 · {FormatSize(_shelfService.TotalBytes)} · 退出软件自动清理";
        CopyButton.IsEnabled = _visibleItems.Count > 0 && !_isDragging && !_isClearing;
        ClearButton.IsEnabled = _visibleItems.Count > 0 && !_isDragging && !_isClearing;
        SelectAllButton.IsEnabled = _visibleItems.Count > 0 && !_isDragging && !_isClearing;
        RefreshSelectionCaption();
    }

    private void SelectionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        // 显式同步勾选值，避免 Checked 事件早于绑定回写而导致数量落后一拍。
        if (sender is CheckBox { DataContext: PetShelfPanelItem item } checkBox)
        {
            item.IsSelected = checkBox.IsChecked == true;
        }
        RefreshSelectionCaption();
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        var selectAll = _visibleItems.Any(item => !item.IsSelected);
        foreach (var item in _visibleItems)
        {
            item.IsSelected = selectAll;
        }
        RefreshSelectionCaption();
    }

    private void RefreshSelectionCaption()
    {
        if (CopyButtonText is null || SelectAllButtonText is null || SelectionText is null)
        {
            return;
        }
        var selectedCount = _visibleItems.Count(item => item.IsSelected);
        CopyButtonText.Text = selectedCount > 0 ? $"复制所选（{selectedCount}）" : "复制全部";
        SelectAllButtonText.Text = selectedCount > 0 && selectedCount == _visibleItems.Count ? "取消全选" : "全选";
        SelectionText.Text = selectedCount > 0 ? $"已选 {selectedCount} 项 · 长按文件即可一起拖出" : "勾选文件后，长按即可拖出";
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _visibleItems.Where(item => item.IsSelected).Select(item => item.Item).ToArray();
        var items = selected.Length > 0 ? selected : _visibleItems.Select(item => item.Item).ToArray();
        try
        {
            ClipboardService.SetFiles(items.Select(item => item.StoredPath));
            ShowStatus($"已复制 {items.Length} 个文件，可直接粘贴");
        }
        catch (Exception exception)
        {
            ShowStatus($"复制失败：{exception.GetBaseException().Message}");
        }
    }

    private async void CopyImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PetShelfItem item })
        {
            return;
        }

        var image = await _shelfService.LoadImageAsync(item);
        if (image is null)
        {
            ShowStatus("图片已损坏或无法读取");
            return;
        }

        try
        {
            ClipboardService.SetImage(image, recordToHistory: true);
            ShowStatus("图片内容已复制到系统剪贴板");
        }
        catch (Exception exception)
        {
            ShowStatus($"复制图片失败：{exception.GetBaseException().Message}");
        }
    }

    private void OpenItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressOpenClick || _isDragging)
        {
            return;
        }
        if (sender is not FrameworkElement { Tag: PetShelfItem item })
        {
            return;
        }

        if (!PetSessionShelfService.TryOpen(item))
        {
            ShowStatus("文件已删除或无法打开");
        }
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDragging)
        {
            return;
        }
        if (!_clearConfirmationArmed)
        {
            _clearConfirmationArmed = true;
            ClearButtonText.Text = "再次点击确认";
            _clearConfirmationTimer.Stop();
            _clearConfirmationTimer.Start();
            ShowStatus("清空只删除暂存副本，不影响原文件");
            return;
        }

        ResetClearConfirmation();
        _isClearing = true;
        CancelPendingDrag();
        CopyButton.IsEnabled = ClearButton.IsEnabled = SelectAllButton.IsEnabled = false;
        ShelfItemsControl.IsEnabled = false;
        try
        {
            var failed = await _shelfService.ClearAsync();
            ShowStatus(failed == 0 ? "暂存区已清空" : $"已清空，{failed} 个正在使用的文件未能删除");
        }
        catch (Exception exception)
        {
            ShowStatus($"清空失败：{exception.GetBaseException().Message}");
        }
        finally
        {
            _isClearing = false;
            ShelfItemsControl.IsEnabled = true;
            CopyButton.IsEnabled = ClearButton.IsEnabled = SelectAllButton.IsEnabled = _shelfService.Items.Count > 0;
        }
    }

    private void ResetClearConfirmation()
    {
        _clearConfirmationArmed = false;
        _clearConfirmationTimer.Stop();
        ClearButtonText.Text = "清空";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DismissRequested?.Invoke();

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_entranceCompleted || _isDragging || _dragSource is not null)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!IsActive && !_isCommandWheelActive() && !_isDragging && _dragSource is null)
            {
                DismissRequested?.Invoke();
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void Item_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelPendingDrag();
        _suppressOpenClick = false;
        if (!_isDragging && !_isClearing && sender is Button { DataContext: PetShelfPanelItem { IsSelected: true } } button)
        {
            // 保留按钮原生捕获与短按打开；只在已勾选的文件内容区启动长按计时。
            _dragSource = button;
            _dragHoldTimer.Start();
        }
    }

    private void Item_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CancelPendingDrag();

    private void Item_LostMouseCapture(object sender, MouseEventArgs e) => CancelPendingDrag();

    private void CancelPendingDrag()
    {
        _dragHoldTimer.Stop();
        _dragSource = null;
    }

    private void BeginSelectedFilesDrag()
    {
        var source = _dragSource;
        CancelPendingDrag();
        if (_isDragging || _isClearing || source is null || !source.IsMouseCaptured || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var paths = _visibleItems.Where(item => item.IsSelected).Select(item => item.Item.StoredPath).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        _suppressOpenClick = true;
        _isDragging = true;
        source.ReleaseMouseCapture();
        ResetClearConfirmation();
        CopyButton.IsEnabled = ClearButton.IsEnabled = SelectAllButton.IsEnabled = false;
        ShelfItemsControl.IsEnabled = false;
        try
        {
            if (paths.Any(path => !File.Exists(path)))
            {
                ShowStatus("部分所选文件已不存在，请重新选择");
                return;
            }

            // 使用标准文件拖放格式供资源管理器和上传区域接收，仅允许复制，绝不移除暂存项。
            var data = new DataObject(DataFormats.FileDrop, paths);
            using var preferredEffect = new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy));
            data.SetData("Preferred DropEffect", preferredEffect);
            ShowStatus($"正在拖出 {paths.Length} 项 · 暂存文件保留");
            var effect = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
            ShowStatus(effect == DragDropEffects.Copy
                ? $"已拖出 {paths.Length} 项 · 暂存文件仍保留"
                : "未完成拖放 · 暂存文件仍保留");
        }
        catch (Exception exception)
        {
            ShowStatus($"拖出失败：{exception.GetBaseException().Message}");
        }
        finally
        {
            _isDragging = false;
            ShelfItemsControl.IsEnabled = true;
            CopyButton.IsEnabled = ClearButton.IsEnabled = SelectAllButton.IsEnabled = _visibleItems.Count > 0;
        }
    }

    private void ShowStatus(string message) => StatusText.Text = message;

    private void PlayOpenAnimation()
    {
        var scale = _entranceScale ?? new ScaleTransform(0.36, 0.16);
        var translate = _entranceTranslate ?? new TranslateTransform(0, 18);
        if (_entranceScale is null || _entranceTranslate is null)
        {
            PanelRoot.RenderTransformOrigin = new Point(0.5, 1);
            PanelRoot.RenderTransform = new TransformGroup
            {
                Children = { scale, translate }
            };
            PanelRoot.Opacity = 0.18;
        }

        // 使用同一段缓动同时完成位移和尺寸变化，小卡片会连续长成完整面板。
        var duration = TimeSpan.FromMilliseconds(720);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        PanelRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(PanelRoot.Opacity, 1, duration)
        {
            EasingFunction = easing
        });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale.ScaleX, 1, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale.ScaleY, 1, duration) { EasingFunction = easing });
        translate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(translate.X, 0, duration) { EasingFunction = easing });
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(translate.Y, 0, duration) { EasingFunction = easing });
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.00} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.0} MB",
        >= 1024L => $"{bytes / 1024d:0.0} KB",
        _ => $"{Math.Max(0, bytes)} B"
    };
}

internal sealed class PetShelfPanelItem : INotifyPropertyChanged
{
    internal PetShelfPanelItem(PetShelfItem item, bool isSelected)
    {
        Item = item;
        IsSelected = isSelected;
        Detail = $"{FormatSize(item.Size)} · {item.AddedAt:HH:mm}";
    }

    public PetShelfItem Item { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Detail { get; }

    public bool HasThumbnail => Item.Thumbnail is not null;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.00} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.0} MB",
        >= 1024L => $"{bytes / 1024d:0.0} KB",
        _ => $"{Math.Max(0, bytes)} B"
    };
}
