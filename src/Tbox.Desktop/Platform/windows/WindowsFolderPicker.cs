#if WINDOWS
using System.Runtime.InteropServices;

namespace Tbox.Desktop.Platform;

// Tbox owns this dialog and its COM object on the UI STA. The native modal
// message loop keeps processing the application's global-hotkey messages.
internal sealed class WindowsFolderPicker
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint FolderOptions = 0x20 | 0x40 | 0x800 | 0x02000000; // Pick folders, filesystem, existing path, no recent item.
    private const uint FileSystemPath = 0x80058000;
    private static readonly Guid FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid ShellItemId = typeof(IShellItem).GUID;
    private IFileDialog? _active;

    internal string? Show(nint owner, string title)
    {
        var dialogType = Type.GetTypeFromCLSID(FileOpenDialog, throwOnError: true)!;
        var dialog = (IFileDialog)Activator.CreateInstance(dialogType)!;
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FolderOptions);
            dialog.SetTitle(title);
            _active = dialog;
            var result = dialog.Show(owner);
            if (result == Cancelled) return null;
            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out var item);
            return ReadPath(item);
        }
        finally
        {
            _active = null;
            Marshal.ReleaseComObject(dialog);
        }
    }

    internal bool Owns(nint window) => window != 0 && _active is IOleWindow ole &&
        ole.GetWindow(out var handle) == 0 && window == handle;

    internal async Task<FolderNavigationResult> NavigateAsync(nint window, string path, CancellationToken cancellationToken)
    {
        var dialog = _active;
        if (dialog is null || !IsActive(window)) return new(false, "文件夹选择器已关闭或不在前台。");
        try
        {
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, in ShellItemId, out var item));
            try { dialog.SetFolder(item); }
            finally { Marshal.ReleaseComObject(item); }

            var deadline = Environment.TickCount64 + 1500;
            while (ReferenceEquals(_active, dialog) && IsActive(window))
            {
                cancellationToken.ThrowIfCancellationRequested();
                dialog.GetFolder(out var current);
                if (ReadPath(current) is { } actual && string.Equals(Path.TrimEndingDirectorySeparator(actual),
                        Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)) return new(true);
                if (Environment.TickCount64 >= deadline) break;
                await Task.Delay(25, cancellationToken);
            }
        }
        catch (COMException error)
        {
            AppLog.Write($"Tbox folder picker navigation failed: 0x{error.HResult:X8}");
        }
        return new(false, "文件夹选择器未能切换到最近目录。");
    }

    private bool IsActive(nint window) => NativeMethods.GetForegroundWindow() == window && Owns(window);

    private static string? ReadPath(IShellItem item)
    {
        try
        {
            if (item.GetDisplayName(FileSystemPath, out var value) < 0) return null;
            try { return Marshal.PtrToStringUni(value); }
            finally { Marshal.FreeCoTaskMem(value); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid interfaceId, out IShellItem item);

    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow
    {
        [PreserveSig] int GetWindow(out nint window);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, in Guid handler, in Guid interfaceId, out nint result);
        void GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint nameKind, out nint name);
    }

    // Keep declaration order identical to the native IFileDialog vtable.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint owner);
        void SetFileTypes(uint count, nint filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(nint events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out nint name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
    }
}
#endif
