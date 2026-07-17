using System.Runtime.InteropServices;

namespace Tbox.App;

internal sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint NifInfo = 16;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const uint WmRightButtonUp = 0x0205;
    private const uint MfString = 0;
    private const uint MfSeparator = 0x0800;
    private const uint TpmReturnCommand = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const int OpenCommand = 1;
    private const int ExitCommand = 2;

    private readonly IntPtr _ownerWindow;
    private readonly IntPtr _messageWindow;
    private readonly Action _showSettings;
    private readonly Action _exit;
    private readonly IDisposable _messageSubscription;
    private readonly uint _taskbarCreatedMessage;
    private NotifyIconData _data;
    private bool _disposed;

    internal TrayIconService(IntPtr window, WindowMessageRouter messageRouter, Action showSettings, Action exit)
    {
        _ownerWindow = window;
        _messageWindow = messageRouter.Handle;
        _showSettings = showSettings;
        _exit = exit;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _messageSubscription = messageRouter.Subscribe(OnWindowMessage);
        _data = CreateData();
        AddIcon();
    }

    internal void ShowWarning(string message)
    {
        if (_disposed) return;
        _data.flags = NifInfo;
        _data.infoTitle = "Tbox · 文件夹跳转";
        _data.info = message;
        _data.infoFlags = 2;
        ShellNotifyIcon(NimModify, ref _data);
        _data.flags = NifMessage | NifIcon | NifTip;
    }

    private void AddIcon()
    {
        _data.flags = NifMessage | NifIcon | NifTip;
        if (!ShellNotifyIcon(NimAdd, ref _data))
        {
            AppLog.Write($"Shell_NotifyIcon add failed. Win32={Marshal.GetLastWin32Error()}");
        }
    }

    private NotifyIconData CreateData() => new()
    {
        size = (uint)Marshal.SizeOf<NotifyIconData>(),
        window = _messageWindow,
        id = 1,
        flags = NifMessage | NifIcon | NifTip,
        callbackMessage = CallbackMessage,
        icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)),
        tip = "Tbox",
        info = string.Empty,
        infoTitle = string.Empty
    };

    private bool OnWindowMessage(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (message == _taskbarCreatedMessage)
        {
            AddIcon();
            return true;
        }
        if (message != CallbackMessage) return false;

        var mouseMessage = unchecked((uint)lParam.ToInt64());
        if (mouseMessage == WmLeftButtonDoubleClick)
        {
            _showSettings();
        }
        else if (mouseMessage == WmRightButtonUp)
        {
            ShowMenu();
        }
        return true;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MfString, OpenCommand, "打开 Tbox");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, ExitCommand, "退出");
            GetCursorPos(out var point);
            SetForegroundWindow(_ownerWindow);
            var command = TrackPopupMenu(
                menu,
                TpmReturnCommand | TpmRightButton,
                point.x,
                point.y,
                0,
                _ownerWindow,
                IntPtr.Zero);
            if (command == OpenCommand) _showSettings();
            if (command == ExitCommand) _exit();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        ShellNotifyIcon(NimDelete, ref _data);
        _messageSubscription.Dispose();
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal uint size;
        internal IntPtr window;
        internal uint id;
        internal uint flags;
        internal uint callbackMessage;
        internal IntPtr icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string tip;
        internal uint state;
        internal uint stateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string info;
        internal uint timeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string infoTitle;
        internal uint infoFlags;
        internal Guid item;
        internal IntPtr balloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int x;
        internal int y;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nuint item, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
