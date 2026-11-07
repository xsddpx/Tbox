#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Tbox.Desktop.Platform;

internal sealed class WindowsHotkeys : IDisposable
{
    private readonly Action<int> _callback;
    private readonly WindowProc _windowProc;
    private readonly string _className = $"Tbox.Desktop.Hotkeys.{Guid.NewGuid():N}";
    private readonly nint _module = GetModuleHandle(null);
    private nint _window;
    private bool _disposed;

    public WindowsHotkeys(Action<int> callback)
    {
        _callback = callback; _windowProc = Dispatch;
        var definition = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = _module,
            ClassName = _className, WindowProc = Marshal.GetFunctionPointerForDelegate(_windowProc) };
        if (RegisterClassEx(ref definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _window = CreateWindowEx(0, _className, "Tbox hotkeys", 0, 0, 0, 0, 0, -3, 0, _module, 0);
        if (_window == 0)
        {
            var error = Marshal.GetLastWin32Error(); UnregisterClass(_className, _module); throw new Win32Exception(error);
        }
    }

    public bool Register(int mask)
    {
        for (var id = 1; id <= 4; id++)
        {
            if ((mask & (1 << (id - 1))) == 0) continue;
            // Preserve existing Windows shortcuts F6/F7/F8. MOD_NOREPEAT prevents key-repeat.
            if (RegisterHotKey(_window, id, id == 4 ? 0x4002u : 0x4000u, id == 4 ? 0x47u : (uint)(0x74 + id))) continue;
            for (var previous = 1; previous < id; previous++) UnregisterHotKey(_window, previous);
            return false;
        }
        return true;
    }

    private nint Dispatch(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0x0312)
        {
            // Never let managed exceptions cross an unmanaged window-procedure boundary.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _callback((int)wParam)); return 0;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var id = 1; id <= 4; id++) UnregisterHotKey(_window, id);
        DestroyWindow(_window); _window = 0; UnregisterClass(_className, _module);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public nint WindowProc; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass definition);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint module, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
#endif
