using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Tbox.App;

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
    internal const ushort VkControl = 0x11;
    internal const ushort VkShift = 0x10;
    internal const ushort VkG = 0x47;
    internal const ushort VkL = 0x4C;
    internal const ushort VkReturn = 0x0D;
    internal const uint InputKeyboard = 1;
    internal const uint WmSetText = 0x000C;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const uint WmChar = 0x0102;
    internal const uint SmtoAbortIfHung = 0x0002;
    internal const uint GaRoot = 2;
    internal const uint ShgfiDisplayName = 0x000000200;
    private const int DwmwaTransitionsForcedisabled = 3;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    internal delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time);
    internal delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

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

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
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

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

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

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int virtualKey);

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
        [FieldOffset(0)] internal KEYBDINPUT keyboard;
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
