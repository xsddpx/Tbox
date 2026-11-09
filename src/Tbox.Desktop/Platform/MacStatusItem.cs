using System.Runtime.InteropServices;

namespace Tbox.Desktop.Platform;

// macOS menu bar item owned by Tbox, because Avalonia's status item cannot report a plain click.
internal sealed class MacStatusItem : IDisposable
{
    private const string Library = "TboxNative";
    private readonly StatusCallback _callback;
    private readonly Action _clicked;
    private readonly Action<int> _chose;

    public MacStatusItem(byte[] templatePng, string tooltip, IReadOnlyList<string?> menu, Action clicked, Action<int> chose)
    {
        _clicked = clicked; _chose = chose;
        _callback = Dispatch;
        var titles = string.Join('\n', menu.Select(title => title ?? ""));
        if (NativeCreate(templatePng, templatePng.Length, tooltip, titles, _callback) == 0)
            throw new InvalidOperationException("无法创建菜单栏图标。");
    }

    // Menu entries are numbered from 1 in the order given, skipping null separators.
    private void Dispatch(int command)
    {
        if (command == 0) _clicked(); else _chose(command);
    }

    public DesktopPoint? Anchor
    {
        get
        {
            try { return MacPlatformServices.ReadJson<DesktopPoint>(NativeAnchor()); }
            catch (InvalidOperationException) { return null; }
        }
    }

    public void Dispose() => NativeRemove();
    // Menu choices are numbered from 1, as in Dispatch.
    public void Retitle(int command, string title) => NativeRetitle(command, title);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StatusCallback(int command);
    [DllImport(Library, EntryPoint = "tbox_status_item_create")]
    private static extern int NativeCreate(byte[] png, int length, [MarshalAs(UnmanagedType.LPUTF8Str)] string tooltip,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string titles, StatusCallback callback);
    [DllImport(Library, EntryPoint = "tbox_status_item_remove")] private static extern void NativeRemove();
    [DllImport(Library, EntryPoint = "tbox_status_item_retitle")] private static extern void NativeRetitle(int command, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport(Library, EntryPoint = "tbox_status_item_anchor")] private static extern nint NativeAnchor();
}
