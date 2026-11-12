#if WINDOWS
using System.Runtime.InteropServices;
using Avalonia;
using Windows.Graphics;

namespace Tbox.Desktop.Platform;

internal static partial class NativeMethods
{
    internal const int WmHotkey = 0x0312;
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;
    internal const uint ModNoRepeat = 0x4000;
    internal const uint EventSystemForeground = 0x0003;
    internal const uint WineventOutofcontext = 0;
    internal const uint KeyeventfKeyup = 0x0002;
    internal const uint InputMouse = 0;
    internal const ushort VkControl = 0x11;
    internal const ushort VkShift = 0x10;
    internal const ushort VkG = 0x47;
    internal const ushort VkL = 0x4C;
    internal const ushort VkReturn = 0x0D;
    internal const ushort VkLButton = 0x01;
    internal const ushort VkRButton = 0x02;
    internal const uint InputKeyboard = 1;
    private const uint MouseeventfWheel = 0x0800;
    internal const uint WmSetText = 0x000C;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const uint WmChar = 0x0102;
    internal const uint SmtoAbortIfHung = 0x0002;
    internal const uint GaRoot = 2;
    internal const uint ShgfiDisplayName = 0x000000200;
    private const int DwmwaTransitionsForcedisabled = 3;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFramechanged = 0x0020;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int GwlpHwndParent = -8;
    private const int GwlWndproc = -4;
    private const int GwlStyle = -16;
    private const int GwlExstyle = -20;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickframe = 0x00040000L;
    private const long WsSysmenu = 0x00080000L;
    private const long WsMinimizebox = 0x00020000L;
    private const long WsMaximizebox = 0x00010000L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolwindow = 0x00000080L;
    private const long WsExNoactivate = 0x08000000L;
    private const uint WdaExcludefromcapture = 0x00000011;

