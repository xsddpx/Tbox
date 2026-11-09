namespace Tbox.Core;

public sealed record HotkeySettings(bool Control = true, bool Alt = false, bool Shift = false, bool Windows = false, int Key = 0x47)
{
    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if (Control) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            if (Windows) parts.Add("Win");
            parts.Add(((char)Key).ToString().ToUpperInvariant());
            return string.Join(" + ", parts);
        }
    }
}

public sealed record AppSettings
{
    public bool FolderJumpEnabled { get; init; } = true;
    public bool ScreenPasteEnabled { get; init; } = true;
    public string? ScreenCaptureCacheDirectory { get; init; }
    public bool StartWithWindows { get; init; }
    public bool ShowFailureNotifications { get; init; } = true;
    public string AppTheme { get; init; } = "system";
    public HotkeySettings FolderJumpHotkey { get; init; } = new();
    // Shared desktop app: global shortcuts chosen in the main window; unset entries use the platform defaults.
    public HotkeyBindings? Hotkeys { get; init; }
}
