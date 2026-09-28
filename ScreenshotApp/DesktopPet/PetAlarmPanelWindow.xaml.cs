using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ScreenshotApp.DesktopPet;

/// <summary>主动轮盘展开的静默闹钟设置与管理面板。</summary>
public partial class PetAlarmPanelWindow : Window
{
    private readonly PetAlarmService _alarmService;
    private readonly Func<bool> _isCommandWheelActive;
    private bool _entranceCompleted;
    private bool _changingRepeat;
    private bool _showingEditor;
    private ScaleTransform? _entranceScale;
    private TranslateTransform? _entranceTranslate;

    internal PetAlarmPanelWindow(PetAlarmService alarmService, Func<bool> isCommandWheelActive)
    {
        InitializeComponent();
        _alarmService = alarmService;
        _isCommandWheelActive = isCommandWheelActive;
        HourComboBox.ItemsSource = Enumerable.Range(0, 24).Select(value => value.ToString("00")).ToArray();
        MinuteComboBox.ItemsSource = Enumerable.Range(0, 60).Select(value => value.ToString("00")).ToArray();
        var now = DateTime.Now.AddMinutes(5);
        HourComboBox.SelectedIndex = now.Hour;
        MinuteComboBox.SelectedIndex = now.Minute;
        _alarmService.ItemsChanged += AlarmService_ItemsChanged;
        Closed += (_, _) => _alarmService.ItemsChanged -= AlarmService_ItemsChanged;
        Loaded += async (_, _) =>
        {
            RefreshItems();
            PlayOpenAnimation();
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

    private void AlarmService_ItemsChanged()
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
        var items = _alarmService.Items
            .OrderBy(item => _alarmService.GetNextOccurrence(item, DateTime.Now) ?? DateTime.MaxValue)
            .Select(item => new AlarmPanelItem(item, FormatRepeat(item), FormatNext(item)))
            .ToArray();
        AlarmItemsControl.ItemsSource = items;
        AlarmScroller.Visibility = items.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_showingEditor)
        {
            SummaryText.Text = $"{items.Length} 个闹钟 · 仅消息提醒 · 无声音";
        }
    }

    private void NewAlarmButton_Click(object sender, RoutedEventArgs e)
    {
        _showingEditor = true;
        ListPage.Visibility = Visibility.Collapsed;
        EditorPage.Visibility = Visibility.Visible;
        NewAlarmButton.Visibility = Visibility.Collapsed;
        BackToListButton.Visibility = Visibility.Visible;
        PageTitleText.Text = "新建闹钟";
        SummaryText.Text = "设置时间、重复方式与提前提醒";
        StatusText.Text = "所有提醒均无声音";
        ResetEditor();
    }

    private void BackToListButton_Click(object sender, RoutedEventArgs e) => ShowListPage();

    private void ShowListPage()
    {
        _showingEditor = false;
        EditorPage.Visibility = Visibility.Collapsed;
        ListPage.Visibility = Visibility.Visible;
        BackToListButton.Visibility = Visibility.Collapsed;
        NewAlarmButton.Visibility = Visibility.Visible;
        PageTitleText.Text = "静默闹钟";
        StatusText.Text = "闹钟仅在 X-Tool 运行时触发";
        RefreshItems();
    }

    private void ResetEditor()
    {
        var now = DateTime.Now.AddMinutes(5);
        HourComboBox.SelectedIndex = now.Hour;
        MinuteComboBox.SelectedIndex = now.Minute;
        NameTextBox.Clear();
        OnceToggle.IsChecked = true;
        foreach (var toggle in WeekdayPanel.Children.OfType<ToggleButton>())
        {
            toggle.IsChecked = false;
        }
        foreach (var toggle in AdvancePanel.Children.OfType<ToggleButton>())
        {
            toggle.IsChecked = false;
        }
        DeleteAfterToggle.IsChecked = false;
    }

