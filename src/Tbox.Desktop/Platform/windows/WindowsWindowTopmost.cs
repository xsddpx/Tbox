#if WINDOWS
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Tbox.Desktop.Platform;

internal sealed class WindowsWindowTopmost : IWindowTopmost
{
    private const uint PreservePlacement = 0x0001 | 0x0002 | 0x0010 | 0x0200 | 0x0400;
    private readonly string _property = $"Tbox.WindowTopmost.{Guid.NewGuid():N}";
    private readonly Dictionary<nint, Entry> _windows = [];
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
    private WindowTopmostBorder? _border;
    private bool _showBorder = true;

    private sealed class Entry(string title)
    {
        public string Title { get; } = title;
        public nint Border { get; set; }
        public (int Left, int Top, int Width, int Height, int Thickness) Geometry { get; set; }
        public bool BorderVisible { get; set; }
    }

    public WindowsWindowTopmost() => _timer.Tick += (_, _) => Refresh();
    public IReadOnlyList<TopmostWindow> Windows => _windows.Select(pair => new TopmostWindow(pair.Key, pair.Value.Title)).ToArray();
    public event Action<string?>? Changed;

    public void ToggleForeground()
    {
        var window = NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), NativeMethods.GaRoot);
        if (_windows.ContainsKey(window) && IsOwned(window) && IsTopmost(window)) { Remove(window); return; }
        NativeMethods.GetWindowThreadProcessId(window, out var process);
        if (window == 0 || process == Environment.ProcessId || window == GetShellWindow() ||
            !NativeMethods.IsWindowVisible(window) || NativeMethods.IsWindowCloaked(window) || GetWindow(window, 4) != 0)
        {
            Changed?.Invoke("请先选中要置顶的应用主窗口。");
            return;
        }
        if (IsTopmost(window)) { Changed?.Invoke("当前窗口已经置顶。"); return; }
        if (HasTopmostOwnedWindow(window)) { Changed?.Invoke("请先解除该应用自身的浮窗置顶。"); return; }
        // The property is deleted by Windows when the HWND dies, even if the same process reuses its handle.
        if (!SetProp(window, _property, 1)) { Changed?.Invoke("系统拒绝置顶当前窗口。"); return; }
        if (!SetWindowPos(window, -1, 0, 0, 0, 0, PreservePlacement) || !IsTopmost(window))
        {
            RemoveProp(window, _property);
            Changed?.Invoke("系统拒绝置顶当前窗口。");
            return;
        }
        Drop(window);
        var text = new char[1024];
        var count = NativeMethods.GetWindowText(window, text, text.Length);
        _windows.Add(window, new Entry(count > 0 ? new string(text, 0, count) : "未命名窗口"));
        _timer.Start();
        Refresh();
        Changed?.Invoke(null);
    }

    public void SetBorderVisible(bool visible)
    {
        _showBorder = visible;
        Refresh();
    }

    public void Remove(long id)
    {
        var window = (nint)id;
        if (!_windows.ContainsKey(window)) return;
        Changed?.Invoke(TryRemove(window) ? null : "系统未能解除该窗口的置顶，请重试。");
    }

    public void Clear()
    {
        if (_windows.Count == 0) return;
        var failed = false;
        foreach (var window in _windows.Keys.ToArray()) failed |= !TryRemove(window);
        Changed?.Invoke(failed ? "部分窗口的置顶未能解除，请重试。" : null);
    }

    private bool TryRemove(nint window)
    {
        if (IsOwned(window))
        {
            if (IsTopmost(window) && (!SetWindowPos(window, -2, 0, 0, 0, 0, PreservePlacement) || IsTopmost(window))) return false;
            RemoveProp(window, _property);
        }
        Drop(window);
        return true;
    }

    private void Drop(nint window)
    {
        if (_windows.Remove(window, out var entry) && entry.Border != 0) DestroyWindow(entry.Border);
        if (_windows.Count == 0) _timer.Stop();
    }

    private bool IsOwned(nint window) => GetProp(window, _property) == 1;
    private static bool IsTopmost(nint window) => (GetWindowLongPtr(window, -20) & 8) != 0;

    private static bool HasTopmostOwnedWindow(nint window)
    {
        // Demoting an owner also demotes its owned windows. Preserve pre-existing floating panels.
        var found = false;
        NativeMethods.EnumWindows((candidate, _) =>
        {
            found = candidate != window && IsTopmost(candidate) && NativeMethods.GetAncestor(candidate, 3) == window;
            return !found;
        }, 0);
        return found;
    }

    private void Refresh()
    {
        var removed = false;
        foreach (var (window, entry) in _windows)
        {
            if (!IsOwned(window) || !IsTopmost(window))
            {
                if (IsOwned(window)) RemoveProp(window, _property);
                Drop(window);
                removed = true;
                continue;
            }
            RefreshBorder(window, entry);
        }
        if (removed) Changed?.Invoke(null);
    }

    private void RefreshBorder(nint window, Entry entry)
    {
        if (!_showBorder || NativeMethods.IsIconic(window) || !NativeMethods.IsWindowVisible(window) || NativeMethods.IsWindowCloaked(window) ||
            !(NativeMethods.TryGetExtendedFrameBounds(window, out var bounds) || NativeMethods.GetWindowRect(window, out bounds)) ||
            bounds.right <= bounds.left || bounds.bottom <= bounds.top)
        {
            if (entry.BorderVisible) ShowWindow(entry.Border, 0);
            entry.BorderVisible = false;
            return;
        }
        var thickness = Math.Max(2, (int)Math.Round(3 * NativeMethods.GetDpiForWindow(window) / 96.0));
        var width = bounds.right - bounds.left + 2 * thickness;
        var height = bounds.bottom - bounds.top + 2 * thickness;
        var geometry = (Left: bounds.left - thickness, Top: bounds.top - thickness, width, height, thickness);
        _border ??= new WindowTopmostBorder();
        if (entry.Border == 0) entry.Border = _border.Create();
        if (entry.Border == 0) return;
        if (entry.Geometry != geometry)
        {
            if (!WindowTopmostBorder.SetOutline(entry.Border, width, height, thickness)) return;
            entry.Geometry = geometry;
        }
        // Stay directly behind the source in the same band, so another topmost window can cover both.
        if (!entry.BorderVisible || GetWindow(window, 2) != entry.Border ||
            !NativeMethods.GetWindowRect(entry.Border, out var shown) || shown.left != geometry.Left || shown.top != geometry.Top ||
            shown.right - shown.left != width || shown.bottom - shown.top != height)
            entry.BorderVisible = SetWindowPos(entry.Border, window, geometry.Left, geometry.Top, width, height, 0x0010 | 0x0040 | 0x0200);
    }

    public void Dispose()
    {
        Clear();
        foreach (var window in _windows.Keys.ToArray()) { RemoveProp(window, _property); Drop(window); }
        _timer.Stop();
        _border?.Dispose();
    }

    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode)] private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
}

