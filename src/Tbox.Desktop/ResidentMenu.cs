using Avalonia.Controls;

namespace Tbox.Desktop;

// Shared tray and menu-bar entries. A null title is a separator and is not a choice.
internal static class ResidentMenu
{
    internal static NativeMenu Create(IReadOnlyList<(Func<string>? Title, Action Action)> entries)
    {
        var menu = new NativeMenu();
        foreach (var (title, action) in entries)
        {
            if (title is null) { menu.Add(new NativeMenuItemSeparator()); continue; }
            var item = new NativeMenuItem(title());
            item.Click += (_, _) => action();
            menu.Add(item);
        }
        return menu;
    }

    internal static string[] Titles(IReadOnlyList<(Func<string>? Title, Action Action)> entries) =>
        entries.Where(entry => entry.Title is not null).Select(entry => entry.Title!.Invoke()).ToArray();

    internal static void Retitle(NativeMenu menu, IReadOnlyList<string> titles)
    {
        // NativeMenuItemSeparator derives from NativeMenuItem. Counting it writes the next title
        // onto the separator and shifts every later title, so a refresh drops 区域截图 and repeats 退出.
        var items = menu.Items.OfType<NativeMenuItem>().Where(item => item is not NativeMenuItemSeparator).ToArray();
        for (var index = 0; index < items.Length && index < titles.Count; index++) items[index].Header = titles[index];
    }

    // macOS status-item tags are 1-based and skip separators, matching TboxStatusItem.swift.
    internal static void RetitleMac(IReadOnlyList<(Func<string>? Title, Action Action)> entries, Action<int, string> retitle)
    {
        var titles = Titles(entries);
        for (var index = 0; index < titles.Length; index++) retitle(index + 1, titles[index]);
    }

    // Returns false when the command does not name a choice. Commands start at 1.
    internal static bool Choose(IReadOnlyList<(Func<string>? Title, Action Action)> entries, int command)
    {
        var actions = entries.Where(entry => entry.Title is not null).Select(entry => entry.Action).ToArray();
        if (command < 1 || command > actions.Length) return false;
        actions[command - 1]();
        return true;
    }
}
