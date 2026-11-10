#if WINDOWS
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Tbox.Desktop.Platform;

// Supplies the monitor lookup used by ScreenRegionRecorder.
internal static class ScreenCaptureService
{
    internal readonly record struct DisplayInfo(string DeviceName, RectInt32 Bounds);
    internal static DisplayInfo GetDisplayInfo(RectInt32 region)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = region.X + region.Width / 2, Y = region.Y + region.Height / 2 }, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), DeviceName = "" };
        if (!GetMonitorInfo(monitor, ref info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new DisplayInfo(info.DeviceName, new RectInt32(info.Monitor.Left, info.Monitor.Top,
            info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top));
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public uint Size; public NativeRect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
#endif
