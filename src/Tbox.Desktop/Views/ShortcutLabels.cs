namespace Tbox.Desktop.Views;

internal static class ShortcutLabels
{
    public static string Capture => OperatingSystem.IsMacOS() ? "⌃⇧1" : "F6";
    public static string Pin => OperatingSystem.IsMacOS() ? "⌃⇧2" : "F7";
    public static string Toggle => OperatingSystem.IsMacOS() ? "⌃⇧3" : "F8";
    public static string Folder => OperatingSystem.IsMacOS() ? "⌃G" : "Ctrl+G";
    public static string Command => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
}
