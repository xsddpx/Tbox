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
    private const ushort AltKey = 0x12;
    private const ushort DKey = 0x44;

    internal static bool IsStandardDialog(IntPtr window) =>
        window != IntPtr.Zero && NativeMethods.IsWindowVisible(window) && HasClass(window, "#32770") &&
        FindAddressBand(window) != IntPtr.Zero;

    internal static NavigationResult Navigate(IntPtr window, string path)
    {
        if (!Directory.Exists(path)) return NavigationResult.InvalidPath;
        if (!HasClass(window, "#32770") || !IsForeground(window)) return NavigationResult.UnsupportedWindow;

        // Resolve the shell address band before the shortcut can hide or replace its breadcrumb.
        var addressHost = FindAddressBand(window);
        if (addressHost == IntPtr.Zero) return NavigationResult.UnsupportedWindow;
        var addressBar = FindFocusedAddressEditor(window, addressHost);
        if (addressBar == IntPtr.Zero)
        {
            addressBar = OpenAddressEditor(window, addressHost, NativeMethods.VkControl, NativeMethods.VkL);
            if (addressBar == IntPtr.Zero) addressBar = OpenAddressEditor(window, addressHost, AltKey, DKey);
        }
        if (addressBar == IntPtr.Zero)
        {
            AppLog.Write("File-dialog navigation could not find the address-bar editor.");
            return NavigationResult.InputFailed;
        }
        return NavigateAddress(window, addressHost, addressBar, path) ? NavigationResult.Success : NavigationResult.InputFailed;
    }

    private static bool NavigateAddress(IntPtr dialog, IntPtr addressHost, IntPtr addressBar, string path)
    {
        if (FindFocusedAddressEditor(dialog, addressHost) != addressBar) return false;
        var setResult = NativeMethods.SendMessageTimeout(
            addressBar,
            NativeMethods.WmSetText,
            IntPtr.Zero,
            path,
            NativeMethods.SmtoAbortIfHung,
            750,
            out var textSet);
        if (setResult == IntPtr.Zero || textSet == 0)
        {
            AppLog.Write($"File-dialog navigation could not set the address-bar text. Win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return false;
        }

        // Submit once, and only while the confirmed editor still has focus.
        if (FindFocusedAddressEditor(dialog, addressHost) != addressBar ||
            !SendKeys(Key(NativeMethods.VkReturn), Key(NativeMethods.VkReturn, NativeMethods.KeyeventfKeyup))) return false;
        if (WaitForNavigation(dialog, addressHost, addressBar, path, TimeSpan.FromMilliseconds(1500))) return true;
        AppLog.Write("File-dialog navigation could not confirm the destination after submitting the address.");
        return false;
    }

    private static IntPtr OpenAddressEditor(IntPtr dialog, IntPtr addressHost, ushort modifier, ushort key)
    {
        if (!IsForeground(dialog)) return IntPtr.Zero;
        var focused = FindFocusedAddressEditor(dialog, addressHost);
        if (focused != IntPtr.Zero) return focused;
        if (!SendKeys(Key(modifier), Key(key), Key(key, NativeMethods.KeyeventfKeyup),
                Key(modifier, NativeMethods.KeyeventfKeyup))) return IntPtr.Zero;
        var deadline = Environment.TickCount64 + 500;
        do
        {
            if (!IsForeground(dialog)) break;
            focused = FindFocusedAddressEditor(dialog, addressHost);
            if (focused != IntPtr.Zero) return focused;
            Thread.Sleep(10);
        }
        while (Environment.TickCount64 < deadline);
        return IntPtr.Zero;
    }

    private static IntPtr FindFocusedAddressEditor(IntPtr dialog, IntPtr addressHost)
    {
        if (!IsForeground(dialog) || !NativeMethods.IsChild(dialog, addressHost)) return IntPtr.Zero;
        var info = new NativeMethods.GuiThreadInfo
        {
            size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
        };
        var thread = NativeMethods.GetWindowThreadProcessId(dialog, out _);
        return thread != 0 && NativeMethods.GetGUIThreadInfo(thread, ref info) &&
            NativeMethods.IsChild(addressHost, info.focusWindow) && NativeMethods.IsWindowVisible(info.focusWindow) &&
            HasClass(info.focusWindow, "Edit") ? info.focusWindow : IntPtr.Zero;
    }

    private static bool IsForeground(IntPtr dialog) => NativeMethods.GetForegroundWindow() == dialog;

    private static bool SendKeys(params NativeMethods.INPUT[] inputs) =>
        NativeMethods.SendInput((uint)inputs.Length, inputs,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>()) == inputs.Length;

    private static bool WaitForNavigation(IntPtr dialog, IntPtr addressHost, IntPtr editor, string targetPath, TimeSpan timeout)
    {
        var shellDisplayName = GetShellDisplayName(targetPath);
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        do
        {
            if (!IsForeground(dialog)) return false;
            if (!NativeMethods.IsWindowVisible(editor) &&
                TryGetCurrentAddress(addressHost, out var addressText) &&
                AddressTextMatches(targetPath, addressText, shellDisplayName))
            {
                return true;
            }
            Thread.Sleep(10);
        }
        while (Environment.TickCount64 < deadline);
        return false;
    }

    private static bool TryGetCurrentAddress(IntPtr addressHost, out string addressText)
    {
        addressText = string.Empty;
        var addressToolbar = FindAddressToolbar(addressHost);
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

    private static IntPtr FindAddressBand(IntPtr dialog)
    {
        var found = IntPtr.Zero;
        NativeMethods.EnumChildWindows(dialog, (window, _) =>
        {
            if (!HasClass(window, "Address Band Root") || !NativeMethods.IsWindowVisible(window)) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
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
