#if WINDOWS
namespace Tbox.Desktop.Platform;

internal sealed class WindowsDialogObservation : IDisposable
{
    private const uint MoveSizeEnd = 0x000B;
    private const uint ObjectDestroy = 0x8001;
    private const uint ObjectLocationChange = 0x800B;
    private readonly nint _window;
    private readonly Action<FolderDialogChange> _changed;
    private readonly NativeMethods.WinEventDelegate _callback;
    private readonly List<nint> _hooks = [];
    private bool _disposed;

    private WindowsDialogObservation(nint window, Action<FolderDialogChange> changed)
    {
        _window = window;
        _changed = changed;
        _callback = OnEvent;
    }

    public static WindowsDialogObservation? TryStart(nint window, Action<FolderDialogChange> changed)
    {
        var thread = NativeMethods.GetWindowThreadProcessId(window, out var process);
        if (thread == 0) return null;
        var observation = new WindowsDialogObservation(window, changed);
        if (observation.Hook(ObjectLocationChange, process, thread) &&
            observation.Hook(MoveSizeEnd, process, thread) &&
            observation.Hook(ObjectDestroy, process, thread)) return observation;
        observation.Dispose();
        return null;
    }

    private bool Hook(uint type, uint process, uint thread)
    {
        // Own file pickers run on the UI STA and must produce events too.
        var hook = NativeMethods.SetWinEventHook(type, type, 0, _callback, process, thread, NativeMethods.WineventOutofcontext);
        if (hook == 0) return false;
        _hooks.Add(hook);
        return true;
    }

    private void OnEvent(nint hook, uint type, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || objectId != 0 || childId != 0) return;
        if (window == _window)
        {
            _changed(type switch
            {
                ObjectLocationChange => FolderDialogChange.Position,
                MoveSizeEnd => FolderDialogChange.Settled,
                _ => FolderDialogChange.Closed
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var hook in _hooks) NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
    }
}
#endif
