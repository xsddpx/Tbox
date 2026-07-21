using System.Runtime.InteropServices;

namespace Tbox.App;

internal static class ExplorerShellViewResolver
{
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserInterface = new("000214E2-0000-0000-C000-000000000046");

    internal static IntPtr FindUnambiguousTabWindow(IntPtr explorerRoot)
    {
        var firstTab = NativeMethods.FindWindowEx(
            explorerRoot,
            IntPtr.Zero,
            "ShellTabWindowClass",
            null);
        if (firstTab == IntPtr.Zero) return IntPtr.Zero;

        var secondTab = NativeMethods.FindWindowEx(
            explorerRoot,
            firstTab,
            "ShellTabWindowClass",
            null);
        return secondTab == IntPtr.Zero ? firstTab : IntPtr.Zero;
    }

    internal static IntPtr TryGetTabWindow(object shellWindow, IntPtr explorerRoot)
    {
        IntPtr browserPointer = IntPtr.Zero;
        try
        {
            var provider = (IServiceProviderNative)shellWindow;
            var service = TopLevelBrowserService;
            var browserInterface = ShellBrowserInterface;
            var result = provider.QueryService(
                ref service,
                ref browserInterface,
                out browserPointer);
            if (result < 0 || browserPointer == IntPtr.Zero) return IntPtr.Zero;

            var browserObject = Marshal.GetObjectForIUnknown(browserPointer);
            var browser = (IShellBrowserNative)browserObject;
            result = browser.QueryActiveShellView(out var shellView);
            if (result < 0 || shellView is null) return IntPtr.Zero;
            result = shellView.GetWindow(out var viewWindow);
            if (result < 0 || viewWindow == IntPtr.Zero) return IntPtr.Zero;

            var current = viewWindow;
            while (current != IntPtr.Zero && current != explorerRoot)
            {
                var parent = NativeMethods.GetParent(current);
                if (parent == explorerRoot) return current;
                current = parent;
            }
        }
        catch (Exception exception) when (
            exception is COMException or InvalidCastException or InvalidOperationException)
        {
            // Older Explorer implementations and transient tab teardown may
            // not expose this service. The selection policy handles fallback.
        }
        finally
        {
            if (browserPointer != IntPtr.Zero)
            {
                Marshal.Release(browserPointer);
            }
        }

        return IntPtr.Zero;
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderNative
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid interfaceId, out IntPtr result);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowserNative
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
        [PreserveSig] int InsertMenusSB(IntPtr sharedMenu, IntPtr menuWidths);
        [PreserveSig] int SetMenuSB(IntPtr sharedMenu, IntPtr holeMenu, IntPtr activeObject);
        [PreserveSig] int RemoveMenusSB(IntPtr sharedMenu);
        [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string statusText);
        [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr message, ushort commandId);
        [PreserveSig] int BrowseObject(IntPtr itemIdList, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
        [PreserveSig] int GetControlWindow(uint controlId, out IntPtr window);
        [PreserveSig] int SendControlMsg(
            uint controlId,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            out IntPtr result);
        [PreserveSig] int QueryActiveShellView(
            [MarshalAs(UnmanagedType.Interface)] out IShellViewNative? shellView);
    }

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellViewNative
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
    }
}
