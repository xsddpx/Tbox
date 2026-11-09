namespace Tbox.Core;

// Global hotkeys Tbox registers. The values are the ids the native hotkey callbacks report.
public enum HotkeyAction { Capture = 1, Pin = 2, Toggle = 3, Folder = 4 }

// A key combination. Meta is the Windows key on Windows and Command on macOS. Key is a
// platform-neutral name: "A"–"Z", "0"–"9", "F1"–"F12" or "PrintScreen".
public sealed record Shortcut(bool Control, bool Alt, bool Shift, bool Meta, string Key)
{
    public string[] Caps(bool mac)
    {
        var caps = new List<string>();
        if (Control) caps.Add(mac ? "⌃" : "Ctrl");
        if (Alt) caps.Add(mac ? "⌥" : "Alt");
        if (Shift) caps.Add(mac ? "⇧" : "Shift");
        if (Meta) caps.Add(mac ? "⌘" : "Win");
        caps.Add(Key == "PrintScreen" ? "PrtSc" : Key);
        return [.. caps];
    }

    // "Ctrl+Shift+A" on Windows, "⌃⇧A" on macOS.
    public string Display(bool mac) => string.Join(mac ? "" : "+", Caps(mac));
}

public sealed record HotkeyBinding(HotkeyAction Action, Shortcut Shortcut);

// Saved choices; a null entry means the platform default.
public sealed record HotkeyBindings(Shortcut? Capture = null, Shortcut? Pin = null, Shortcut? Toggle = null, Shortcut? Folder = null)
{
    public Shortcut? Get(HotkeyAction action) => action switch
    {
        HotkeyAction.Capture => Capture, HotkeyAction.Pin => Pin, HotkeyAction.Toggle => Toggle, _ => Folder
    };

    public HotkeyBindings With(HotkeyAction action, Shortcut? shortcut) => action switch
    {
        HotkeyAction.Capture => this with { Capture = shortcut }, HotkeyAction.Pin => this with { Pin = shortcut },
        HotkeyAction.Toggle => this with { Toggle = shortcut }, _ => this with { Folder = shortcut }
    };
}

public static class ShortcutPolicy
{
    public static readonly HotkeyAction[] Actions = [HotkeyAction.Capture, HotkeyAction.Pin, HotkeyAction.Toggle, HotkeyAction.Folder];

    public static string Name(HotkeyAction action) => action switch
    {
        HotkeyAction.Capture => "截图", HotkeyAction.Pin => "贴出剪贴板", HotkeyAction.Toggle => "显隐贴图", _ => "文件夹跳转"
    };

    // Windows keeps F6 / F7 / F8 / Ctrl+G; macOS keeps ⌃⇧1 / ⌃⇧2 / ⌃⇧3 / ⌃G.
    public static Shortcut Default(HotkeyAction action, bool mac) => (action, mac) switch
    {
        (HotkeyAction.Folder, _) => new(true, false, false, false, "G"),
        (_, true) => new(true, false, true, false, ((int)action).ToString()),
        _ => new(false, false, false, false, $"F{5 + (int)action}")
    };

    public static Shortcut Effective(HotkeyBindings? bindings, HotkeyAction action, bool mac) =>
        bindings?.Get(action) ?? Default(action, mac);

    // Null when the shortcut can be registered; otherwise the reason, written for the settings row.
    public static string? Validate(Shortcut shortcut, bool mac)
    {
        if (WindowsKeyCode(shortcut.Key) is null || mac && MacKeyCode(shortcut.Key) is null)
            return mac ? "不支持这个按键，请使用字母、数字或 F1–F12。" : "不支持这个按键，请使用字母、数字、F1–F12 或 PrtSc。";
        var typed = shortcut.Key.Length == 1;
        if (typed && !shortcut.Control && !shortcut.Alt && !shortcut.Meta)
            return mac ? "字母和数字需要搭配 ⌃、⌥ 或 ⌘。" : "字母和数字需要搭配 Ctrl、Alt 或 Win。";
        // A global hotkey would take these editing shortcuts away from every application.
        if (typed && !mac && shortcut is { Control: true, Alt: false, Shift: false, Meta: false } && "ACVXYZS".Contains(shortcut.Key[0]))
            return "这是常用的编辑快捷键，请换一个组合。";
        if (typed && mac && shortcut is { Meta: true, Control: false, Alt: false })
            return "⌘ 加字母或数字是应用常用的快捷键，请再加上 ⌃ 或 ⌥。";
        return null;
    }

    // Virtual-key codes for RegisterHotKey.
    public static int? WindowsKeyCode(string key) => key switch
    {
        [var c] when c is >= 'A' and <= 'Z' || c is >= '0' and <= '9' => c,
        "PrintScreen" => 0x2C,
        _ when FunctionNumber(key) is { } n => 0x6F + n,
        _ => null
    };

    // Carbon virtual key codes (kVK_*) for RegisterEventHotKey; they follow the ANSI key positions.
    public static int? MacKeyCode(string key)
    {
        if (FunctionNumber(key) is { } n) return MacFunctionKeys[n - 1];
        var index = key.Length == 1 ? MacKeys.IndexOf(key[0]) : -1;
        return index < 0 ? null : MacKeyCodes[index];
    }

    private const string MacKeys = "ASDFHGZXCVBQWERYT123465978OUIPLJKNM0";
    private static readonly int[] MacKeyCodes =
    [
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11,
        0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x19, 0x1A, 0x1C, 0x1F, 0x20, 0x22, 0x23, 0x25, 0x26, 0x28, 0x2D, 0x2E, 0x1D
    ];
    private static readonly int[] MacFunctionKeys = [0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F];

    private static int? FunctionNumber(string key) =>
        key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var n) && n is >= 1 and <= 12 && key[1] != '0' ? n : null;
}
