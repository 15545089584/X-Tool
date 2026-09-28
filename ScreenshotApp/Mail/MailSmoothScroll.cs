using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenshotApp.Mail;

/// <summary>保留像素虚拟化；仅在滚轮操作期间按渲染帧平滑推进。</summary>
internal static class MailSmoothScroll
{
    public static void Attach(ListBox list, Border fade, TextBlock hint)
    {
        ScrollViewer? viewer = null;
        bool running = false;
        double target = 0;
        var watch = new System.Diagnostics.Stopwatch();
        void Stop()
        {
            if (!running) return;
            CompositionTarget.Rendering -= Frame;
            running = false; watch.Stop();
        }
        void UpdateEdge()
        {
            if (viewer is null) return;
            bool more = viewer.ScrollableHeight - viewer.VerticalOffset > 1;
            fade.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = list.Items.Count == 0 ? "" : more ? "向下滚动查看更多" : "已到列表底部";
        }
        void Frame(object? sender, EventArgs e)
        {
            if (viewer is null) { Stop(); return; }
            target = Math.Clamp(target, 0, viewer.ScrollableHeight);
            var seconds = Math.Min(watch.Elapsed.TotalSeconds, 0.05); watch.Restart();
            var distance = target - viewer.VerticalOffset;
            if (Math.Abs(distance) < 0.5) { viewer.ScrollToVerticalOffset(target); Stop(); }
            else viewer.ScrollToVerticalOffset(viewer.VerticalOffset + distance * (1 - Math.Exp(-20 * seconds)));
            UpdateEdge();
        }
        void Changed(object sender, ScrollChangedEventArgs e) => UpdateEdge();
        list.Loaded += (_, _) =>
        {
            viewer = FindViewer(list);
            if (viewer is not null) { viewer.ScrollChanged -= Changed; viewer.ScrollChanged += Changed; UpdateEdge(); }
        };
        list.Unloaded += (_, _) => { Stop(); if (viewer is not null) viewer.ScrollChanged -= Changed; viewer = null; };
        list.PreviewMouseDown += (_, _) => Stop();
        list.PreviewKeyDown += (_, _) => Stop();
        list.PreviewMouseWheel += (_, e) =>
        {
            if (viewer is null || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            int lines = SystemParameters.WheelScrollLines;
            if (lines == 0) return;
            if (!running) target = viewer.VerticalOffset;
            double step = lines < 0 ? viewer.ViewportHeight * 0.85 : lines * 22;
            target = Math.Clamp(target - e.Delta / 120.0 * step, 0, viewer.ScrollableHeight);
            if (!SystemParameters.ClientAreaAnimation) { viewer.ScrollToVerticalOffset(target); UpdateEdge(); return; }
            if (!running) { running = true; watch.Restart(); CompositionTarget.Rendering += Frame; }
        };
    }
    private static ScrollViewer? FindViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
