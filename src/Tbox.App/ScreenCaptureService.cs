using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Tbox.App;

internal static partial class ScreenCaptureService
{
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    internal static IReadOnlyList<CapturedScreen> CaptureAllMonitors()
    {
        var monitorHandles = new List<IntPtr>();
        MonitorEnumProc callback = (IntPtr monitor, IntPtr _, ref Rect _, IntPtr _) =>
        {
            monitorHandles.Add(monitor);
            return true;
        };

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
        {
            throw new InvalidOperationException($"无法枚举显示器，Win32={Marshal.GetLastWin32Error()}。");
        }
        if (monitorHandles.Count == 0)
        {
            throw new InvalidOperationException("系统没有返回可截图的显示器。");
        }

        return monitorHandles
            .Select(CaptureMonitor)
            .OrderBy(screen => screen.Left)
            .ThenBy(screen => screen.Top)
            .ToArray();
    }

    private static CapturedScreen CaptureMonitor(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            throw new InvalidOperationException($"无法读取显示器信息，Win32={Marshal.GetLastWin32Error()}。");
        }

        var width = info.Monitor.Right - info.Monitor.Left;
        var height = info.Monitor.Bottom - info.Monitor.Top;
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException($"无法访问屏幕，Win32={Marshal.GetLastWin32Error()}。");
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                throw new InvalidOperationException($"无法创建截图缓冲区，Win32={Marshal.GetLastWin32Error()}。");
            }

            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                    SizeImage = (uint)checked(width * height * 4)
                }
            };

            bitmap = CreateDIBSection(screenDc, ref bitmapInfo, DibRgbColors, out var pixelsPointer, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || pixelsPointer == IntPtr.Zero)
            {
                throw new InvalidOperationException($"无法创建截图位图，Win32={Marshal.GetLastWin32Error()}。");
            }

            previous = SelectObject(memoryDc, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
            {
                throw new InvalidOperationException($"无法选择截图位图，Win32={Marshal.GetLastWin32Error()}。");
            }

            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, info.Monitor.Left, info.Monitor.Top, SrcCopy | CaptureBlt))
            {
                throw new InvalidOperationException($"屏幕截图失败，Win32={Marshal.GetLastWin32Error()}。");
            }

            var pixels = new byte[checked(width * height * 4)];
            Marshal.Copy(pixelsPointer, pixels, 0, pixels.Length);
            for (var index = 3; index < pixels.Length; index += 4)
            {
                pixels[index] = 255;
            }

            return new CapturedScreen(info.Monitor.Left, info.Monitor.Top, width, height, pixels);
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1) && memoryDc != IntPtr.Zero)
            {
                SelectObject(memoryDc, previous);
            }
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    internal static PointInt32 GetCursorPosition()
    {
        return GetCursorPos(out var cursor)
            ? new PointInt32(cursor.X, cursor.Y)
            : new PointInt32(0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Colors;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorEnumProc(
        IntPtr monitor,
        IntPtr monitorDc,
        ref Rect monitorRect,
        IntPtr data);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr dc,
        IntPtr clip,
        MonitorEnumProc callback,
        IntPtr data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr GetDC(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr dc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr dc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial IntPtr SelectObject(IntPtr dc, IntPtr value);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr value);
}
