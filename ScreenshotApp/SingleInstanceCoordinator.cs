namespace ScreenshotApp;

/// <summary>
/// 使用 Windows 命名互斥体限制同一登录会话只运行一个截影实例，
/// 并通过命名事件通知已有实例恢复主窗口。
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private readonly RegisteredWaitHandle? _activationRegistration;
    private bool _ownsMutex;

    internal SingleInstanceCoordinator(
        string mutexName,
        string activationEventName,
        Action activationRequested)
    {
        _mutex = new Mutex(true, mutexName, out var createdNew);
        IsPrimaryInstance = createdNew;
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            return;
        }

        _activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            activationEventName);
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    activationRequested();
                }
            },
            null,
            Timeout.Infinite,
            false);
    }

    internal bool IsPrimaryInstance { get; }

    internal bool NotifyPrimaryInstance(string activationEventName)
    {
        if (IsPrimaryInstance)
        {
            return false;
        }

        // 首个实例创建互斥体与激活事件之间存在极短窗口，稍作重试即可覆盖。
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(activationEventName);
                return activationEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(40);
            }
        }

        return false;
    }

    public void Dispose()
    {
        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 进程退出期间互斥体可能已经由系统回收。
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
