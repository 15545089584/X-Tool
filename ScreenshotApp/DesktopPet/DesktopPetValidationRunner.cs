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
        await Task.Delay(300, cancellationToken);

        var initialFrame = window.CurrentFrameIndex;
        await Task.Delay(180, cancellationToken);
        var laterFrame = window.CurrentFrameIndex;
        var state01Position = new Point(window.Left, window.Top);
        var state01Snapshot = Path.Combine(outputDirectory, "desktop-pet-state-01.png");
        CaptureWindow(window, state01Snapshot);

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var memoryAfterState01 = process.PrivateMemorySize64;
        var state01DecodedBytes = window.CachedDecodedBytes;

        await window.SwitchStateForValidationAsync("state-06", cancellationToken);
        await Task.Delay(300, cancellationToken);
        var state06Position = new Point(window.Left, window.Top);
        var state06Snapshot = Path.Combine(outputDirectory, "desktop-pet-state-06.png");
        CaptureWindow(window, state06Snapshot);
        process.Refresh();
        var memoryAfterState06 = process.PrivateMemorySize64;
        var bothStatesDecodedBytes = window.CachedDecodedBytes;

        var workingArea = window.GetCurrentWorkingAreaForValidation();
        window.Left = workingArea.Left + Math.Max(0, (workingArea.Width - window.ActualWidth) / 2);
        window.Top = workingArea.Top + Math.Max(0, (workingArea.Height - window.ActualHeight) / 2);
        window.SnapToNearestEdge();
        var snappedPosition = new Point(window.Left, window.Top);
        var edgeDistance = CalculateNearestEdgeDistance(window, workingArea);

        var report = new DesktopPetValidationReport(
            DateTimeOffset.Now,
            initialFrame,
            laterFrame,
            initialFrame != laterFrame,
            state01Position,
            state06Position,
            state01Position == state06Position,
            workingArea,
            snappedPosition,
            edgeDistance,
            memoryAfterState01,
            memoryAfterState06,
            state01DecodedBytes,
            bothStatesDecodedBytes,
            state01Snapshot,
            state06Snapshot);

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
    int InitialFrame,
    int LaterFrame,
    bool PlaybackAdvanced,
    Point State01Position,
    Point State06Position,
    bool PositionStableAcrossStateSwitch,
    Rect WorkingArea,
    Point SnappedPosition,
    double NearestEdgeDistance,
    long PrivateMemoryAfterState01,
    long PrivateMemoryAfterState06,
    long DecodedBytesAfterState01,
    long DecodedBytesAfterState06,
    string State01Snapshot,
    string State06Snapshot);
