using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class TrayMenuTests
{
    [AvaloniaFact]
    public void RefreshingTrayTitlesSkipsSeparatorsSoEachShortcutStaysOnItsAction()
    {
        var capture = "F6";
        var pin = "F7";
        var toggle = "F8";
        var chosen = new List<string>();
        var entries = Entries(chosen, () => capture, () => pin, () => toggle);
        var menu = ResidentMenu.Create(entries);

        // InitializeAsync raises ShortcutsChanged once shortcuts are loaded, before the user opens the menu.
        ResidentMenu.Retitle(menu, ResidentMenu.Titles(entries));

        Assert.Equal(
        [
            "打开 Tbox", "-", "区域截图   F6", "剪贴板贴图   F7", "隐藏 / 显示贴图   F8", "-", "退出 Tbox"
        ], menu.Items.Select(Header).ToArray());
        Assert.Equal(2, menu.Items.OfType<NativeMenuItemSeparator>().Count());

        Click(Choice(menu, "区域截图"));
        Click(Choice(menu, "剪贴板贴图"));
        Click(Choice(menu, "隐藏 / 显示贴图"));
        Click(Choice(menu, "退出 Tbox"));
        Assert.Equal(["capture", "pin", "toggle", "quit"], chosen);

        capture = "Ctrl+Shift+A";
        pin = "Ctrl+Shift+B";
        toggle = "Ctrl+Shift+C";
        chosen.Clear();
        ResidentMenu.Retitle(menu, ResidentMenu.Titles(entries));

        Assert.Equal(
        [
            "打开 Tbox", "-", "区域截图   Ctrl+Shift+A", "剪贴板贴图   Ctrl+Shift+B",
            "隐藏 / 显示贴图   Ctrl+Shift+C", "-", "退出 Tbox"
        ], menu.Items.Select(Header).ToArray());
        Click(Choice(menu, "Ctrl+Shift+A"));
        Click(Choice(menu, "Ctrl+Shift+B"));
        Click(Choice(menu, "Ctrl+Shift+C"));
        Assert.Equal(["capture", "pin", "toggle"], chosen);
        Assert.Single(menu.Items.OfType<NativeMenuItem>(), item => item is not NativeMenuItemSeparator && item.Header == "退出 Tbox");
    }

    [Fact]
    public void MenuBarCommandsSkipSeparatorsSoEachChoiceRunsItsOwnAction()
    {
        var chosen = new List<string>();
        var entries = Entries(chosen, () => "F6", () => "F7", () => "F8");
        var updates = new List<(int Command, string Title)>();
        ResidentMenu.RetitleMac(entries, (command, title) => updates.Add((command, title)));

        Assert.Equal(
        [
            (1, "打开 Tbox"), (2, "区域截图   F6"), (3, "剪贴板贴图   F7"),
            (4, "隐藏 / 显示贴图   F8"), (5, "退出 Tbox")
        ], updates);
        Assert.False(ResidentMenu.Choose(entries, 0));
        Assert.Empty(chosen);
        Assert.True(ResidentMenu.Choose(entries, 2));
        Assert.True(ResidentMenu.Choose(entries, 3));
        Assert.True(ResidentMenu.Choose(entries, 4));
        Assert.True(ResidentMenu.Choose(entries, 5));
        Assert.False(ResidentMenu.Choose(entries, 6));
        Assert.Equal(["capture", "pin", "toggle", "quit"], chosen);
    }

    private static (Func<string>? Title, Action Action)[] Entries(
        List<string> chosen, Func<string> capture, Func<string> pin, Func<string> toggle) =>
    [
        (() => "打开 Tbox", () => chosen.Add("open")), (null, () => { }),
        (() => $"区域截图   {capture()}", () => chosen.Add("capture")),
        (() => $"剪贴板贴图   {pin()}", () => chosen.Add("pin")),
        (() => $"隐藏 / 显示贴图   {toggle()}", () => chosen.Add("toggle")), (null, () => { }),
        (() => "退出 Tbox", () => chosen.Add("quit"))
    ];

    private static string Header(object item) => item is NativeMenuItem menuItem ? menuItem.Header ?? "" : "";

    private static NativeMenuItem Choice(NativeMenu menu, string title) =>
        menu.Items.OfType<NativeMenuItem>().Single(item => item is not NativeMenuItemSeparator && item.Header?.Contains(title) == true);

    private static void Click(NativeMenuItem item) => ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();
}
