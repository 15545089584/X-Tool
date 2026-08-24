using System.Windows;

namespace ScreenshotApp.DesktopPet;

internal static class DesktopPetPlacement
{
    internal static Point SnapToNearestEdge(Rect windowBounds, Rect workingArea, double margin)
    {
        margin = Math.Max(0, margin);
        var minimumLeft = workingArea.Left + margin;
        var maximumLeft = Math.Max(minimumLeft, workingArea.Right - windowBounds.Width - margin);
        var minimumTop = workingArea.Top + margin;
        var maximumTop = Math.Max(minimumTop, workingArea.Bottom - windowBounds.Height - margin);
        var clampedLeft = Math.Clamp(windowBounds.Left, minimumLeft, maximumLeft);
        var clampedTop = Math.Clamp(windowBounds.Top, minimumTop, maximumTop);

        var candidates = new[]
        {
            new EdgeCandidate(new Point(minimumLeft, clampedTop), Math.Abs(windowBounds.Left - minimumLeft)),
            new EdgeCandidate(new Point(maximumLeft, clampedTop), Math.Abs(windowBounds.Left - maximumLeft)),
            new EdgeCandidate(new Point(clampedLeft, minimumTop), Math.Abs(windowBounds.Top - minimumTop)),
            new EdgeCandidate(new Point(clampedLeft, maximumTop), Math.Abs(windowBounds.Top - maximumTop))
        };

        return candidates.OrderBy(candidate => candidate.Distance).First().Position;
    }

    private readonly record struct EdgeCandidate(Point Position, double Distance);
}
