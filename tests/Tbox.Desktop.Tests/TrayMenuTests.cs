using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

// The Windows tray menu (APP-002): one card, one row per command, no duplicate quit entry,
// icons and shortcut caps in their own columns, and hairlines that run edge to edge inside the card.
public sealed class TrayMenuTests
{
    [AvaloniaFact]
    public void MenuListsEachCommandOnceWithShortcutCapsAndSeparators()
    {
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        var menu = new TrayMenuWindow(controller, () => true);
        menu.Show(); Layout(menu);
        try
        {
            var entries = menu.Entries();
            Assert.Equal(["打开 Tbox", "区域截图", "剪贴板贴图", "隐藏 / 显示贴图", "退出 Tbox"], entries.Select(entry => entry.Label));
            Assert.Single(entries, entry => entry.Label == "退出 Tbox");
            // The caps mirror the global shortcuts; the row labels themselves stay clean.
            Assert.Equal(["F6", "F7", "F8"], entries.Where(entry => entry.Caps is not null).Select(entry => entry.Caps));
            var rows = Rows(menu);
            Assert.Equal(5, rows.Length);
            Assert.Equal(["打开 Tbox", "区域截图 F6", "剪贴板贴图 F7", "隐藏 / 显示贴图 F8", "退出 Tbox"],
                rows.Select(button => AutomationProperties.GetName(button)));
            // Separators sit between groups only: never before the first row or after the last one.
            var order = menu.RowsPanel.Children.Select(control => control is Grid ? "row" : "sep").ToArray();
            Assert.Equal(["row", "sep", "row", "row", "row", "sep", "row"], order);
            Assert.Equal(2, Separators(menu).Length);
        }
        finally { menu.Close(); }
    }

    [AvaloniaFact]
    public void MenuUsesTheLightCardAndPaintsSeparatorsEdgeToEdge()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        var menu = new TrayMenuWindow(controller, () => true);
        menu.Show(); Layout(menu);
        try
        {
            var card = (Border)menu.Content!;
            Assert.Equal(Color.Parse("#FFFFFF"), ((ISolidColorBrush)card.Background!).Color);
            Assert.Equal(DesignTokens.CardRadius, card.CornerRadius.TopLeft);
            var separators = Separators(menu);
            var left = separators.Min(border => border.TranslatePoint(default, card)!.Value.X);
            var right = separators.Max(border => border.TranslatePoint(default, card)!.Value.X + border.Bounds.Width);
            // Inside the card's own padding on both sides — the ragged native separators are what this replaces.
            Assert.Equal(TrayMenuWindow.CardPadding, left);
            Assert.Equal(card.Bounds.Width - TrayMenuWindow.CardPadding, right);
            var chips = menu.GetVisualDescendants().OfType<Border>().Where(border => border.Width == 24 && border.Child is LineIcon).ToArray();
            Assert.Equal(5, chips.Length);
        }
        finally { menu.Close(); }
    }

    [AvaloniaFact]
    public void MenuShowsWhetherPinsAreVisibleAndFollowsShortcutChanges()
    {
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        var visible = true;
        var menu = new TrayMenuWindow(controller, () => visible);
        menu.Show(); Layout(menu);
        try
        {
            var toggle = Assert.Single(menu.Entries(), entry => entry.Label == "隐藏 / 显示贴图");
            Assert.Equal(IconKind.Eye, toggle.Icon);
            Assert.True(toggle.Accent);
            visible = false;
            menu.Refresh(); Layout(menu);
            toggle = Assert.Single(menu.Entries(), entry => entry.Label == "隐藏 / 显示贴图");
            Assert.Equal(IconKind.EyeOff, toggle.Icon);
            Assert.False(toggle.Accent);

            ShortcutLabels.Bindings = () => new HotkeyBindings(Pin: new Shortcut(Control: true, Alt: true, Shift: false, Meta: false, "F9"));
            try { menu.Refresh(); Assert.Equal("Ctrl+Alt+F9", Assert.Single(menu.Entries(), entry => entry.Label == "剪贴板贴图").Caps); }
            finally { ShortcutLabels.Bindings = () => null; }
        }
        finally { menu.Close(); }
    }

    [AvaloniaFact]
    public void EscapeDismissesTheMenuAndAChosenRowClosesItFirst()
    {
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller);
        var menu = new TrayMenuWindow(controller, () => true);
        menu.Show(); Layout(menu);
        try
        {
            menu.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.False(menu.IsVisible);
        }
        finally { if (menu.IsVisible) menu.Close(); }

        menu = new TrayMenuWindow(controller, () => true);
        menu.Show(); Layout(menu);
        try
        {
            var row = Assert.Single(Rows(menu), button => AutomationProperties.GetName(button) == "区域截图 F6");
            row.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            // The command starts from the already closed menu, so its own windows are not dismissed as the menu's.
            Assert.False(menu.IsVisible);
        }
        finally { if (menu.IsVisible) menu.Close(); controller.Main.Close(); }
    }

    [AvaloniaFact]
    public void MenuStaysInsideTheWorkingAreaAboveTheTray()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        var menu = new TrayMenuWindow(controller, () => true);
        menu.Show(); Layout(menu);
        try
        {
            // A 1920×1032 working area above a 48 px taskbar.
            var working = new Rect(0, 0, 1920, 1032);
            var point = menu.PlaceInside(working);
            Assert.InRange(point.X, working.X, working.Right - menu.Bounds.Width);
            Assert.InRange(point.Y, working.Y, working.Bottom - menu.Bounds.Height);
            Assert.InRange(working.Bottom - (point.Y + menu.Bounds.Height), 0, 12);
            Preview(menu, "tray-menu");
        }
        finally { menu.Close(); }
    }

    private static Button[] Rows(TrayMenuWindow menu) =>
        menu.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).ToArray();

    private static Border[] Separators(TrayMenuWindow menu) =>
        menu.GetVisualDescendants().OfType<Border>().Where(border => border.IsEffectivelyVisible && border.Child is Border { Height: 1 }).ToArray();

    private static void Layout(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

    private static void Preview(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("TBOX_UI_PREVIEWS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var size = window.Bounds.Size;
        using var preview = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)size.Width), Math.Max(1, (int)size.Height)), new Vector(96, 96));
        preview.Render(window); preview.Save(Path.Combine(directory, name + ".png"));
    }
}
