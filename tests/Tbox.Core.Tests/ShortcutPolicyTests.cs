using System.Text.Json;
using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class ShortcutPolicyTests
{
    [Theory]
    [InlineData(HotkeyAction.Capture, false, "F6")]
    [InlineData(HotkeyAction.Pin, false, "F7")]
    [InlineData(HotkeyAction.Toggle, false, "F8")]
    [InlineData(HotkeyAction.Folder, false, "Ctrl+G")]
    [InlineData(HotkeyAction.Capture, true, "⌃⇧1")]
    [InlineData(HotkeyAction.Toggle, true, "⌃⇧3")]
    [InlineData(HotkeyAction.Folder, true, "⌃G")]
    public void DefaultsKeepTheExistingShortcuts(HotkeyAction action, bool mac, string expected) =>
        Assert.Equal(expected, ShortcutPolicy.Default(action, mac).Display(mac));

    [Fact]
    public void SavedChoicesOverrideOnlyTheirAction()
    {
        var bindings = new HotkeyBindings().With(HotkeyAction.Pin, new Shortcut(true, true, false, false, "P"));
        Assert.Equal("Ctrl+Alt+P", ShortcutPolicy.Effective(bindings, HotkeyAction.Pin, false).Display(false));
        Assert.Equal("F6", ShortcutPolicy.Effective(bindings, HotkeyAction.Capture, false).Display(false));
        Assert.Equal(new HotkeyBindings(), bindings.With(HotkeyAction.Pin, null));
    }

    [Theory]
    [InlineData(false, false, false, false, "A", false)] // A bare letter would stop typing.
    [InlineData(true, false, false, false, "C", false)]  // Ctrl+C belongs to every application.
    [InlineData(true, false, true, false, "C", true)]
    [InlineData(false, false, false, false, "F9", true)]
    [InlineData(false, false, false, false, "PrintScreen", true)]
    [InlineData(true, false, false, false, "Tab", false)]
    [InlineData(false, true, false, false, "1", true)]
    public void WindowsValidationRejectsTypingAndEditingKeys(bool control, bool alt, bool shift, bool meta, string key, bool valid) =>
        Assert.Equal(valid, ShortcutPolicy.Validate(new Shortcut(control, alt, shift, meta, key), mac: false) is null);

    [Theory]
    [InlineData(false, false, false, true, "C", false)] // ⌘C is an application shortcut.
    [InlineData(true, false, false, true, "C", true)]
    [InlineData(false, false, false, false, "PrintScreen", false)]
    [InlineData(true, false, true, false, "4", true)]
    public void MacValidationKeepsCommandShortcutsForApplications(bool control, bool alt, bool shift, bool meta, string key, bool valid) =>
        Assert.Equal(valid, ShortcutPolicy.Validate(new Shortcut(control, alt, shift, meta, key), mac: true) is null);

    [Theory]
    [InlineData("A", 0x41, 0x00)]
    [InlineData("G", 0x47, 0x05)]
    [InlineData("1", 0x31, 0x12)]
    [InlineData("0", 0x30, 0x1D)]
    [InlineData("M", 0x4D, 0x2E)]
    [InlineData("F6", 0x75, 0x61)]
    [InlineData("F12", 0x7B, 0x6F)]
    public void KeysMapToNativeCodes(string key, int windows, int mac)
    {
        Assert.Equal(windows, ShortcutPolicy.WindowsKeyCode(key));
        Assert.Equal(mac, ShortcutPolicy.MacKeyCode(key));
    }

    [Fact]
    public void EveryLetterAndDigitHasADistinctMacCode()
    {
        var keys = Enumerable.Range('A', 26).Concat(Enumerable.Range('0', 10)).Select(c => ((char)c).ToString()).ToArray();
        var codes = keys.Select(ShortcutPolicy.MacKeyCode).ToArray();
        Assert.All(codes, code => Assert.NotNull(code));
        Assert.Equal(keys.Length, codes.Distinct().Count());
        Assert.Null(ShortcutPolicy.MacKeyCode("F13")); Assert.Null(ShortcutPolicy.WindowsKeyCode("F0"));
    }

    [Fact]
    public void SettingsWithoutShortcutsStillLoadAndRoundTrip()
    {
        Assert.Null(JsonSerializer.Deserialize<AppSettings>("{\"ScreenPasteEnabled\":true}")!.Hotkeys);
        var settings = new AppSettings { Hotkeys = new HotkeyBindings(Folder: new Shortcut(true, false, true, false, "J")) };
        Assert.Equal(settings, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)));
    }
}
