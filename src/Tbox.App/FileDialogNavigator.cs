namespace Tbox.App;

internal enum NavigationResult
{
    Success,
    UnsupportedWindow,
    InvalidPath,
    InputFailed
}

internal static class FileDialogNavigator
{
    private const int AddressToolbarId = 1001;

    internal static NavigationResult Navigate(IntPtr window, string path)
    {
        if (!Directory.Exists(path)) return NavigationResult.InvalidPath;
        if (!HasClass(window, "#32770")) return NavigationResult.UnsupportedWindow;

        var dialogThread = NativeMethods.GetWindowThreadProcessId(window, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = currentThread == dialogThread || NativeMethods.AttachThreadInput(currentThread, dialogThread, true);
        if (!attached)
        {
            AppLog.Write($"AttachThreadInput failed. Win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return NavigationResult.InputFailed;
        }

        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if (TryNavigateOnce(window, path)) return NavigationResult.Success;
                AppLog.Write($"File-dialog navigation attempt {attempt} failed.");
                Thread.Sleep(30);
            }
            return NavigationResult.InputFailed;
        }
        finally
        {
            if (currentThread != dialogThread)
            {
                NativeMethods.AttachThreadInput(currentThread, dialogThread, false);
            }
        }
    }

    private static bool TryNavigateOnce(IntPtr dialog, string path)
    {
        NativeMethods.SetForegroundWindow(dialog);
        var focusInputs = new[]
        {
            Key(NativeMethods.VkControl),
            Key(NativeMethods.VkL),
            Key(NativeMethods.VkL, NativeMethods.KeyeventfKeyup),
            Key(NativeMethods.VkControl, NativeMethods.KeyeventfKeyup)
        };
        var sent = NativeMethods.SendInput(
            (uint)focusInputs.Length,
            focusInputs,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != focusInputs.Length && !PostAddressBarShortcut(dialog)) return false;

        var addressBar = WaitForAddressBar(dialog, TimeSpan.FromMilliseconds(500));
        if (addressBar == IntPtr.Zero) return false;
        NativeMethods.SetFocus(addressBar);
        var setResult = NativeMethods.SendMessageTimeout(
            addressBar,
            NativeMethods.WmSetText,
            IntPtr.Zero,
            path,
            NativeMethods.SmtoAbortIfHung,
            750,
            out _);
        if (setResult == IntPtr.Zero) return false;

        NativeMethods.PostMessage(addressBar, NativeMethods.WmKeyDown, NativeMethods.VkReturn, IntPtr.Zero);
        NativeMethods.PostMessage(addressBar, NativeMethods.WmKeyUp, NativeMethods.VkReturn, IntPtr.Zero);
        if (WaitForNavigation(dialog, path, TimeSpan.FromMilliseconds(350))) return true;

        NativeMethods.SetFocus(addressBar);
        NativeMethods.PostMessage(addressBar, NativeMethods.WmChar, NativeMethods.VkReturn, IntPtr.Zero);
        if (WaitForNavigation(dialog, path, TimeSpan.FromMilliseconds(350))) return true;

        NativeMethods.SetForegroundWindow(dialog);
        NativeMethods.SetFocus(addressBar);
        var enterInputs = new[]
        {
            Key(NativeMethods.VkReturn),
            Key(NativeMethods.VkReturn, NativeMethods.KeyeventfKeyup)
        };
        NativeMethods.SendInput(
            (uint)enterInputs.Length,
            enterInputs,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
        return WaitForNavigation(dialog, path, TimeSpan.FromMilliseconds(500));
    }

    private static IntPtr WaitForAddressBar(IntPtr dialog, TimeSpan timeout)
    {
        var threadId = NativeMethods.GetWindowThreadProcessId(dialog, out _);
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        do
        {
            var info = new NativeMethods.GuiThreadInfo
            {
                size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
            };
            if (NativeMethods.GetGUIThreadInfo(threadId, ref info) &&
                info.focusWindow != IntPtr.Zero &&
                NativeMethods.IsChild(dialog, info.focusWindow) &&
                HasClass(info.focusWindow, "Edit"))
            {
                return info.focusWindow;
            }
            Thread.Sleep(10);
        }
        while (Environment.TickCount64 < deadline);
        return IntPtr.Zero;
    }

    private static bool PostAddressBarShortcut(IntPtr dialog)
    {
        var keyboardState = new byte[256];
        if (!NativeMethods.GetKeyboardState(keyboardState)) return false;
        var originalControl = keyboardState[NativeMethods.VkControl];
        try
        {
            keyboardState[NativeMethods.VkControl] |= 0x80;
            if (!NativeMethods.SetKeyboardState(keyboardState)) return false;
            return NativeMethods.PostMessage(dialog, NativeMethods.WmKeyDown, NativeMethods.VkL, IntPtr.Zero) &&
                   NativeMethods.PostMessage(dialog, NativeMethods.WmKeyUp, NativeMethods.VkL, IntPtr.Zero);
        }
        finally
        {
            keyboardState[NativeMethods.VkControl] = originalControl;
            NativeMethods.SetKeyboardState(keyboardState);
        }
    }

    private static bool WaitForNavigation(IntPtr dialog, string targetPath, TimeSpan timeout)
    {
        var addressToolbar = FindDescendantById(dialog, AddressToolbarId);
        if (addressToolbar == IntPtr.Zero) return false;
        var trimmedTarget = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expectedName = Path.GetFileName(trimmedTarget);
        if (string.IsNullOrEmpty(expectedName)) expectedName = trimmedTarget;
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        do
        {
            var text = new char[512];
            var length = NativeMethods.GetWindowText(addressToolbar, text, text.Length);
            if (length > 0 && new string(text, 0, length).Contains(expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            Thread.Sleep(10);
        }
        while (Environment.TickCount64 < deadline);
        return false;
    }

    private static IntPtr FindDescendantById(IntPtr parent, int controlId)
    {
        var found = IntPtr.Zero;
        NativeMethods.EnumChildProc callback = (window, _) =>
        {
            if (NativeMethods.GetDlgCtrlID(window) != controlId) return true;
            found = window;
            return false;
        };
        NativeMethods.EnumChildWindows(parent, callback, IntPtr.Zero);
        return found;
    }

    private static bool HasClass(IntPtr window, string expected)
    {
        var className = new char[64];
        var length = NativeMethods.GetClassName(window, className, className.Length);
        return length > 0 && new string(className, 0, length).Equals(expected, StringComparison.Ordinal);
    }

    private static NativeMethods.INPUT Key(ushort key, uint flags = 0) => new()
    {
        type = NativeMethods.InputKeyboard,
        union = new NativeMethods.InputUnion
        {
            keyboard = new NativeMethods.KEYBDINPUT { virtualKey = key, flags = flags }
        }
    };
}
