using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Tbox.App;

internal delegate bool WindowMessageHandler(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);

internal sealed class WindowMessageRouter : IDisposable
{
    private static readonly ConcurrentDictionary<IntPtr, WindowMessageRouter> Windows = new();
    private static readonly WindowProc StaticWindowProc = DispatchWindowMessage;
    private readonly List<WindowMessageHandler> _handlers = [];
    private readonly string _className;
    private readonly IntPtr _module;
    private bool _disposed;

    internal IntPtr Handle { get; private set; }

    internal WindowMessageRouter()
    {
        _module = GetModuleHandle(null);
        _className = $"Tbox.MessageWindow.{Environment.ProcessId}";
        var windowClass = new WindowClass
        {
            size = (uint)Marshal.SizeOf<WindowClass>(),
            instance = _module,
            className = _className,
            windowProc = Marshal.GetFunctionPointerForDelegate(StaticWindowProc)
        };
        if (RegisterClassEx(ref windowClass) == 0)
        {
            throw new InvalidOperationException($"无法注册消息窗口，Win32={Marshal.GetLastWin32Error()}。");
        }

        Handle = CreateWindowEx(
            0,
            _className,
            "Tbox Message Window",
            0,
            0,
            0,
            0,
            0,
            new IntPtr(-3),
            IntPtr.Zero,
            _module,
            IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            UnregisterClass(_className, _module);
            throw new InvalidOperationException($"无法创建消息窗口，Win32={error}。");
        }
        Windows[Handle] = this;
    }

    internal IDisposable Subscribe(WindowMessageHandler handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    private static IntPtr DispatchWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Windows.TryGetValue(hwnd, out var owner))
        {
            foreach (var handler in owner._handlers.ToArray())
            {
                try
                {
                    if (handler(message, wParam, lParam, out var result)) return result;
                }
                catch (Exception exception)
                {
                    AppLog.Write($"Window message handler failed: {exception}");
                }
            }
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void Unsubscribe(WindowMessageHandler handler) => _handlers.Remove(handler);

    public void Dispose()
    {
        if (_disposed) return;
        _handlers.Clear();
        if (Handle != IntPtr.Zero)
        {
            Windows.TryRemove(Handle, out _);
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
        UnregisterClass(_className, _module);
        _disposed = true;
    }

    private sealed class Subscription(WindowMessageRouter owner, WindowMessageHandler handler) : IDisposable
    {
        private WindowMessageRouter? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(handler);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint size;
        internal uint style;
        internal IntPtr windowProc;
        internal int classExtra;
        internal int windowExtra;
        internal IntPtr instance;
        internal IntPtr icon;
        internal IntPtr cursor;
        internal IntPtr background;
        internal string? menuName;
        internal string className;
        internal IntPtr smallIcon;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
