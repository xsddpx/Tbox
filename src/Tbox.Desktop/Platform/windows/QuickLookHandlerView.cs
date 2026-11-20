#if WINDOWS
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Tbox.Desktop.Platform;

// Peek's ShellPreviewHandlerControl: a child window inside the preview that the handler in prevhost.exe draws into.
// Avalonia positions the child; WM_SIZE passes the new size on to the handler.
internal sealed class QuickLookHandlerView : IQuickLookHostedPreview
{
    private readonly Host _host;
    private IPreviewHandler? _handler;
    private Stream? _stream;

    private QuickLookHandlerView(IPreviewHandler handler, Stream? stream)
    {
        _handler = handler; _stream = stream;
        _host = new Host(this);
        View = new Panel { Background = Avalonia.Media.Brushes.White, Children = { _host } };
    }

    public Control View { get; }
    public event Action? Failed;

    // Creating the handler starts prevhost.exe; like Peek this runs off the UI thread, in the multithreaded apartment.
    internal static async Task<IQuickLookHostedPreview?> OpenAsync(string path, CancellationToken cancellationToken)
    {
        if (QuickLookPreviewHandlers.Find(Path.GetExtension(path)) is not { } clsid) return null;
        var (handler, stream) = await Task.Run(() => (QuickLookPreviewHandlers.Create(clsid, path, out var opened), opened), cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            Unload(handler, stream);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new QuickLookHandlerView(handler, stream);
    }

    private void Attach(nint window)
    {
        if (_handler is not { } handler) return;
        try
        {
            GetClientRect(window, out var rect);
            if (handler is IPreviewHandlerVisuals visuals)
            {
                // COLORREF is 0x00BBGGRR: white behind, Tbox's text colour #1A1B22 in front.
                visuals.SetBackgroundColor(0x00FFFFFF);
                visuals.SetTextColor(0x00221B1A);
            }
            handler.SetWindow(window, ref rect);
            handler.DoPreview();
        }
        catch (Exception error) when (error is COMException or InvalidCastException or InvalidComObjectException)
        {
            AppLog.Write("QuickLook preview handler: " + error.GetType().Name + " 0x" + error.HResult.ToString("X8"));
            Failed?.Invoke();
        }
    }

    private void Resize(int width, int height)
    {
        if (_handler is not { } handler) return;
        var rect = new QuickLookRect { Right = width, Bottom = height };
        try { handler.SetRect(ref rect); } catch (Exception error) when (error is COMException or InvalidComObjectException) { }
    }

    public void Dispose()
    {
        var handler = _handler; _handler = null;
        if (handler is not null) Unload(handler, _stream);
        _stream = null;
    }

    private static void Unload(IPreviewHandler handler, Stream? stream)
    {
        // Unload and release separately: a crashed prevhost.exe throws from Unload, and the object must still be released.
        try { handler.Unload(); } catch (Exception error) when (error is COMException or InvalidComObjectException) { }
        QuickLookPreviewHandlers.Release(handler, unlock: false);
        stream?.Dispose();
    }

    private sealed class Host(QuickLookHandlerView owner) : NativeControlHost
    {
        private const string ClassName = "TboxQuickLookPreviewHandler";
        private static readonly Dictionary<nint, QuickLookHandlerView> Owners = [];
        private static WndProc? _procedure;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            Register();
            // WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN. Peek makes its container layered; under an Avalonia window that
            // makes DoPreview fail (0x8007057E for RTF, E_FAIL for fonts), so this one is a plain child.
            var window = CreateWindowEx(0, ClassName, null, 0x40000000 | 0x10000000 | 0x02000000, 0, 0, 1, 1, parent.Handle, 0, GetModuleHandle(null), 0);
            if (window == 0) return base.CreateNativeControlCore(parent);
            Owners[window] = owner;
            owner.Attach(window);
            return new PlatformHandle(window, "HWND");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            owner.Dispose();
            Owners.Remove(control.Handle);
            DestroyWindow(control.Handle);
        }

        private static void Register()
        {
            if (_procedure is not null) return;
            _procedure = Procedure;
            var info = new WndClass
            {
                Size = (uint)Marshal.SizeOf<WndClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                Instance = GetModuleHandle(null), Background = GetStockObject(0), ClassName = ClassName
            };
            RegisterClassEx(ref info);
        }

        private static nint Procedure(nint window, uint message, nint wParam, nint lParam)
        {
            if (message == 0x0005 && Owners.TryGetValue(window, out var view)) // WM_SIZE
                view.Resize((int)(lParam & 0xFFFF), (int)((lParam >> 16) & 0xFFFF));
            return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon;
    }
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WndClass info);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string? name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out QuickLookRect rect);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
#endif
