using System.Runtime.InteropServices;
using Tbox.Core;

namespace Tbox.App;

internal sealed class ExplorerFolderTracker : IDisposable
{
    private readonly NativeMethods.WinEventDelegate _callback;
    private readonly AutoResetEvent _captureRequested = new(false);
    private readonly string _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly object _stateLock = new();
    private Thread? _worker;
    private IntPtr _hook;
    private IntPtr _pendingWindow;
    private bool _stopRequested;
    private bool _disposed;
    private string _currentFolder;
    private string? _lastShellPath;

    internal string CurrentFolder => Volatile.Read(ref _currentFolder);
    internal string? LastShellPathForTesting => Volatile.Read(ref _lastShellPath);

    internal ExplorerFolderTracker()
    {
        _currentFolder = _userProfile;
        _callback = OnForegroundChanged;
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != IntPtr.Zero) return;

        lock (_stateLock)
        {
            _stopRequested = false;
            _worker = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "Tbox.ExplorerFolderTracker"
            };
            _worker.Start();
        }

        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground,
            NativeMethods.EventSystemForeground,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WineventOutofcontext);
        if (_hook == IntPtr.Zero)
        {
            Stop();
            throw new InvalidOperationException($"无法监听前台窗口，Win32={Marshal.GetLastWin32Error()}。");
        }

        QueueCapture(NativeMethods.GetForegroundWindow());
    }

    internal void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        Thread? worker;
        lock (_stateLock)
        {
            _stopRequested = true;
            worker = _worker;
            _worker = null;
        }
        _captureRequested.Set();
        worker?.Join(TimeSpan.FromSeconds(1));
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time)
        => QueueCapture(hwnd);

    private void QueueCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsExplorerWindow(hwnd)) return;
        lock (_stateLock)
        {
            if (_stopRequested) return;
            _pendingWindow = hwnd;
        }
        _captureRequested.Set();
    }

    internal void CaptureWindowForTesting(IntPtr hwnd)
    {
        lock (_stateLock)
        {
            _pendingWindow = hwnd;
        }
        _captureRequested.Set();
    }

    private static bool IsExplorerWindow(IntPtr hwnd)
    {
        var className = new char[64];
        var length = NativeMethods.GetClassName(hwnd, className, className.Length);
        if (length == 0) return false;
        var name = new string(className, 0, length);
        return name is "CabinetWClass" or "ExploreWClass";
    }

    private void CaptureLoop()
    {
        while (true)
        {
            _captureRequested.WaitOne();
            IntPtr window;
            lock (_stateLock)
            {
                if (_stopRequested) return;
                window = _pendingWindow;
                _pendingWindow = IntPtr.Zero;
            }
            if (window != IntPtr.Zero) Capture(window);
        }
    }

    private void Capture(IntPtr hwnd)
    {
        AppLog.Write("Explorer capture started.");
        var targetRoot = NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot);
        if (targetRoot == IntPtr.Zero) targetRoot = hwnd;
        object? shellObject = null;
        object? windowsObject = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return;
            shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null) return;
            dynamic shell = shellObject;
            windowsObject = shell.Windows();
            dynamic windows = windowsObject;
            var count = Convert.ToInt32(windows.Count);
            for (var index = 0; index < count; index++)
            {
                object? itemObject = null;
                try
                {
                    itemObject = windows.Item(index);
                    if (itemObject is null) continue;
                    dynamic item = itemObject;
                    var itemWindow = new IntPtr(Convert.ToInt64(item.HWND));
                    var itemRoot = NativeMethods.GetAncestor(itemWindow, NativeMethods.GaRoot);
                    if (itemRoot == IntPtr.Zero) itemRoot = itemWindow;
                    if (itemRoot != targetRoot) continue;
                    string? shellPath = item.Document?.Folder?.Self?.Path;
                    Volatile.Write(ref _lastShellPath, shellPath);
                    var resolved = FolderTargetPolicy.Resolve(shellPath, _userProfile);
                    Volatile.Write(ref _currentFolder, resolved);
                    AppLog.Write("Explorer folder captured.");
                    return;
                }
                catch (COMException)
                {
                    // Explorer windows may disappear while enumerating.
                }
                finally
                {
                    if (itemObject is not null && Marshal.IsComObject(itemObject))
                    {
                        Marshal.FinalReleaseComObject(itemObject);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            AppLog.Write($"Explorer capture failed: {exception.GetType().Name}");
        }
        finally
        {
            if (windowsObject is not null && Marshal.IsComObject(windowsObject))
            {
                Marshal.FinalReleaseComObject(windowsObject);
            }
            if (shellObject is not null && Marshal.IsComObject(shellObject))
            {
                Marshal.FinalReleaseComObject(shellObject);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _captureRequested.Dispose();
        _disposed = true;
    }
}
