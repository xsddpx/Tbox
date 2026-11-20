#if WINDOWS
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Tbox.Desktop.Platform;

// Win32 and shell calls behind 速览, following PowerToys Peek (FileExplorerHelper, HWNDExtensions, DefaultAppHelper).
internal static class QuickLookWindows
{
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserId = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid ShellItemArrayId = new("B63EA76D-1F85-456F-A19C-48159EFA858B");
    private static readonly Guid ImageFactoryId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    private const uint SelectionItems = 0x1, AllItems = 0x2, ViewOrder = 0x80000000, FileSystemPath = 0x80058000;

    // Peek activates over Explorer, the desktop and its own window.
    internal static bool IsActivatable(nint foreground, nint preview)
    {
        if (foreground == 0) return false;
        if (foreground == preview) return true;
        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GaRoot);
        if (root == 0) root = foreground;
        var name = ClassName(root);
        if (name is "CabinetWClass" or "ExploreWClass") return true;
        return name is "Progman" or "WorkerW" && NativeMethods.FindWindowEx(root, 0, "SHELLDLL_DefView", null) != 0;
    }

    // Peek's CaretVisible: a focused Edit or Input control, or a blinking caret, means the user is typing
    // (renaming, the address bar, the search box).
    internal static bool IsEditing(nint foreground)
    {
        var info = new NativeMethods.GuiThreadInfo { size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        if (!NativeMethods.GetGUIThreadInfo(0, ref info)) return false;
        if (info.activeWindow != foreground || info.focusWindow == 0) return false;
        var name = ClassName(info.focusWindow);
        if (name.Contains("Edit", StringComparison.OrdinalIgnoreCase) || name.Contains("Input", StringComparison.OrdinalIgnoreCase)) return true;
        return (info.flags & 0x1) != 0; // GUI_CARETBLINKING
    }

    internal static QuickLookSelection? ReadSelection(nint window)
    {
        var items = ReadItems(window, SelectionItems | ViewOrder);
        return items is null || items.Count == 0 ? null : new QuickLookSelection(items, items.Count > 1);
    }

    internal static IReadOnlyList<string> ReadFolder(nint window) => ReadItems(window, AllItems | ViewOrder) ?? [];

    private static List<string>? ReadItems(nint window, uint which)
    {
        var root = NativeMethods.GetAncestor(window, NativeMethods.GaRoot);
        if (root == 0) root = window;
        var desktop = ClassName(root) is "Progman" or "WorkerW";
        var shellType = Type.GetTypeFromCLSID(ShellWindowsClass, throwOnError: true)!;
        var windows = (IShellWindows)Activator.CreateInstance(shellType)!;
        try
        {
            if (desktop)
            {
                object location = 0x0000, rootLocation = System.Reflection.Missing.Value; // CSIDL_DESKTOP
                if (windows.FindWindowSW(ref location, ref rootLocation, 8, out _, 1, out var dispatch) != 0 || dispatch is null) return null;
                try { return ItemsOf(dispatch, which); }
                finally { Release(dispatch); }
            }
            // Win11 tabs: the active tab is the first ShellTabWindowClass child (Peek's HWNDExtensions).
            var activeTab = NativeMethods.FindWindowEx(root, 0, "ShellTabWindowClass", null);
            if (activeTab == 0) activeTab = NativeMethods.FindWindowEx(root, 0, "TabWindowClass", null);
            if (windows.get_Count(out var count) != 0) return null;
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    if (windows.Item(index, out item) != 0 || item is null) continue;
                    if (new nint(Convert.ToInt64(((dynamic)item).HWND)) != root) continue;
                    if (activeTab != 0 && ExplorerShellViewResolver.TryGetTabWindow(item, root) is var tab && tab != 0 && tab != activeTab) continue;
                    if (ItemsOf(item, which) is { } found) return found;
                }
                catch (Exception error) when (error is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                finally { Release(item); }
            }
            return null;
        }
        finally { Release(windows); }
    }

    private static IShellBrowser? Browser(object shellWindow)
    {
        if (shellWindow is not IServiceProvider provider) return null;
        var service = TopLevelBrowser; var id = ShellBrowserId;
        if (provider.QueryService(ref service, ref id, out var pointer) != 0 || pointer == 0) return null;
        try { return (IShellBrowser)Marshal.GetObjectForIUnknown(pointer); }
        finally { Marshal.Release(pointer); }
    }

    private static List<string>? ItemsOf(object shellWindow, uint which)
    {
        var browser = Browser(shellWindow);
        if (browser is null) return null;
        object? view = null, array = null;
        try
        {
            if (browser.QueryActiveShellView(out view) != 0 || view is not IFolderView folderView) return null;
            var id = ShellItemArrayId;
            if (folderView.Items(which, ref id, out array) != 0 || array is not IShellItemArray items) return [];
            if (items.GetCount(out var count) != 0) return [];
            var paths = new List<string>((int)count);
            for (uint index = 0; index < count; index++)
            {
                if (items.GetItemAt(index, out var item) != 0 || item is null) continue;
                try
                {
                    if (item.GetDisplayName(FileSystemPath, out var name) != 0 || name == 0) continue;
                    try { if (Marshal.PtrToStringUni(name) is { Length: > 0 } path) paths.Add(path); }
                    finally { Marshal.FreeCoTaskMem(name); }
                }
                finally { Release(item); }
            }
            return paths;
        }
        finally { Release(array); Release(view); Release(browser); }
    }

    internal static QuickLookMonitor MonitorOf(nint window)
    {
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        var dpi = GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? x : Math.Max(96u, NativeMethods.GetDpiForWindow(window));
        var r = info.Monitor;
        return new QuickLookMonitor(new DesktopPlacement(r.left, r.top, r.right - r.left, r.bottom - r.top), Math.Max(1, dpi / 96d));
    }

    // Peek's BringToForeground: borrow the foreground thread's input state for the call.
    internal static void BringToForeground(nint window)
    {
        if (NativeMethods.IsIconic(window)) ShowWindow(window, 9); // SW_RESTORE
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == window) return;
        var current = NativeMethods.GetCurrentThreadId();
        var other = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var attached = other != 0 && other != current && NativeMethods.AttachThreadInput(other, current, true);
        try { NativeMethods.SetForegroundWindow(window); BringWindowToTop(window); }
        finally { if (attached) NativeMethods.AttachThreadInput(other, current, false); }
    }

    internal static Bitmap? Thumbnail(string path, int size, bool thumbnailOnly)
    {
        var id = ImageFactoryId;
        if (SHCreateItemFromParsingName(path, 0, ref id, out var factory) != 0 || factory is null) return null;
        try
        {
            // SIIGBF_THUMBNAILONLY, otherwise SIIGBF_BIGGERSIZEOK so icons fall back as well.
            var flags = thumbnailOnly ? 0x8 : 0x1;
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, flags, out var bitmap) != 0 || bitmap == 0) return null;
            try { return FromHBitmap(bitmap); }
            finally { DeleteObject(bitmap); }
        }
        catch (COMException) { return null; }
        finally { Release(factory); }
    }

    internal static Bitmap? SmallIcon(string name, bool directory)
    {
        var info = new NativeMethods.ShFileInfo();
        // SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES: the name need not exist.
        if (NativeMethods.SHGetFileInfo(name, directory ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf(info), 0x100 | 0x1 | 0x10) == 0 || info.icon == 0) return null;
        try
        {
            if (!GetIconInfo(info.icon, out var icon)) return null;
            try { return icon.Color == 0 ? null : FromHBitmap(icon.Color); }
            finally { if (icon.Color != 0) DeleteObject(icon.Color); if (icon.Mask != 0) DeleteObject(icon.Mask); }
        }
        finally { DestroyIcon(info.icon); }
    }

    private static Bitmap? FromHBitmap(nint bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var header) == 0 || header.Width <= 0 || header.Height == 0) return null;
        var width = header.Width; var height = Math.Abs(header.Height);
        var info = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var pixels = new byte[checked(width * height * 4)];
        var dc = GetDC(0);
        try { if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) == 0) return null; }
        finally { ReleaseDC(0, dc); }
        var opaque = true;
        for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { opaque = false; break; }
        // Thumbnails without an alpha channel arrive with every alpha byte at zero.
        if (opaque) for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return ToBitmap(pixels, width, height, AlphaFormat.Premul);
    }

    internal static Bitmap ToBitmap(byte[] pixels, int width, int height, AlphaFormat alpha)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, alpha);
        using var target = bitmap.Lock();
        for (var row = 0; row < height; row++)
            Marshal.Copy(pixels, row * width * 4, target.Address + row * target.RowBytes, width * 4);
        return bitmap;
    }

    internal static string TypeName(string path, bool directory)
    {
        var info = new NativeMethods.ShFileInfo();
        NativeMethods.SHGetFileInfo(path, directory ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf(info), 0x400 | 0x10);
        return string.IsNullOrWhiteSpace(info.typeName) ? (directory ? "文件夹" : Path.GetExtension(path).TrimStart('.').ToUpperInvariant() + " 文件") : info.typeName;
    }

    // DefaultAppHelper: AssocQueryString(ASSOCF_VERIFY, ASSOCSTR_FRIENDLYAPPNAME, extension).
    internal static string? DefaultAppName(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return null;
        uint length = 0;
        if (AssocQueryString(0x40, 4, extension, null, null, ref length) != 1 || length == 0) return null;
        var buffer = new char[length];
        if (AssocQueryString(0x40, 4, extension, null, buffer, ref length) != 0) return null;
        var name = new string(buffer, 0, (int)Math.Max(0, length - 1)).Trim();
        return name.Length == 0 ? null : name;
    }

    // Peek launches the file and shows the "Open with" picker when no application is registered.
    internal static void Open(string path, nint owner)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception)
        {
            var info = new OpenAsInfo { File = path, Flags = 0x4 | 0x1 | 0x2 }; // OAIF_EXEC | OAIF_ALLOW_REGISTRATION | OAIF_REGISTER_EXT
            Marshal.ThrowExceptionForHR(SHOpenWithDialog(owner, ref info));
        }
    }

    // SHFileOperation(FO_DELETE, FOF_ALLOWUNDO | FOF_WANTNUKEWARNING) after Peek's 200 ms pause.
    internal static (bool Deleted, string? Error) Delete(string path, nint owner)
    {
        Thread.Sleep(200);
        var operation = new FileOperation { Window = owner, Function = 3, From = path + "\0\0", Flags = 0x40 | 0x4000 };
        var result = SHFileOperation(ref operation);
        if (operation.AnyOperationsAborted) return (false, null);
        if (result != 0) return (false, new Win32Exception(result).Message);
        return File.Exists(path) || Directory.Exists(path) ? (false, null) : (true, null);
    }

    private static string ClassName(nint window)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        [PreserveSig] int get_Count(out int count);
        [PreserveSig] int Item([MarshalAs(UnmanagedType.Struct)] object index, [MarshalAs(UnmanagedType.IDispatch)] out object? folder);
        [PreserveSig] int _NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object enumerator);
        [PreserveSig] int Register([MarshalAs(UnmanagedType.IDispatch)] object window, int hwnd, int windowClass, out int cookie);
        [PreserveSig] int RegisterPending(int thread, [MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int windowClass, out int cookie);
        [PreserveSig] int Revoke(int cookie);
        [PreserveSig] int OnNavigate(int cookie, [MarshalAs(UnmanagedType.Struct)] ref object location);
        [PreserveSig] int OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
        [PreserveSig] int FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root,
            int windowClass, out int hwnd, int options, [MarshalAs(UnmanagedType.IDispatch)] out object? dispatch);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid interfaceId, out nint result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out nint window);
        [PreserveSig] int ContextSensitiveHelp(int enterMode);
        [PreserveSig] int InsertMenusSB(nint sharedMenu, nint menuWidths);
        [PreserveSig] int SetMenuSB(nint sharedMenu, nint holeMenu, nint activeObject);
        [PreserveSig] int RemoveMenusSB(nint sharedMenu);
        [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int EnableModelessSB(int enable);
        [PreserveSig] int TranslateAcceleratorSB(nint message, ushort id);
        [PreserveSig] int BrowseObject(nint itemIdList, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out nint stream);
        [PreserveSig] int GetControlWindow(uint id, out nint window);
        [PreserveSig] int SendControlMsg(uint id, uint message, nint wParam, nint lParam, out nint result);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object? view);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int GetFolder(ref Guid id, out nint folder);
        [PreserveSig] int Item(int index, out nint itemIdList);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object? items);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(nint context, ref Guid handler, ref Guid id, out nint result);
        [PreserveSig] int GetPropertyStore(int flags, ref Guid id, out nint store);
        [PreserveSig] int GetPropertyDescriptionList(nint key, ref Guid id, out nint list);
        [PreserveSig] int GetAttributes(int flags, uint mask, out uint attributes);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemAt(uint index, out IShellItem? item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint context, ref Guid handler, ref Guid id, out nint result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint form, out nint name);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out nint bitmap);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeMethods.Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public bool IsIcon; public int X, Y; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public nint Bits; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OpenAsInfo { public string File; public string? Class; public int Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FileOperation
    {
        public nint Window; public uint Function; public string From; public string? To; public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted; public nint NameMappings; public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid id, out IShellItemImageFactory? item);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref FileOperation operation);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHOpenWithDialog(nint owner, ref OpenAsInfo info);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int AssocQueryString(int flags, int kind, string association, string? extra, [Out] char[]? output, ref uint length);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int kind, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(nint value, int size, out NativeBitmap bitmap);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
#endif