    private void RepeatToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_changingRepeat || OnceToggle is null || DailyToggle is null || CustomToggle is null)
        {
            return;
        }

        _changingRepeat = true;
        OnceToggle.IsChecked = ReferenceEquals(sender, OnceToggle);
        DailyToggle.IsChecked = ReferenceEquals(sender, DailyToggle);
        CustomToggle.IsChecked = ReferenceEquals(sender, CustomToggle);
        WeekdayPanel.Visibility = CustomToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _changingRepeat = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var weekdays = WeekdayPanel.Children.OfType<ToggleButton>()
            .Where(button => button.IsChecked == true && button.Tag is string)
            .Select(button => Enum.Parse<DayOfWeek>((string)button.Tag))
            .ToList();
        var repeatMode = DailyToggle.IsChecked == true
            ? PetAlarmRepeatMode.Daily
            : CustomToggle.IsChecked == true ? PetAlarmRepeatMode.Custom : PetAlarmRepeatMode.Once;
        if (repeatMode == PetAlarmRepeatMode.Custom && weekdays.Count == 0)
        {
            StatusText.Text = "自定义重复至少选择一个星期";
            return;
        }

        var advances = AdvancePanel.Children.OfType<ToggleButton>()
            .Where(button => button.IsChecked == true && button.Tag is string)
            .Select(button => int.Parse((string)button.Tag))
            .ToList();
        _alarmService.Add(new PetAlarmItem
        {
            Name = NameTextBox.Text,
            Hour = Math.Max(0, HourComboBox.SelectedIndex),
            Minute = Math.Max(0, MinuteComboBox.SelectedIndex),
            RepeatMode = repeatMode,
            Weekdays = weekdays,
            AdvanceMinutes = advances,
            DeleteAfterDismiss = DeleteAfterToggle.IsChecked == true
        });
        ShowListPage();
        StatusText.Text = "闹钟已保存；提醒不会播放声音";
    }

    private void EnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string id } toggle)
        {
            _alarmService.SetEnabled(id, toggle.IsChecked == true);
        }
    }

    private void DeleteAlarmButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            _alarmService.Remove(id);
            StatusText.Text = "闹钟已删除";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DismissRequested?.Invoke();

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_entranceCompleted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!IsActive && !_isCommandWheelActive())
            {
                DismissRequested?.Invoke();
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void PlayOpenAnimation()
    {
        var scale = _entranceScale ?? new ScaleTransform(0.34, 0.1);
        var translate = _entranceTranslate ?? new TranslateTransform(0, 18);
        if (_entranceScale is null || _entranceTranslate is null)
        {
            PanelRoot.RenderTransformOrigin = new Point(0.5, 1);
            PanelRoot.RenderTransform = new TransformGroup { Children = { scale, translate } };
            PanelRoot.Opacity = 0.18;
        }

        var duration = TimeSpan.FromMilliseconds(720);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        PanelRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(PanelRoot.Opacity, 1, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 1, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 1, duration) { EasingFunction = easing });
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(translate.X, 0, duration) { EasingFunction = easing });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(translate.Y, 0, duration) { EasingFunction = easing });
    }

    private string FormatNext(PetAlarmItem item)
    {
        var next = item.IsEnabled ? _alarmService.GetNextOccurrence(item, DateTime.Now) : null;
        return next is null ? "已停用" : $"下次 {next:MM-dd HH:mm}";
    }

    private static string FormatRepeat(PetAlarmItem item) => item.RepeatMode switch
    {
        PetAlarmRepeatMode.Once => "仅一次",
        PetAlarmRepeatMode.Daily => "每天",
        PetAlarmRepeatMode.Custom => string.Join("、", item.Weekdays.Select(FormatWeekday)),
        _ => string.Empty
    };

    private static string FormatWeekday(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        DayOfWeek.Sunday => "周日",
        _ => string.Empty
    };

    private sealed record AlarmPanelItem(PetAlarmItem Alarm, string Repeat, string Next)
    {
        public string Id => Alarm.Id;

        public bool IsEnabled => Alarm.IsEnabled;

        public string Title => $"{Alarm.Hour:00}:{Alarm.Minute:00}  {Alarm.Name}";

        public string Detail => $"{Repeat} · {Next}";
    }
}
