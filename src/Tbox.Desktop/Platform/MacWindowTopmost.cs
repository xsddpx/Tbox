using System.Runtime.InteropServices;

namespace Tbox.Desktop.Platform;

internal sealed class MacWindowTopmost : IWindowTopmost
{
    private const string Library = "TboxNative";
    private readonly MacPlatformServices _platform;
    private readonly ChangeCallback _callback;
    private TopmostWindow[] _windows = [];

    public MacWindowTopmost(MacPlatformServices platform)
    {
        _platform = platform;
        _callback = OnChanged;
        NativeWatch(_callback);
    }

    public IReadOnlyList<TopmostWindow> Windows => _windows;
    public bool NeedsScreenPermission => !_platform.HasScreenPermission;
    public bool NeedsAccessibilityPermission => NativeAccessibilityPermission() == 0;
    public void RequestAccessibilityPermission() => NativeRequestAccessibilityPermission();
    public event Action<string?>? Changed;
    public void ToggleForeground() => NativeToggle();
    public void SetBorderVisible(bool visible) => NativeBorder(visible ? 1 : 0);
    public void Remove(long id) => NativeRemove((uint)id);
    public void Clear() => NativeClear();

    private void OnChanged(nint message)
    {
        try
        {
            _windows = MacPlatformServices.ReadJson<Snapshot>(NativeWindows()).Windows;
            Changed?.Invoke(Marshal.PtrToStringUTF8(message));
        }
        catch (Exception error) { AppLog.Write("Window topmost update: " + error.GetType().Name); }
    }

    public void Dispose()
    {
        NativeWatch(null);
        NativeClear();
        _windows = [];
    }

    private sealed record Snapshot(TopmostWindow[] Windows);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ChangeCallback(nint message);
    [DllImport(Library, EntryPoint = "tbox_topmost_watch")] private static extern void NativeWatch(ChangeCallback? callback);
    [DllImport(Library, EntryPoint = "tbox_topmost_windows")] private static extern nint NativeWindows();
    [DllImport(Library, EntryPoint = "tbox_topmost_toggle")] private static extern void NativeToggle();
    [DllImport(Library, EntryPoint = "tbox_topmost_border")] private static extern void NativeBorder(int visible);
    [DllImport(Library, EntryPoint = "tbox_topmost_remove")] private static extern void NativeRemove(uint id);
    [DllImport(Library, EntryPoint = "tbox_topmost_clear")] private static extern void NativeClear();
    [DllImport(Library, EntryPoint = "tbox_folder_permission")] private static extern int NativeAccessibilityPermission();
    [DllImport(Library, EntryPoint = "tbox_enable_elements")] private static extern int NativeRequestAccessibilityPermission();
}
