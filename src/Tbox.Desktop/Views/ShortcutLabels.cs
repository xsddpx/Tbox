using Tbox.Core;

namespace Tbox.Desktop.Views;

internal static class ShortcutLabels
{
    // The saved global shortcuts. Set by the controller; defaults apply until settings load.
    internal static Func<HotkeyBindings?> Bindings { get; set; } = () => null;
    public static Shortcut For(HotkeyAction action) => ShortcutPolicy.Effective(Bindings(), action, OperatingSystem.IsMacOS());
    public static string[] Caps(HotkeyAction action) => For(action).Caps(OperatingSystem.IsMacOS());
    public static string Label(HotkeyAction action) => For(action).Display(OperatingSystem.IsMacOS());

    public static string Capture => Label(HotkeyAction.Capture);
    public static string Pin => Label(HotkeyAction.Pin);
    public static string Toggle => Label(HotkeyAction.Toggle);
    public static string Folder => Label(HotkeyAction.Folder);
    public static string Command => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
}