internal sealed class WindowTopmostBorder : IDisposable
{
    private readonly string _name = $"Tbox.WindowTopmostBorder.{Guid.NewGuid():N}";
    private readonly nint _instance = GetModuleHandle(null);
    private readonly nint _brush = CreateSolidBrush(0xE84A6D); // COLORREF for #6D4AE8.
    private readonly BorderProc _procedure = Dispatch;
    private readonly bool _registered;

    public WindowTopmostBorder()
    {
        var definition = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = _instance,
            ClassName = _name, WindowProc = Marshal.GetFunctionPointerForDelegate(_procedure), Background = _brush };
        _registered = _brush != 0 && RegisterClassEx(ref definition) != 0;
    }

    public nint Create()
    {
        if (!_registered) return 0;
        const uint topmostClickThrough = 0x00000008 | 0x00000020 | 0x00000080 | 0x00080000 | 0x08000000;
        var border = CreateWindowEx(topmostClickThrough, _name, "", 0x80000000, 0, 0, 1, 1, 0, 0, _instance, 0);
        if (border == 0) return 0;
        SetLayeredWindowAttributes(border, 0, 255, 2);
        NativeMethods.ExcludeWindowFromCapture(border);
        return border;
    }

    public static bool SetOutline(nint border, int width, int height, int thickness)
    {
        var outside = CreateRectRgn(0, 0, width, height);
        var inside = CreateRectRgn(thickness, thickness, width - thickness, height - thickness);
        var transferred = outside != 0 && inside != 0 && CombineRgn(outside, outside, inside, 4) != 0 && SetWindowRgn(border, outside, true) != 0;
        if (inside != 0) DeleteObject(inside);
        if (outside != 0 && !transferred) DeleteObject(outside);
        return transferred;
    }

    private static nint Dispatch(nint window, uint message, nint wParam, nint lParam) => message switch
    {
        0x0084 => -1, // HTTRANSPARENT
        0x0021 => 3,  // MA_NOACTIVATE
        _ => DefWindowProc(window, message, wParam, lParam)
    };

    public void Dispose()
    {
        if (_registered) UnregisterClass(_name, _instance); // UnregisterClass owns the registered background brush.
        else if (_brush != 0) DeleteObject(_brush);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint BorderProc(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style;
        public nint WindowProc;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass definition);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint result, nint first, nint second, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}
#endif
