using System.Runtime.InteropServices;
using Tbox.Core;

namespace Tbox.App;

internal sealed class ExplorerFolderTracker : IDisposable
{
    private readonly NativeMethods.WinEventDelegate _callback;
    private readonly string _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly object _captureLock = new();
    private readonly object _stateLock = new();
    private IntPtr _hook;
    private IntPtr _pendingWindow;
    private IntPtr _lastExplorerWindow;
    private bool _captureWorkerScheduled;
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

        lock (_stateLock) _stopRequested = false;

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

        lock (_stateLock)
        {
            _stopRequested = true;
            _pendingWindow = IntPtr.Zero;
        }
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time)
    {
        var explorerWindow = GetExplorerRoot(hwnd);
        if (explorerWindow != IntPtr.Zero)
        {
            QueueCapture(explorerWindow, rememberWindow: true);
            return;
        }

        // A directory change does not raise EVENT_SYSTEM_FOREGROUND while the
        // same Explorer window remains active. Refresh that window when focus
        // moves away so the latest location is normally ready before Ctrl+G.
        IntPtr lastExplorerWindow;
        lock (_stateLock)
        {
            lastExplorerWindow = _lastExplorerWindow;
        }
        if (lastExplorerWindow != IntPtr.Zero)
        {
            QueueCapture(lastExplorerWindow, rememberWindow: false);
        }
    }

    private void QueueCapture(IntPtr hwnd, bool rememberWindow = true)
    {
        var explorerWindow = GetExplorerRoot(hwnd);
        if (explorerWindow == IntPtr.Zero) return;
        var scheduleWorker = false;
        lock (_stateLock)
        {
            if (_stopRequested) return;
            if (rememberWindow) _lastExplorerWindow = explorerWindow;
            _pendingWindow = explorerWindow;
            if (!_captureWorkerScheduled)
            {
                _captureWorkerScheduled = true;
                scheduleWorker = true;
            }
        }
        if (scheduleWorker)
        {
            ThreadPool.QueueUserWorkItem(_ => DrainCaptures());
        }
    }

    internal void CaptureWindowForTesting(IntPtr hwnd)
    {
        QueueCapture(hwnd);
    }

    internal string RefreshCurrentFolder()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IntPtr explorerWindow;
        lock (_stateLock)
        {
            explorerWindow = _lastExplorerWindow;
        }
        if (explorerWindow != IntPtr.Zero)
        {
            CaptureSerialized(explorerWindow);
        }
        return CurrentFolder;
    }

    private static IntPtr GetExplorerRoot(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot);
        if (root != IntPtr.Zero) hwnd = root;
        var className = new char[64];
        var length = NativeMethods.GetClassName(hwnd, className, className.Length);
        if (length == 0) return IntPtr.Zero;
        var name = new string(className, 0, length);
        return name is "CabinetWClass" or "ExploreWClass" ? hwnd : IntPtr.Zero;
    }

    private void DrainCaptures()
    {
        while (true)
        {
            IntPtr window;
            lock (_stateLock)
            {
                if (_stopRequested)
                {
                    _captureWorkerScheduled = false;
                    return;
                }
                window = _pendingWindow;
                _pendingWindow = IntPtr.Zero;
                if (window == IntPtr.Zero)
                {
                    _captureWorkerScheduled = false;
                    return;
                }
            }
            try
            {
                CaptureSerialized(window);
            }
            catch (Exception exception)
            {
                // A single unexpected Shell/COM failure must not permanently
                // disable future capture scheduling.
                AppLog.Write($"Explorer capture worker failed: {exception}");
            }
        }
    }

    private void CaptureSerialized(IntPtr hwnd)
    {
        lock (_captureLock)
        {
            Capture(hwnd);
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
                    string? shellPath = null;
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        shellPath = item.Document?.Folder?.Self?.Path;
                        if (IsStableShellPath(shellPath) || attempt == 3) break;
                        Thread.Sleep(25);
                    }
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
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            AppLog.Write($"Explorer capture failed: {exception.GetType().Name}");
        }
    }

    private static bool IsStableShellPath(string? shellPath)
    {
        if (string.IsNullOrWhiteSpace(shellPath)) return false;
        return !Path.IsPathRooted(shellPath) || Directory.Exists(shellPath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
