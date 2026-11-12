#if WINDOWS
using System.Runtime.InteropServices;
using Tbox.Core;

namespace Tbox.Desktop.Platform;

/// <summary>
/// Publishes the clipboard image formats that Windows applications read.
/// The shared Avalonia clipboard path also publishes a CF_BITMAP, which this stack builds from a bitmap
/// compatible with a memory DC: that bitmap is 1 bpp, so the system clipboard history and the emoji panel
/// prefer the black version over the DIB and PNG data. Copies are therefore published here, without CF_BITMAP.
/// </summary>
internal static class WindowsClipboardImage
{
    private const uint CfDib = 8;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;
    private const int MaxAttempts = 5;

    public static void Copy(byte[] png, byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(png);
        // Build before opening the clipboard: it can fail, and a failed copy must not empty the clipboard.
        var dib = ClipboardImageDib.Build(bgra, width, height);
        var pngFormat = RegisterClipboardFormat("PNG");
        var pngMimeFormat = RegisterClipboardFormat("image/png");
        for (var attempt = 1; ; attempt++)
        {
            if (OpenClipboard(0))
            {
                try
                {
                    if (!EmptyClipboard()) throw Error("无法清空剪贴板");
                    SetData(CfDib, dib);
                    if (pngFormat != 0) SetData(pngFormat, png);
                    if (pngMimeFormat != 0) SetData(pngMimeFormat, png);
                    return;
                }
                finally { CloseClipboard(); }
            }
            if (attempt >= MaxAttempts) throw Error("无法打开剪贴板，可能被其他程序占用或桌面已锁定");
            Thread.Sleep(20);
        }
    }

    private static void SetData(uint format, byte[] data)
    {
        var handle = GlobalAlloc(GmemMoveable | GmemZeroInit, (nuint)data.Length);
        if (handle == 0) throw Error("无法为剪贴板分配内存");
        var target = GlobalLock(handle);
        if (target == 0) { GlobalFree(handle); throw Error("无法锁定剪贴板内存"); }
        try { Marshal.Copy(data, 0, target, data.Length); }
        finally { GlobalUnlock(handle); }
        // The system owns the memory after a successful SetClipboardData; only free it when ownership did not move.
        if (SetClipboardData(format, handle) == 0) { GlobalFree(handle); throw Error("无法写入剪贴板"); }
    }

    private static InvalidOperationException Error(string message) =>
        new($"{message}（错误码 {Marshal.GetLastWin32Error()}）。");

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalFree(nint memory);
}
#endif
