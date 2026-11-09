#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
using Tbox.Core;

namespace Tbox.Desktop.Platform;

internal sealed class WindowsHotkeys : IDisposable
{
    private readonly Action<HotkeyAction> _callback;
    private readonly WindowProc _windowProc;
    private readonly string _className = $"Tbox.Desktop.Hotkeys.{Guid.NewGuid():N}";
    private readonly nint _module = GetModuleHandle(null);
    private nint _window;
    private bool _disposed;

    public WindowsHotkeys(Action<HotkeyAction> callback)
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

    // Registers one shortcut under its action id; false when the system or another application holds it.
    // RegisterHotKey keeps an older combination under the same id, so the caller unregisters first.
    public bool Register(HotkeyBinding binding)
    {
        var shortcut = binding.Shortcut;
        // MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_WIN 8; MOD_NOREPEAT prevents key-repeat.
        var modifiers = 0x4000u | (shortcut.Alt ? 1u : 0) | (shortcut.Control ? 2u : 0) | (shortcut.Shift ? 4u : 0) | (shortcut.Meta ? 8u : 0);
        return ShortcutPolicy.WindowsKeyCode(shortcut.Key) is { } key && RegisterHotKey(_window, (int)binding.Action, modifiers, (uint)key);
    }

    public void Unregister(HotkeyAction action) => UnregisterHotKey(_window, (int)action);

    private nint Dispatch(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0x0312)
        {
            // Never let managed exceptions cross an unmanaged window-procedure boundary.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _callback((HotkeyAction)(int)wParam)); return 0;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var action in ShortcutPolicy.Actions) UnregisterHotKey(_window, (int)action);
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
