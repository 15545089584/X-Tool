using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.DesktopPet;

internal static class DesktopPetValidationRunner
{
    internal static async Task<DesktopPetValidationReport> RunAsync(
        DesktopPetWindow window,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        await WaitForStateAsync(window, "state-01", cancellationToken);
        var process = Process.GetCurrentProcess();
        var stateResults = new List<DesktopPetStateValidation>();
        Point? referencePosition = null;
        foreach (var stateId in window.StateIds)
        {
            await window.SwitchStateForValidationAsync(stateId, cancellationToken);
            await Task.Delay(220, cancellationToken);
            var initialFrame = window.CurrentFrameIndex;
            await Task.Delay(130, cancellationToken);
            var laterFrame = window.CurrentFrameIndex;
            var position = new Point(window.Left, window.Top);
            referencePosition ??= position;
            var snapshot = Path.Combine(outputDirectory, $"desktop-pet-{stateId}.png");
            CaptureWindow(window, snapshot);
            process.Refresh();
            stateResults.Add(new DesktopPetStateValidation(
                stateId,
                initialFrame,
                laterFrame,
                initialFrame != laterFrame,
                position,
                position == referencePosition.Value,
                window.CachedDecodedBytes,
                process.PrivateMemorySize64,
                snapshot));
        }

        var workingArea = window.GetCurrentWorkingAreaForValidation();
        window.Left = workingArea.Left + Math.Max(0, (workingArea.Width - window.ActualWidth) / 2);
        window.Top = workingArea.Top + Math.Max(0, (workingArea.Height - window.ActualHeight) / 2);
        window.SnapToNearestEdge();
        var snappedPosition = new Point(window.Left, window.Top);
        var edgeDistance = CalculateNearestEdgeDistance(window, workingArea);

        await window.SetScalePercentForValidationAsync(60, cancellationToken);
        await Task.Delay(180, cancellationToken);
        window.UpdateLayout();
        var smallSize = new Size(window.ActualWidth, window.ActualHeight);
        var smallEdgeDistance = CalculateNearestEdgeDistance(window, workingArea);
        var smallSnapshot = Path.Combine(outputDirectory, "desktop-pet-size-60.png");
        CaptureWindow(window, smallSnapshot);

        await window.SetScalePercentForValidationAsync(160, cancellationToken);
        await Task.Delay(180, cancellationToken);
        window.UpdateLayout();
        var largeSize = new Size(window.ActualWidth, window.ActualHeight);
        var largeEdgeDistance = CalculateNearestEdgeDistance(window, workingArea);
        var largeSnapshot = Path.Combine(outputDirectory, "desktop-pet-size-160.png");
        CaptureWindow(window, largeSnapshot);
        process.Refresh();
        var memoryAtLargeSize = process.PrivateMemorySize64;

        await window.SetScalePercentForValidationAsync(100, cancellationToken);
        await window.SwitchStateForValidationAsync("state-01", cancellationToken);

        var report = new DesktopPetValidationReport(
            DateTimeOffset.Now,
            stateResults,
            stateResults.All(result => result.PlaybackAdvanced),
            stateResults.All(result => result.PositionStable),
            workingArea,
            snappedPosition,
            edgeDistance,
            smallSize,
            largeSize,
            smallEdgeDistance,
            largeEdgeDistance,
            memoryAtLargeSize,
            smallSnapshot,
            largeSnapshot);

        var reportPath = Path.Combine(outputDirectory, "desktop-pet-validation.json");
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return report;
    }

    private static async Task WaitForStateAsync(
        DesktopPetWindow window,
        string stateId,
        CancellationToken cancellationToken)
    {
        var timeout = Stopwatch.StartNew();
        while (!string.Equals(window.CurrentStateId, stateId, StringComparison.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeout.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException($"等待桌面宠物状态 {stateId} 超时。");
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private static void CaptureWindow(DesktopPetWindow window, string outputPath)
    {
        var width = Math.Max(1, (int)Math.Round(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Round(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static double CalculateNearestEdgeDistance(DesktopPetWindow window, Rect workingArea)
    {
        var right = window.Left + window.ActualWidth;
        var bottom = window.Top + window.ActualHeight;
        return new[]
        {
            Math.Abs(window.Left - workingArea.Left),
            Math.Abs(right - workingArea.Right),
            Math.Abs(window.Top - workingArea.Top),
            Math.Abs(bottom - workingArea.Bottom)
        }.Min();
    }
}

internal sealed record DesktopPetValidationReport(
    DateTimeOffset CapturedAt,
    IReadOnlyList<DesktopPetStateValidation> States,
    bool AllStatesAdvanced,
    bool PositionStableAcrossStateSwitches,
    Rect WorkingArea,
    Point SnappedPosition,
    double NearestEdgeDistance,
    Size SmallSize,
    Size LargeSize,
    double SmallNearestEdgeDistance,
    double LargeNearestEdgeDistance,
    long PrivateMemoryAtLargeSize,
    string SmallSnapshot,
    string LargeSnapshot);

internal sealed record DesktopPetStateValidation(
    string StateId,
    int InitialFrame,
    int LaterFrame,
    bool PlaybackAdvanced,
    Point Position,
    bool PositionStable,
    long CachedDecodedBytes,
    long PrivateMemoryBytes,
    string Snapshot);
