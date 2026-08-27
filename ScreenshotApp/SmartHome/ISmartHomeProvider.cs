namespace ScreenshotApp.SmartHome;

public interface ISmartHomeProvider : IAsyncDisposable
{
    event Action<SmartHomeSnapshot>? SnapshotChanged;

    event Action<Exception?>? Disconnected;

    SmartHomeSnapshot? CurrentSnapshot { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task ExecuteAsync(SmartHomeControlRequest request, CancellationToken cancellationToken);
}
