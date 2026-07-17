using System.Runtime.InteropServices;
using Tbox.Core;
using Windows.Graphics;

namespace Tbox.App;

internal sealed class CaptureTargetDetector
{
    private const int ObjectIdClient = -4;
    private const int ChildIdSelf = 0;
    private static readonly Guid AccessibleInterfaceId = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private readonly IReadOnlyList<WindowTarget> _windows;

    private CaptureTargetDetector(IReadOnlyList<WindowTarget> windows)
    {
        _windows = windows;
    }

    internal static CaptureTargetDetector CreateSnapshot()
    {
        var windows = new List<WindowTarget>();
        var currentProcessId = (uint)Environment.ProcessId;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window)) return true;
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId == currentProcessId) return true;
            if (NativeMethods.IsWindowCloaked(window)) return true;
            if (!NativeMethods.TryGetExtendedFrameBounds(window, out var bounds) &&
                !NativeMethods.GetWindowRect(window, out bounds))
            {
                return true;
            }
            if (bounds.right - bounds.left < 2 || bounds.bottom - bounds.top < 2) return true;

            windows.Add(new WindowTarget(
                window,
                new CaptureTargetBounds(
                    bounds.left,
                    bounds.top,
                    bounds.right - bounds.left,
                    bounds.bottom - bounds.top)));
            return true;
        }, IntPtr.Zero);
        return new CaptureTargetDetector(windows);
    }

    internal CaptureTargetBounds? FindTarget(CaptureDetectionMode mode, PointInt32 desktopPoint)
    {
        var window = _windows.FirstOrDefault(candidate => candidate.Bounds.Contains(desktopPoint.X, desktopPoint.Y));
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
        PointInt32 desktopPoint)
    {
        return TryFindAccessibleElement(window, desktopPoint, out var bounds) ? bounds : null;
    }

    private static bool TryFindAccessibleElement(
        IntPtr window,
        PointInt32 desktopPoint,
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
                    hit = current.AccHitTest(desktopPoint.X, desktopPoint.Y);
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
        PointInt32 desktopPoint)
    {
        try
        {
            accessible.AccLocation(out var left, out var top, out var width, out var height, childId);
            var result = new CaptureTargetBounds(left, top, width, height);
            return result.Contains(desktopPoint.X, desktopPoint.Y) ? result : null;
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
}
