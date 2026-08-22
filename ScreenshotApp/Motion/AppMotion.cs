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
    private static readonly TimeSpan PageEnterDuration = TimeSpan.FromMilliseconds(360);
    private static readonly TimeSpan PageExitDuration = TimeSpan.FromMilliseconds(230);

    public static void AnimatePageTransition(
        FrameworkElement? outgoing,
        FrameworkElement incoming,
        int direction,
        Action completed)
    {
        direction = direction < 0 ? -1 : 1;
        ResetPage(incoming);
        incoming.Visibility = Visibility.Visible;
        incoming.IsHitTestVisible = true;
        Panel.SetZIndex(incoming, 2);

        if (outgoing is not null && !ReferenceEquals(outgoing, incoming))
        {
            ResetPage(outgoing);
            outgoing.Visibility = Visibility.Visible;
            outgoing.IsHitTestVisible = false;
            Panel.SetZIndex(outgoing, 1);
            AnimatePageExit(outgoing, direction);
        }

        var (incomingScale, incomingTranslate) = EnsureMotionTransform(incoming);
        var enterEase = new QuarticEase { EasingMode = EasingMode.EaseOut };
        incoming.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = enterEase,
            FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
        incomingScale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnimation(0.982, 1, PageEnterDuration, enterEase, FillBehavior.Stop), HandoffBehavior.SnapshotAndReplace);
        incomingScale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnimation(0.982, 1, PageEnterDuration, enterEase, FillBehavior.Stop), HandoffBehavior.SnapshotAndReplace);
        incomingTranslate.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(7, 0, PageEnterDuration, enterEase, FillBehavior.Stop), HandoffBehavior.SnapshotAndReplace);

        var horizontal = CreateAnimation(direction * 34, 0, PageEnterDuration, enterEase, FillBehavior.Stop);
        horizontal.Completed += (_, _) => completed();
        incomingTranslate.BeginAnimation(TranslateTransform.XProperty, horizontal, HandoffBehavior.SnapshotAndReplace);
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

    private static void AnimatePageExit(FrameworkElement outgoing, int direction)
    {
        var (scale, translate) = EnsureMotionTransform(outgoing);
        var exitEase = new CubicEase { EasingMode = EasingMode.EaseIn };
        outgoing.BeginAnimation(UIElement.OpacityProperty, CreateAnimation(0, PageExitDuration, exitEase), HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnimation(0.992, PageExitDuration, exitEase), HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnimation(0.992, PageExitDuration, exitEase), HandoffBehavior.SnapshotAndReplace);
        translate.BeginAnimation(TranslateTransform.XProperty, CreateAnimation(direction * -14, PageExitDuration, exitEase), HandoffBehavior.SnapshotAndReplace);
        translate.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(-3, PageExitDuration, exitEase), HandoffBehavior.SnapshotAndReplace);
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
