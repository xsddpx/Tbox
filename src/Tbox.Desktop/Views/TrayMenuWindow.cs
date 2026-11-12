using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Views;

// The Windows tray menu, drawn as a UI 3.0 card instead of the native menu (APP-002): one surface for every
// entry, icons and shortcut caps in their own columns, and hairlines that never fall inside the card padding.
// One row per command, in the same order the macOS status menu keeps. macOS keeps the system status menu.
internal sealed class TrayMenuWindow : Window
{
    internal const double RowHeight = 34, SeparatorHeight = 9, CardPadding = 6;
    private const double ShadowInset = 12;
    private static readonly IBrush Strong = Brush.Parse("#1A1B22");
    private readonly Func<bool> _pinsVisible;
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly List<Row> _items = [];
    private readonly List<Button> _buttons = [];
    private bool _dismissing;

    // One row per command, in the order the macOS status menu keeps: title, mirrored shortcut,
    // whether it can run now, and what it does. The actions are filled in by the constructor.
    internal sealed record Row(Func<string> Title, HotkeyAction? Shortcut, Func<bool> Enabled, Action Action);
    internal readonly record struct Entry(string Label, string? Caps, IconKind Icon, ToolTone Tone, bool Enabled, bool Accent);
    // The stack holding rows and separators in order; tests read it to check grouping.
    internal StackPanel RowsPanel => _rows;

    internal static readonly Row[] DefaultRows =
    [
        new(() => "打开 Tbox", null, () => true, () => { }),
        new(() => "区域截图", HotkeyAction.Capture, () => true, () => { }),
        new(() => "剪贴板贴图", HotkeyAction.Pin, () => true, () => { }),
        new(() => "隐藏 / 显示贴图", HotkeyAction.Toggle, () => true, () => { }),
        new(() => "退出 Tbox", null, () => true, () => { })
    ];

    internal TrayMenuWindow(DesktopController controller, Func<bool> pinsVisible)
    {
        _pinsVisible = pinsVisible;
        Ui.Configure(this, "Tbox 菜单", 232, 220);
        ShowActivated = false; ShowInTaskbar = false; Topmost = true; CanResize = false;
        SystemDecorations = SystemDecorations.None; SizeToContent = SizeToContent.WidthAndHeight;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Action[] actions =
        [
            controller.ShowMain, () => controller.Run(controller.CaptureAsync), () => controller.Run(controller.PinClipboardAsync),
            controller.TogglePins, controller.Quit
        ];
        for (var index = 0; index < DefaultRows.Length; index++)
        {
            var row = DefaultRows[index] with { Action = actions[index] };
            _items.Add(row);
            if (index is 1 or 4) _rows.Children.Add(Separator());
            _rows.Children.Add(Build(row));
        }
        // A transparent margin leaves room for the card's shadow inside the borderless window.
        Content = new Border
        {
            Margin = new Thickness(ShadowInset), Padding = new Thickness(CardPadding), MinWidth = 208,
            CornerRadius = new CornerRadius(DesignTokens.CardRadius), Background = Brushes.White,
            BoxShadow = BoxShadows.Parse("0 0 0 1 #0F101220, 0 8 24 0 #24101220"),
            Child = _rows
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => Dismiss();
        controller.ShortcutsChanged += Refresh;
        Closed += (_, _) => controller.ShortcutsChanged -= Refresh;
    }

    internal Entry[] Entries() => _items.Select(row =>
    {
        var (icon, tone) = row.Shortcut switch
        {
            HotkeyAction.Capture => (IconKind.Capture, ToolTone.Blue),
            HotkeyAction.Pin => (IconKind.Pin, ToolTone.Amber),
            HotkeyAction.Toggle => (_pinsVisible() ? IconKind.Eye : IconKind.EyeOff, ToolTone.Grey),
            _ => (IconKind.Gear, ToolTone.Grey)
        };
        var accent = row.Shortcut is HotkeyAction.Toggle && _pinsVisible();
        return new Entry(row.Title(), row.Shortcut is { } action ? string.Join("+", ShortcutLabels.Caps(action)) : null,
            icon, tone, row.Enabled(), accent);
    }).ToArray();

    // The menu opens beside the tray icon inside the screen's working area, preferring above it.
    internal Point PlaceInside(Rect working)
    {
        UpdateLayout();
        var x = Math.Clamp(working.Right - Bounds.Width - 4, working.X + 4, Math.Max(working.X + 4, working.Right - Bounds.Width - 4));
        var y = working.Bottom - Bounds.Height - 8;
        if (y < working.Y + 4) y = working.Y + 4;
        return new Point(x, y);
    }

    internal void Dismiss()
    {
        if (_dismissing) return;
        _dismissing = true;
        Close();
    }

    internal void Refresh()
    {
        var entries = Entries();
        for (var index = 0; index < entries.Length; index++) Paint(_buttons[index], entries[index]);
    }

    private Control Build(Row row)
    {
        var button = new Button
        {
            Height = RowHeight, MinHeight = RowHeight, Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(DesignTokens.ControlRadius),
            BorderThickness = default, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse("#F2F1F7");
        button.Resources["ButtonBackgroundPressed"] = Brush.Parse("#E9E8F0");
        button.Resources["ButtonBorderBrushPointerOver"] = Brushes.Transparent;
        button.Resources["ButtonBorderBrushPressed"] = Brushes.Transparent;
        button.Click += (_, _) => Choose(row);
        var content = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10, Children =
        {
            new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center }
        } };
        Grid.SetColumn(content.Children[1], 1); Grid.SetColumn(content.Children[2], 2);
        button.Content = content;
        _buttons.Add(button);
        Paint(button, Entries()[_buttons.Count - 1]);
        return new Grid { Children = { button } };
    }

