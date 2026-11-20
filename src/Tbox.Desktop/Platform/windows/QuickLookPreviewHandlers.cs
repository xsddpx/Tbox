#if WINDOWS
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace Tbox.Desktop.Platform;

// System preview handlers as PowerToys Peek hosts them (ShellPreviewHandlerPreviewer, ShellPreviewHandlerControl):
// the handler runs in prevhost.exe, draws into a child window of the preview, and is unloaded when the file changes.
internal static class QuickLookPreviewHandlers
{
    private const string HandlerKey = "shellex\\{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private static readonly ConcurrentDictionary<Guid, IClassFactory> Factories = new();
    // Handlers Tbox draws better itself, or that Tbox's own previews already cover (PowerToys installs Monaco, SVG and PDF handlers).
    private static readonly HashSet<Guid> Skipped =
    [
        new("D8034CFA-F34B-41FE-AD45-62FCBB52A6DA"), new("FCDD4EED-41AA-492F-8A84-31A1546226E0"), new("DC6EFB56-9CFA-464D-8880-44885D7DC193")
    ];

    // Peek's GetPreviewHandlerGuid: the extension's own key, then its file class.
    internal static Guid? Find(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        try
        {
            using var extensionKey = Registry.ClassesRoot.OpenSubKey(extension);
            using var handler = extensionKey?.OpenSubKey(HandlerKey);
            var value = handler?.GetValue(null) as string;
            if (value is null && extensionKey?.GetValue(null) is string { Length: > 0 } className)
            {
                using var classHandler = Registry.ClassesRoot.OpenSubKey(className + "\\" + HandlerKey);
                value = classHandler?.GetValue(null) as string;
            }
            // Windows registers its own handlers (RTF, for one) here, which Peek does not read; a file class taken
            // over by another program, such as LibreOffice for .rtf, would otherwise hide them.
            if (value is null)
            {
                using var system = Registry.ClassesRoot.OpenSubKey("SystemFileAssociations\\" + extension + "\\" + HandlerKey);
                value = system?.GetValue(null) as string;
            }
            return Guid.TryParse(value, out var id) && !Skipped.Contains(id) ? id : null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    // Creates and initializes the handler for path. Runs on an STA thread; the handler object lives in prevhost.exe.
    internal static IPreviewHandler Create(Guid clsid, string path, out Stream? stream)
    {
        stream = null;
        // The shell parses only backslash paths.
        path = Path.GetFullPath(path);
        for (var attempt = 0; ; attempt++)
        {
            var factory = Factories.GetOrAdd(clsid, id =>
            {
                Marshal.ThrowExceptionForHR(CoGetClassObject(id, ClsctxLocalServer, 0, typeof(IClassFactory).GUID, out var created));
                var value = (IClassFactory)created;
                // Keeping the server locked makes the next file of the same type open faster, as in Peek.
                value.LockServer(true);
                return value;
            });
            object instance;
            try
            {
                var iid = typeof(IPreviewHandler).GUID;
                factory.CreateInstance(null, ref iid, out instance);
            }
            catch (COMException) when (attempt == 0)
            {
                // prevhost.exe probably exited; get a new factory once.
                if (Factories.TryRemove(new KeyValuePair<Guid, IClassFactory>(clsid, factory))) Release(factory, unlock: true);
                continue;
            }
            var handler = (IPreviewHandler)instance;
            try
            {
                if (handler is IInitializeWithStream withStream)
                {
                    stream = File.OpenRead(path);
                    withStream.Initialize(new ManagedIStream(stream), 0);
                }
                else if (handler is IInitializeWithItem withItem)
                {
                    Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, typeof(IShellItemHandle).GUID, out var item));
                    try { withItem.Initialize(item, 0); } finally { Marshal.ReleaseComObject(item); }
                }
                else if (handler is IInitializeWithFile withFile) withFile.Initialize(path, 0);
                else throw new NotSupportedException("预览处理程序不支持此文件。");
                return handler;
            }
            catch
            {
                stream?.Dispose(); stream = null;
                Release(handler, unlock: false);
                throw;
            }
        }
    }

    internal static void Release(object? value, bool unlock)
    {
        if (value is null) return;
        if (unlock && value is IClassFactory factory) { try { factory.LockServer(false); } catch (COMException) { } catch (InvalidComObjectException) { } }
        try { Marshal.FinalReleaseComObject(value); } catch (ArgumentException) { } catch (InvalidComObjectException) { }
    }

    internal static void ReleaseFactories()
    {
        foreach (var key in Factories.Keys.ToArray())
            if (Factories.TryRemove(key, out var factory)) Release(factory, unlock: true);
    }

    private const uint ClsctxLocalServer = 0x4;
    [DllImport("ole32.dll")] private static extern int CoGetClassObject([In] in Guid clsid, uint context, nint serverInfo, [In] in Guid iid, [MarshalAs(UnmanagedType.Interface)] out object factory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, nint context, [In] in Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IClassFactory
    {
        void CreateInstance([MarshalAs(UnmanagedType.IUnknown)] object? outer, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object instance);
        void LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemHandle { }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream { void Initialize(IStream stream, uint mode); }

    [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithItem { void Initialize([MarshalAs(UnmanagedType.Interface)] object item, uint mode); }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile { void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode); }

    // A read-only IStream over a .NET stream for IInitializeWithStream.
    private sealed class ManagedIStream(Stream stream) : IStream
    {
        public void Read(byte[] buffer, int count, nint read)
        {
            var total = stream.Read(buffer, 0, count);
            if (read != 0) Marshal.WriteInt32(read, total);
        }
        public void Seek(long offset, int origin, nint position)
        {
            var result = stream.Seek(offset, (SeekOrigin)origin);
            if (position != 0) Marshal.WriteInt64(position, result);
        }
        public void Stat(out STATSTG stat, int flag) => stat = new STATSTG { type = 2, cbSize = stream.Length, grfMode = 0 };
        public void Clone(out IStream clone) => throw new COMException(null, unchecked((int)0x80004001));
        public void Commit(int flags) { }
        public void CopyTo(IStream target, long count, nint read, nint written) => throw new COMException(null, unchecked((int)0x80004001));
        public void LockRegion(long offset, long count, int type) => throw new COMException(null, unchecked((int)0x80030001));
        public void Revert() { }
        public void SetSize(long size) => throw new COMException(null, unchecked((int)0x80030005));
        public void UnlockRegion(long offset, long count, int type) => throw new COMException(null, unchecked((int)0x80030001));
        public void Write(byte[] buffer, int count, nint written) => throw new COMException(null, unchecked((int)0x80030005));
    }
}

[ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandler
{
    void SetWindow(nint parent, ref QuickLookRect rect);
    void SetRect(ref QuickLookRect rect);
    void DoPreview();
    void Unload();
    void SetFocus();
    void QueryFocus(out nint window);
    [PreserveSig] int TranslateAccelerator(nint message);
}

[ComImport, Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandlerVisuals
{
    void SetBackgroundColor(uint color);
    void SetFont(nint font);
    void SetTextColor(uint color);
}

[StructLayout(LayoutKind.Sequential)]
internal struct QuickLookRect { public int Left, Top, Right, Bottom; }
#endif
