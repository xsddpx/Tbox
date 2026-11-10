#if WINDOWS
namespace Tbox.Desktop.Platform;

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
        if (!FocusAddressBar(dialog))
        {
            AppLog.Write("File-dialog navigation failed while sending Ctrl+L.");
            return false;
        }

        var addressBar = WaitForAddressBar(dialog, TimeSpan.FromMilliseconds(500));
        if (addressBar == IntPtr.Zero)
        {
            AppLog.Write("File-dialog navigation could not find the address-bar editor.");
            return false;
        }
        NativeMethods.SetFocus(addressBar);
        var setResult = NativeMethods.SendMessageTimeout(
            addressBar,
            NativeMethods.WmSetText,
            IntPtr.Zero,
            path,
            NativeMethods.SmtoAbortIfHung,
            750,
            out _);
        if (setResult == IntPtr.Zero)
        {
            AppLog.Write($"File-dialog navigation could not set the address-bar text. Win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return false;
        }

        NativeMethods.PostMessage(addressBar, NativeMethods.WmKeyDown, NativeMethods.VkReturn, IntPtr.Zero);
        NativeMethods.PostMessage(addressBar, NativeMethods.WmKeyUp, NativeMethods.VkReturn, IntPtr.Zero);
        if (WaitForNavigation(dialog, addressBar, path, TimeSpan.FromMilliseconds(350))) return true;

        NativeMethods.SetFocus(addressBar);
        NativeMethods.PostMessage(addressBar, NativeMethods.WmChar, NativeMethods.VkReturn, IntPtr.Zero);
        if (WaitForNavigation(dialog, addressBar, path, TimeSpan.FromMilliseconds(350))) return true;

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
        return WaitForNavigation(dialog, addressBar, path, TimeSpan.FromMilliseconds(500));
    }

    private static bool FocusAddressBar(IntPtr dialog)
    {
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
        return sent == focusInputs.Length || PostAddressBarShortcut(dialog);
    }

    private static IntPtr WaitForAddressBar(IntPtr dialog, TimeSpan timeout)
    {
        var toolbar = FindAddressToolbar(dialog);
        var addressHost = NativeMethods.GetParent(toolbar);
        // Native open/save dialogs put the breadcrumb and editor in sibling
        // containers below Address Band Root (the breadcrumb hides on Ctrl+L).
        for (var depth = 0; depth < 4 && addressHost != IntPtr.Zero && addressHost != dialog &&
             !HasClass(addressHost, "Address Band Root"); depth++)
            addressHost = NativeMethods.GetParent(addressHost);
        if (toolbar == IntPtr.Zero || !HasClass(addressHost, "Address Band Root")) return IntPtr.Zero;
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
                // SendInput queues Ctrl+L; the file-name Edit may still own
                // focus until the dialog processes it. Only accept the editor
                // within the address band, never any dialog Edit.
                NativeMethods.IsChild(addressHost, info.focusWindow) &&
                NativeMethods.IsWindowVisible(info.focusWindow) &&
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

    private static bool WaitForNavigation(IntPtr dialog, IntPtr editor, string targetPath, TimeSpan timeout)
    {
        var shellDisplayName = GetShellDisplayName(targetPath);
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        do
        {
            if (!NativeMethods.IsWindowVisible(editor) &&
                TryGetCurrentAddress(dialog, out var addressText) &&
                AddressTextMatches(targetPath, addressText, shellDisplayName))
            {
                return true;
            }
            Thread.Sleep(10);
        }
        while (Environment.TickCount64 < deadline);
        return false;
    }

    internal static bool TryGetCurrentAddress(IntPtr dialog, out string addressText)
    {
        addressText = string.Empty;
        var addressToolbar = FindAddressToolbar(dialog);
        if (addressToolbar == IntPtr.Zero) return false;

        var text = new char[512];
        var length = NativeMethods.GetWindowText(addressToolbar, text, text.Length);
        if (length <= 0) return false;

        addressText = new string(text, 0, length);
        return true;
    }

    internal static bool AddressTextMatches(string targetPath, string addressText, string? shellDisplayName)
    {
        var trimmedTarget = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fileSystemName = Path.GetFileName(trimmedTarget);
        if (string.IsNullOrEmpty(fileSystemName)) fileSystemName = trimmedTarget;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fileSystemName };
        if (!string.IsNullOrWhiteSpace(shellDisplayName)) names.Add(shellDisplayName);
        return names.Any(name =>
            !string.IsNullOrWhiteSpace(name) &&
            ContainsAddressSegment(addressText, name));
    }

    private static bool ContainsAddressSegment(string addressText, string name)
    {
        var startIndex = 0;
        while (startIndex < addressText.Length)
        {
            var match = addressText.IndexOf(name, startIndex, StringComparison.OrdinalIgnoreCase);
            if (match < 0) return false;
            var beforeIsBoundary = match == 0 || IsAddressBoundary(addressText[match - 1]);
            var end = match + name.Length;
            var afterIsBoundary = end == addressText.Length || IsAddressBoundary(addressText[end]);
            if (beforeIsBoundary && afterIsBoundary) return true;
            startIndex = match + 1;
        }
        return false;
    }

    private static bool IsAddressBoundary(char character) =>
        char.IsWhiteSpace(character) || character is '>' or '<' or ':' or '\\' or '/';

    private static string? GetShellDisplayName(string targetPath)
    {
        var shellInfo = new NativeMethods.ShFileInfo();
        if (NativeMethods.SHGetFileInfo(
                targetPath,
                0,
                ref shellInfo,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.ShFileInfo>(),
                NativeMethods.ShgfiDisplayName) != IntPtr.Zero &&
            !string.IsNullOrWhiteSpace(shellInfo.displayName))
        {
            return shellInfo.displayName;
        }
        return null;
    }

    private static IntPtr FindAddressToolbar(IntPtr parent)
    {
        var found = IntPtr.Zero;
        NativeMethods.EnumChildProc callback = (window, _) =>
        {
            // Save dialogs also use ID 1001 for the file-name Edit. Control
            // IDs are only unique within their own parent, not the dialog tree.
            if (NativeMethods.GetDlgCtrlID(window) != AddressToolbarId || !HasClass(window, "ToolbarWindow32")) return true;
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
#endif
