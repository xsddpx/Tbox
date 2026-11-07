#if WINDOWS
// MSAA target lookup adapted from the existing Windows implementation.
using System.Runtime.InteropServices;
using Tbox.Core;
using System.ComponentModel;

namespace Tbox.Desktop.Platform;

internal sealed class WindowsCaptureTargets : ICaptureTargetSource
{
    private const int ObjectIdClient = -4;
    private const int ChildIdSelf = 0;
    private static readonly Guid AccessibleInterfaceId = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private readonly IReadOnlyList<WindowTarget> _windows;

    private WindowsCaptureTargets(IReadOnlyList<WindowTarget> windows)
    {
        _windows = windows;
    }

    internal static WindowsCaptureTargets CreateSnapshot()
    {
        var windows = new List<WindowTarget>();
        var currentProcessId = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId == currentProcessId) return true;
            if (IsWindowCloaked(window)) return true;
            if (!TryGetExtendedFrameBounds(window, out var bounds) &&
                !GetWindowRect(window, out bounds))
            {
                return true;
            }
            if (bounds.Right - bounds.Left < 2 || bounds.Bottom - bounds.Top < 2) return true;

            windows.Add(new WindowTarget(
                window,
                new CaptureTargetBounds(
                    bounds.Left,
                    bounds.Top,
                    bounds.Right - bounds.Left,
                    bounds.Bottom - bounds.Top)));
            return true;
        }, IntPtr.Zero);
        return new WindowsCaptureTargets(windows);
    }

    public bool EnableElementDetection() => true;

    public CaptureTargetBounds? FindTarget(CaptureDetectionMode mode, DesktopPoint desktopPoint)
    {
        var window = _windows.FirstOrDefault(candidate => candidate.Bounds.Contains((int)desktopPoint.X, (int)desktopPoint.Y));
        if (window is null) return null;
        if (mode == CaptureDetectionMode.InterfaceElement &&
            TryFindAccessibleElement(window.Handle, desktopPoint, out var elementBounds))
        {
            return elementBounds;
        }

        return window.Bounds;
    }

    internal static CaptureTargetBounds? FindAccessibleElementForTesting(
        IntPtr window,
        DesktopPoint desktopPoint)
    {
        return TryFindAccessibleElement(window, desktopPoint, out var bounds) ? bounds : null;
    }

    private static bool TryFindAccessibleElement(
        IntPtr window,
        DesktopPoint desktopPoint,
        out CaptureTargetBounds bounds)
    {
        bounds = default;
        object? accessibleObject = null;
        try
        {
            var interfaceId = AccessibleInterfaceId;
            var result = AccessibleObjectFromWindow(window, ObjectIdClient, ref interfaceId, out accessibleObject);
            if (result < 0 || accessibleObject is not IAccessibleNative accessible) return false;

            CaptureTargetBounds? best = TryGetBounds(accessible, ChildIdSelf, desktopPoint);
            var current = accessible;
            for (var depth = 0; depth < 24; depth++)
            {
                object? hit;
                try
                {
                    hit = current.AccHitTest((int)desktopPoint.X, (int)desktopPoint.Y);
                }
                catch (COMException)
                {
                    break;
                }

                if (hit is int childId)
                {
                    if (TryGetBounds(current, childId, desktopPoint) is { } childBounds)
                    {
                        best = childBounds;
                    }
                    if (childId == ChildIdSelf) break;

                    IAccessibleNative? child = null;
                    try
                    {
                        child = current.GetChild(childId) as IAccessibleNative;
                    }
                    catch (COMException)
                    {
                        // Simple MSAA children expose a child id but not a separate COM object.
                    }
                    if (child is null) break;
                    current = child;
                    continue;
                }

                if (hit is not IAccessibleNative nested) break;
                var nestedBounds = TryGetBounds(nested, ChildIdSelf, desktopPoint);
                if (nestedBounds is null || nestedBounds == best) break;
                best = nestedBounds;
                current = nested;
            }

            if (best is not { Width: > 1, Height: > 1 } detected) return false;
            bounds = detected;
            return true;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            return false;
        }
    }

    private static CaptureTargetBounds? TryGetBounds(
        IAccessibleNative accessible,
        int childId,
        DesktopPoint desktopPoint)
    {
        try
        {
            accessible.AccLocation(out var left, out var top, out var width, out var height, childId);
            var result = new CaptureTargetBounds(left, top, width, height);
            return result.Contains((int)desktopPoint.X, (int)desktopPoint.Y) ? result : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private sealed record WindowTarget(IntPtr Handle, CaptureTargetBounds Bounds);

    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IAccessibleNative
    {
        [DispId(-5002)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object? GetChild([MarshalAs(UnmanagedType.Struct)] object childId);

        [DispId(-5015)]
        void AccLocation(
            out int left,
            out int top,
            out int width,
            out int height,
            [MarshalAs(UnmanagedType.Struct)] object childId);

        [DispId(-5017)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object? AccHitTest(int left, int top);
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr window,
        int objectId,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object? accessible);

    private static bool IsWindowCloaked(nint window) => DwmGetWindowAttribute(window, 14, out uint value, sizeof(uint)) == 0 && value != 0;
    private static bool TryGetExtendedFrameBounds(nint window, out NativeRect bounds) => DwmGetWindowAttribute(window, 9, out bounds, Marshal.SizeOf<NativeRect>()) == 0;
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool EnumWindow(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out NativeRect value, int size);
}
#endif