    internal delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    internal delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);
    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hwnd, int id);

    internal static void DisableWindowTransitions(IntPtr hwnd)
    {
        var disabled = 1;
        _ = DwmSetWindowAttribute(
            hwnd,
            DwmwaTransitionsForcedisabled,
            ref disabled,
            Marshal.SizeOf<int>());
    }

    internal static void RemoveWindowBorderAndRounding(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        var borderlessStyle = style & ~(
            WsCaption |
            WsThickframe |
            WsSysmenu |
            WsMinimizebox |
            WsMaximizebox);
        _ = SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(borderlessStyle));
        _ = SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFramechanged);

        var cornerPreference = DwmwcpDoNotRound;
        _ = DwmSetWindowAttribute(
            hwnd,
            DwmwaWindowCornerPreference,
            ref cornerPreference,
            Marshal.SizeOf<int>());
        var borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(
            hwnd,
            DwmwaBorderColor,
            ref borderColor,
            Marshal.SizeOf<int>());
    }

    internal static void MoveAndResizeWindow(IntPtr hwnd, RectInt32 bounds)
    {
        if (!SetWindowPos(
                hwnd,
                IntPtr.Zero,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder))
        {
            throw new InvalidOperationException($"无法设置窗口位置，Win32={Marshal.GetLastWin32Error()}。");
        }
    }

    internal static void MoveWindow(IntPtr hwnd, PointInt32 position)
    {
        if (!SetWindowPos(
                hwnd,
                IntPtr.Zero,
                position.X,
                position.Y,
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder))
        {
            throw new InvalidOperationException($"无法移动窗口，Win32={Marshal.GetLastWin32Error()}。");
        }
    }

    internal static void SetWindowOwner(IntPtr hwnd, IntPtr owner)
    {
        _ = SetWindowLongPtr(hwnd, GwlpHwndParent, owner);
    }

    // Avalonia reports left clicks through TrayIcon.Clicked but sends right clicks only to the tray
    // window procedure, so that procedure is replaced: the released button is remembered and the
    // message is forwarded to Avalonia unchanged.
    private static bool _lastClickWasRight;
    internal static bool LastTrayClickWasRight => _lastClickWasRight;

    private static readonly WindowProc _trayProcedure = OnTrayMessage;
    internal static bool HookTrayWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var previous = SetWindowLongPtr(hwnd, GwlWndproc, Marshal.GetFunctionPointerForDelegate(_trayProcedure));
        _originalTrayProcedure = previous;
        return previous != IntPtr.Zero;
    }

    private static IntPtr OnTrayMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmRButtonUp) _lastClickWasRight = true;
        else if (message == WmLButtonUp) _lastClickWasRight = false;
        return CallWindowProc(_originalTrayProcedure, hwnd, message, wParam, lParam);
    }

    // The tray icon's message window belongs to this process. Avalonia names the windows it creates
    // "AvaloniaMessageWindow <guid>"; the longest such class belongs to the tray icon (the app's own
    // message window is registered first and has a shorter name), so the process's windows are
    // enumerated and the matching one with the longest class name is chosen.
    internal static IntPtr FindOwnWindow()
    {
        var found = IntPtr.Zero;
        var longest = 0;
        var process = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != process) return true;
            var name = new char[256];
            var length = GetClassName(hwnd, name, name.Length);
            var text = new string(name, 0, Math.Max(0, length));
            if (text.StartsWith("AvaloniaMessageWindow", StringComparison.Ordinal) && text.Length > longest)
            {
                longest = text.Length; found = hwnd;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }


    internal static PixelPoint PointerPosition() =>
        GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : new PixelPoint(0, 0);

    internal static bool SendMouseWheel(int delta)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = InputMouse,
                union = new InputUnion
                {
                    mouse = new MOUSEINPUT
                    {
                        mouseData = unchecked((uint)delta),
                        flags = MouseeventfWheel
                    }
                }
            }
        };
        return SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1;
    }

    internal static void MakeWindowClickThroughAndExcludedFromCapture(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        _ = SetWindowLongPtr(
            hwnd,
            GwlExstyle,
            new IntPtr(style | WsExTransparent | WsExToolwindow | WsExNoactivate));
        _ = SetWindowDisplayAffinity(hwnd, WdaExcludefromcapture);
    }

    internal static void ExcludeWindowFromCapture(IntPtr hwnd)
    {
        _ = SetWindowDisplayAffinity(hwnd, WdaExcludefromcapture);
    }

    internal static bool TryGetExtendedFrameBounds(IntPtr hwnd, out Rect bounds)
    {
        return DwmGetWindowAttributeRect(
            hwnd,
            DwmwaExtendedFrameBounds,
            out bounds,
            Marshal.SizeOf<Rect>()) == 0;
    }

    internal static bool IsWindowCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttributeInt(
            hwnd,
            DwmwaCloaked,
            out var cloaked,
            Marshal.SizeOf<int>()) == 0 && cloaked != 0;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttributeRect(
        IntPtr hwnd,
        int attribute,
        out Rect attributeValue,
        int attributeSize);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttributeInt(
        IntPtr hwnd,
        int attribute,
        out int attributeValue,
        int attributeSize);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    // Subclassing the tray icon's window needs the window-procedure slot and the original procedure.
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmLButtonUp = 0x0202;
    private static IntPtr _originalTrayProcedure;

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial IntPtr CallWindowProc(IntPtr procedure, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hwnd, out Rect bounds);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetClassName(IntPtr hwnd, [Out] char[] className, int maxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(IntPtr hook);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint SendInput(uint count, INPUT[] inputs, int size);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out CursorPoint point);

    private struct CursorPoint { public int X, Y; }

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool value);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr SetFocus(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetParent(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindowEx(
        IntPtr parent,
        IntPtr childAfter,
        string? className,
        string? windowName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsChild(IntPtr parent, IntPtr child);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    [LibraryImport("user32.dll")]
    internal static partial int GetDlgCtrlID(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetWindowText(IntPtr hwnd, [Out] char[] text, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr SendMessageTimeout(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetKeyboardState([Out] byte[] keyState);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetKeyboardState(byte[] keyState);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string message);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        internal uint type;
        internal InputUnion union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct InputUnion
    {
        [FieldOffset(0)] internal MOUSEINPUT mouse;
        [FieldOffset(0)] internal KEYBDINPUT keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        internal int dx;
        internal int dy;
        internal uint mouseData;
        internal uint flags;
        internal uint time;
        internal UIntPtr extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        internal ushort virtualKey;
        internal ushort scanCode;
        internal uint flags;
        internal uint time;
        internal UIntPtr extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        internal int size;
        internal int flags;
        internal IntPtr activeWindow;
        internal IntPtr focusWindow;
        internal IntPtr captureWindow;
        internal IntPtr menuOwner;
        internal IntPtr moveSizeWindow;
        internal IntPtr caretWindow;
        internal Rect caretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int left;
        internal int top;
        internal int right;
        internal int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShFileInfo
    {
        internal IntPtr icon;
        internal int iconIndex;
        internal uint attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string displayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] internal string typeName;
    }
}
#endif