    private void Paint(Button button, Entry entry)
    {
        var content = (Grid)button.Content!;
        var chip = (Border)content.Children[0];
        chip.Background = Chip(entry.Tone);
        chip.Child = new LineIcon(entry.Icon, brush: Ink(entry)) { Width = 15, Height = 15,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var label = (TextBlock)content.Children[1];
        label.Text = entry.Label;
        label.Foreground = entry.Accent ? Brush.Parse(DesignTokens.Accent) : Strong;
        label.FontWeight = entry.Accent ? FontWeight.SemiBold : FontWeight.Normal;
        var caps = (StackPanel)content.Children[2];
        caps.Children.Clear();
        if (entry.Caps is { Length: > 0 }) caps.Children.Add(Ui.Key(entry.Caps));
        button.IsEnabled = entry.Enabled;
        button.Opacity = entry.Enabled ? 1 : .45;
        AutomationProperties.SetName(button, entry.Caps is { } keys ? $"{entry.Label} {keys}" : entry.Label);
    }

    private static IBrush Ink(Entry entry) => Brush.Parse(entry.Accent ? DesignTokens.Accent : entry.Tone switch
    {
        ToolTone.Amber => "#B7791F", ToolTone.Blue => "#2F6BDB", ToolTone.Red => "#D9363E", ToolTone.Green => "#1C8A58", _ => "#5F6270"
    });

    private static IBrush Chip(ToolTone tone) => Brush.Parse(tone switch
    {
        ToolTone.Amber => "#FFF3DA", ToolTone.Blue => "#E7F0FF", ToolTone.Red => "#FFE9EA", ToolTone.Green => "#E2F5EC",
        ToolTone.Grey => "#F0EFF5", _ => "#F2EDFF"
    });

    // Inside the card's padding on both sides; the native menu's separators never lined up like this.
    private static Border Separator() => new()
    {
        Height = SeparatorHeight, Child = new Border
        {
            Height = 1, Background = Brush.Parse(DesignTokens.Colors(dark: false).Border), VerticalAlignment = VerticalAlignment.Center
        }
    };

    private void Choose(Row row)
    {
        if (!row.Enabled()) return;
        Dismiss();
        row.Action();
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { args.Handled = true; Dismiss(); return; }
        if (args.Key is not (Key.Up or Key.Down)) return;
        var buttons = _buttons.Where(button => button.IsEnabled).ToArray();
        if (buttons.Length == 0) return;
        args.Handled = true;
        var current = Array.FindIndex(buttons, button => button.IsFocused);
        var next = current < 0 ? (args.Key == Key.Down ? 0 : buttons.Length - 1)
            : (current + (args.Key == Key.Down ? 1 : -1) + buttons.Length) % buttons.Length;
        buttons[next].Focus(NavigationMethod.Directional);
    }
}
