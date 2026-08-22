using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ScreenshotApp.Motion;

/// <summary>
/// X-Tool 自有界面动效协调器。统一页面、卡片和轻提示的时长与缓动，
/// 不依赖 Windows 系统动画开关，也不对高频数据区域持续施加动画。
/// </summary>
internal static class AppMotion
{
    private static readonly TimeSpan PageEnterDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PageExitDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PageEnterDelay = TimeSpan.FromMilliseconds(170);
    private static readonly TimeSpan SubpageEnterDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan SubpageExitDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan SubpageEnterDelay = TimeSpan.FromMilliseconds(130);

    public static void AnimatePageTransition(
        FrameworkElement? outgoing,
        FrameworkElement incoming,
        int direction,
        Action completed)
    {
        _ = direction;
        ResetPage(incoming);
        incoming.Visibility = Visibility.Visible;
        incoming.IsHitTestVisible = true;
        Panel.SetZIndex(incoming, 1);

        if (outgoing is not null && !ReferenceEquals(outgoing, incoming))
        {
            ResetPage(outgoing);
            outgoing.Visibility = Visibility.Visible;
            outgoing.IsHitTestVisible = false;
            Panel.SetZIndex(outgoing, 2);
            AnimatePageExit(outgoing);
        }

        var (incomingScale, _) = EnsureMotionTransform(incoming);
        incoming.Opacity = 0;
        incomingScale.ScaleX = 1.018;
        incomingScale.ScaleY = 1.018;
        var enterEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        incoming.BeginAnimation(UIElement.OpacityProperty, CreateDelayedAnimation(0, 1, PageEnterDuration, PageEnterDelay, enterEase), HandoffBehavior.SnapshotAndReplace);
        incomingScale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateDelayedAnimation(1.018, 1, PageEnterDuration, PageEnterDelay, enterEase), HandoffBehavior.SnapshotAndReplace);
        var focus = CreateDelayedAnimation(1.018, 1, PageEnterDuration, PageEnterDelay, enterEase);
        focus.Completed += (_, _) => completed();
        incomingScale.BeginAnimation(ScaleTransform.ScaleYProperty, focus, HandoffBehavior.SnapshotAndReplace);
    }

    public static void AnimateSubpageTransition(FrameworkElement? outgoing, FrameworkElement incoming, Action completed)
    {
        ResetPage(incoming);
        incoming.Visibility = Visibility.Visible;
        incoming.IsHitTestVisible = true;
        Panel.SetZIndex(incoming, 1);

        if (outgoing is not null && !ReferenceEquals(outgoing, incoming))
        {
            ResetPage(outgoing);
            outgoing.Visibility = Visibility.Visible;
            outgoing.IsHitTestVisible = false;
            Panel.SetZIndex(outgoing, 2);
            var (outgoingScale, outgoingTranslate) = EnsureMotionTransform(outgoing);
            var exitEase = new CubicEase { EasingMode = EasingMode.EaseIn };
            var exit = CreateAnimation(0, SubpageExitDuration, exitEase);
            exit.Completed += (_, _) =>
            {
                if (!outgoing.IsHitTestVisible)
                {
                    outgoing.Visibility = Visibility.Collapsed;
                }
            };
            outgoing.BeginAnimation(UIElement.OpacityProperty, exit, HandoffBehavior.SnapshotAndReplace);
            outgoingScale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnimation(0.988, SubpageExitDuration, exitEase), HandoffBehavior.SnapshotAndReplace);
            outgoingScale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnimation(0.988, SubpageExitDuration, exitEase), HandoffBehavior.SnapshotAndReplace);
            outgoingTranslate.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(0, SubpageExitDuration, exitEase), HandoffBehavior.SnapshotAndReplace);
        }

        var (incomingScale, _) = EnsureMotionTransform(incoming);
        incoming.Opacity = 0;
        incomingScale.ScaleX = 1.012;
        incomingScale.ScaleY = 1.012;
        var enterEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        incoming.BeginAnimation(UIElement.OpacityProperty, CreateDelayedAnimation(0, 1, SubpageEnterDuration, SubpageEnterDelay, enterEase), HandoffBehavior.SnapshotAndReplace);
        incomingScale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateDelayedAnimation(1.012, 1, SubpageEnterDuration, SubpageEnterDelay, enterEase), HandoffBehavior.SnapshotAndReplace);
        var focus = CreateDelayedAnimation(1.012, 1, SubpageEnterDuration, SubpageEnterDelay, enterEase);
        focus.Completed += (_, _) => completed();
        incomingScale.BeginAnimation(ScaleTransform.ScaleYProperty, focus, HandoffBehavior.SnapshotAndReplace);
    }

    public static void ResetPage(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        var (scale, translate) = EnsureMotionTransform(element);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;
    }

    public static void AnimateCard(FrameworkElement element, double scale, double offsetY, int durationMilliseconds)
    {
        var (scaleTransform, translateTransform) = EnsureMotionTransform(element);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var duration = TimeSpan.FromMilliseconds(durationMilliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnimation(scale, duration, easing), HandoffBehavior.SnapshotAndReplace);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnimation(scale, duration, easing), HandoffBehavior.SnapshotAndReplace);
        translateTransform.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(offsetY, duration, easing), HandoffBehavior.SnapshotAndReplace);
    }

    public static void AnimateToastIn(FrameworkElement toast)
    {
        toast.BeginAnimation(UIElement.OpacityProperty, null);
        toast.Opacity = 1;
        var translate = EnsureToastTransform(toast);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.Y = 0;

        var duration = TimeSpan.FromMilliseconds(260);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        toast.BeginAnimation(UIElement.OpacityProperty, CreateAnimation(0, 1, TimeSpan.FromMilliseconds(220), easing, FillBehavior.Stop), HandoffBehavior.SnapshotAndReplace);
        translate.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(16, 0, duration, easing, FillBehavior.Stop), HandoffBehavior.SnapshotAndReplace);
    }

    public static void AnimateToastOut(FrameworkElement toast, Action completed)
    {
        var duration = TimeSpan.FromMilliseconds(210);
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = CreateAnimation(0, duration, easing);
        fade.Completed += (_, _) => completed();
        toast.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        EnsureToastTransform(toast).BeginAnimation(
            TranslateTransform.YProperty,
            CreateAnimation(8, duration, easing),
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimatePageExit(FrameworkElement outgoing)
    {
        var (scale, translate) = EnsureMotionTransform(outgoing);
        var exitEase = new CubicEase { EasingMode = EasingMode.EaseIn };
        var exit = CreateAnimation(0, PageExitDuration, exitEase);
        exit.Completed += (_, _) =>
        {
            if (!outgoing.IsHitTestVisible)
            {
                outgoing.Visibility = Visibility.Collapsed;
            }
        };
        outgoing.BeginAnimation(UIElement.OpacityProperty, exit, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnimation(0.99, PageExitDuration, exitEase), HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnimation(0.99, PageExitDuration, exitEase), HandoffBehavior.SnapshotAndReplace);
        translate.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(0, PageExitDuration, exitEase), HandoffBehavior.SnapshotAndReplace);
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) EnsureMotionTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup group &&
            group.Children.Count == 2 &&
            group.Children[0] is ScaleTransform existingScale &&
            group.Children[1] is TranslateTransform existingTranslate)
        {
            return (existingScale, existingTranslate);
        }

        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(scale);
        transformGroup.Children.Add(translate);
        element.RenderTransform = transformGroup;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return (scale, translate);
    }

    private static TranslateTransform EnsureToastTransform(FrameworkElement toast)
    {
        if (toast.RenderTransform is TranslateTransform existing)
        {
            return existing;
        }

        var translate = new TranslateTransform();
        toast.RenderTransform = translate;
        return translate;
    }

    private static DoubleAnimation CreateAnimation(double target, TimeSpan duration, IEasingFunction easing) =>
        CreateAnimation(null, target, duration, easing, FillBehavior.HoldEnd);

    private static DoubleAnimation CreateAnimation(double from, double to, TimeSpan duration, IEasingFunction easing, FillBehavior fillBehavior) =>
        CreateAnimation((double?)from, to, duration, easing, fillBehavior);

    private static DoubleAnimation CreateDelayedAnimation(double from, double to, TimeSpan duration, TimeSpan delay, IEasingFunction easing)
    {
        var animation = CreateAnimation(from, to, duration, easing, FillBehavior.HoldEnd);
        animation.BeginTime = delay;
        return animation;
    }

    private static DoubleAnimation CreateAnimation(double? from, double to, TimeSpan duration, IEasingFunction easing, FillBehavior fillBehavior)
    {
        return new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = fillBehavior
        };
    }
}

