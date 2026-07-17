namespace Tbox.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\Tbox.Singleton.v1";
    private const string ActivationEventName = "Local\\Tbox.Activate.v1";
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private readonly ManualResetEvent _stopEvent = new(false);
    private Task? _listener;

    internal bool IsPrimary { get; }

    internal SingleInstanceCoordinator()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        IsPrimary = createdNew;
        if (createdNew)
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        }
    }

    internal void SignalPrimary()
    {
        if (IsPrimary) return;
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException) when (attempt < 10)
            {
                // The primary may own the mutex before its activation event is ready.
                Thread.Sleep(50);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                AppLog.Write("Primary instance activation event was not available after 500 ms.");
            }
        }
    }

    internal void Listen(Action activate)
    {
        if (!IsPrimary || _activationEvent is null || _listener is not null) return;
        _listener = Task.Run(() =>
        {
            var handles = new WaitHandle[] { _activationEvent, _stopEvent };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                try { activate(); }
                catch (Exception exception) { AppLog.Write($"Instance activation failed: {exception}"); }
            }
        });
    }

    public void Dispose()
    {
        _stopEvent.Set();
        _listener?.Wait(TimeSpan.FromSeconds(1));
        _activationEvent?.Dispose();
        _stopEvent.Dispose();
        if (IsPrimary)
        {
            try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex?.Dispose();
    }
}