/// <summary>维护一组互斥子页面的当前状态，并安全处理连续快速切换。</summary>
internal sealed class MotionPageGroup
{
    private readonly FrameworkElement[] _pages;
    private FrameworkElement _current;
    private int _transitionVersion;

    public MotionPageGroup(FrameworkElement initialPage, params FrameworkElement[] pages)
    {
        _current = initialPage;
        _pages = pages.Distinct().ToArray();
        foreach (var page in _pages)
        {
            page.Visibility = ReferenceEquals(page, initialPage) ? Visibility.Visible : Visibility.Collapsed;
            page.IsHitTestVisible = ReferenceEquals(page, initialPage);
        }
    }

    public void SetCurrent(FrameworkElement current) => _current = current;

    public void Show(FrameworkElement incoming)
    {
        if (ReferenceEquals(_current, incoming))
        {
            incoming.Visibility = Visibility.Visible;
            incoming.IsHitTestVisible = true;
            return;
        }

        var outgoing = _current;
        var version = ++_transitionVersion;
        _current = incoming;

        foreach (var page in _pages)
        {
            if (ReferenceEquals(page, incoming) || ReferenceEquals(page, outgoing))
            {
                continue;
            }

            AppMotion.ResetPage(page);
            page.Visibility = Visibility.Collapsed;
            page.IsHitTestVisible = false;
        }

        AppMotion.AnimateSubpageTransition(outgoing, incoming, () =>
        {
            if (version != _transitionVersion || ReferenceEquals(_current, outgoing))
            {
                return;
            }

            outgoing.Visibility = Visibility.Collapsed;
            outgoing.IsHitTestVisible = false;
            AppMotion.ResetPage(outgoing);
            Panel.SetZIndex(incoming, 0);
        });
    }
}
