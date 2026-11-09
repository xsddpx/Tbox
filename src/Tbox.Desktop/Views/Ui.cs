using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Tbox.Desktop.Views;

// Shared building blocks so the tray panel, settings, OCR, update and task windows read as one product.
internal static class Ui
{
    public static readonly IBrush Blue = Brush.Parse("#0078D4");
    public static readonly IBrush LongYellow = Brush.Parse("#F8C53A");
    public static readonly IBrush RecordRed = Brush.Parse(DesignTokens.Recording);
    // Capture blue, long-capture yellow and recording red identify the selection mode only.
    public static IBrush ModeBrush(CaptureAction? preset) => preset switch
    {
        CaptureAction.Record => RecordRed, CaptureAction.Long => LongYellow, _ => Blue
    };
    public static readonly IBrush ToolbarBackground = Brush.Parse(DesignTokens.TaskBackground);
    public static readonly IBrush ToolbarBorder = Brush.Parse(DesignTokens.TaskBorder);
    public static readonly IBrush ToolbarText = Brush.Parse(DesignTokens.TaskText);

    public static TextBlock Text(string text, double size = 14) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap
    };
    public static Button Button(string text, Action action, string? tooltip = null)
    {
        var button = new Button { Content = text, MinHeight = DesignTokens.ControlHeight, Padding = new Thickness(14, 5), FontSize = 13,
            CornerRadius = new CornerRadius(DesignTokens.ControlRadius), VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center };
        if (tooltip is not null) ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip ?? text);
        button.Click += (_, _) => action();
        return button;
    }
    public static Button Primary(string text, Action action, string? tooltip = null)
    {
        var button = Button(text, action, tooltip);
        button.Classes.Add("accent"); button.FontWeight = FontWeight.SemiBold;
        return button;
    }
    // Borderless text button used for secondary links such as "打开文件夹" or "退出".
    public static Button Link(string text, Action action, string? tooltip = null, bool accent = true)
    {
        var button = Button(text, action, tooltip);
        button.Classes.Add("link"); button.Padding = new Thickness(6, 2); button.MinHeight = 0;
        return Themed(button, (b, c) =>
        {
            b.Foreground = Brush.Parse(accent ? c.Accent : c.SecondaryText); b.Background = Brushes.Transparent; b.BorderThickness = default;
            b.Resources["ButtonBackgroundPointerOver"] = Brush.Parse(c.Hover);
            b.Resources["ButtonForegroundPointerOver"] = b.Foreground;
            b.Resources["ButtonBackgroundPressed"] = Brush.Parse(c.Border);
            b.Resources["ButtonForegroundPressed"] = b.Foreground;
        });
    }
    // Square icon-only button on a page surface, e.g. the tray panel's gear.
    public static Button IconButton(IconKind icon, string label, Action action)
    {
        var button = Button(label, action, label);
        button.Content = new LineIcon(icon);
        button.Padding = new Thickness(6); button.MinHeight = 32; button.MinWidth = 32;
        return Themed(button, (b, c) =>
        {
            b.Background = Brushes.Transparent; b.BorderThickness = default;
            b.Resources["ButtonBackgroundPointerOver"] = Brush.Parse(c.Hover);
            b.Resources["ButtonBackgroundPressed"] = Brush.Parse(c.Border);
        });
    }

    // Icon button for the dark task surfaces: capture, pin, long capture and recording toolbars.
    public static Button ToolbarButton(IconKind icon, string tooltip, Action action, IBrush? brush = null)
    {
        var button = TaskButton(tooltip, action);
        button.Content = new LineIcon(icon, brush: brush ?? ToolbarText) { Width = 18, Height = 18 };
        return button;
    }
    public static Button ToolbarButton(string glyph, string tooltip, Action action)
    {
        var button = TaskButton(tooltip, action);
        button.Content = glyph; button.FontSize = 15; button.FontWeight = FontWeight.SemiBold; button.Foreground = ToolbarText;
        return button;
    }
    private static Button TaskButton(string tooltip, Action action)
    {
        var button = Button(tooltip, action, tooltip);
        button.Width = 30; button.Height = 32; button.MinWidth = 30; button.MinHeight = 32;
        button.Padding = new Thickness(0); button.CornerRadius = new CornerRadius(7);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.Background = Brushes.Transparent; button.BorderThickness = default; button.Foreground = ToolbarText;
        button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse(DesignTokens.TaskHover);
        button.Resources["ButtonBackgroundPressed"] = Brush.Parse(DesignTokens.TaskBorder);
        button.Resources["ButtonForegroundPointerOver"] = ToolbarText;
        button.Resources["ButtonForegroundPressed"] = ToolbarText;
        button.Resources["ButtonBackgroundDisabled"] = Brushes.Transparent;
        return button;
    }
    // Dark rounded surface shared by every task toolbar.
    public static Border ToolbarSurface(Control child, double padding = 4) => new()
    {
        Child = child, Background = ToolbarBackground, BorderBrush = ToolbarBorder, BorderThickness = new Thickness(1),
        Padding = new Thickness(padding), CornerRadius = new CornerRadius(DesignTokens.TaskRadius)
    };
    public static Border ToolbarDivider() => new()
    {
        Width = 1, Height = 20, Margin = new Thickness(4, 0), Background = ToolbarBorder, VerticalAlignment = VerticalAlignment.Center
    };

    public static StackPanel Row(params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var item in items) { item.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(item); }
        return row;
    }
    public static Border Card(Control content)
    {
        var card = new Border
        {
            Child = content, Padding = new Thickness(24), CornerRadius = new CornerRadius(DesignTokens.CardRadius),
            BorderThickness = new Thickness(1)
        };
        return Themed(card, (b, c) => { b.Background = Brush.Parse(c.Surface); b.BorderBrush = Brush.Parse(c.Border); });
    }

    // Window header used by settings, OCR and update: brand mark, title, optional subtitle and trailing actions.
    public static Grid Header(string title, string? subtitle, Control? trailing, out Bitmap brand)
    {
        using (var asset = AssetLoader.Open(new Uri("avares://Tbox.Desktop/Assets/StoreLogo.png"))) brand = new Bitmap(asset);
        var name = Text(title, DesignTokens.SectionTitle); name.FontWeight = FontWeight.SemiBold;
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name } };
        if (subtitle is not null) titles.Children.Add(Caption(subtitle));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        header.Children.Add(new Image { Source = brand, Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        Grid.SetColumn(titles, 1); header.Children.Add(titles);
        if (trailing is not null) { trailing.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(trailing, 2); header.Children.Add(trailing); }
        return header;
    }

    // Small semibold label above a group card, matching "最近截图" on the tray panel.
    public static TextBlock SectionLabel(string text)
    {
        var label = Caption(text); label.FontWeight = FontWeight.SemiBold; label.Margin = new Thickness(4, 6, 0, 0);
        return label;
    }

    // A card holding rows separated by hairlines, like a native settings group.
    public static Border Group(params Control[] rows)
    {
        var panel = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) panel.Children.Add(Themed(new Border { Height = 1 }, (b, c) => b.Background = Brush.Parse(c.Border)));
            panel.Children.Add(rows[i]);
        }
        var card = Card(panel); card.Padding = default; card.ClipToBounds = true;
        return card;
    }

    // Keyboard shortcut cap: 20 px tall, one cap per key. On a filled accent button it turns translucent white.
    public static Border Key(string text, bool onAccent = false) => Themed(new Border
    {
        CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 0), Height = 20, MinWidth = 20, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center }
    }, (b, c) =>
    {
        b.Background = onAccent ? new SolidColorBrush(Colors.White, .22) : new SolidColorBrush(Color.Parse("#101220"), .06);
        ((TextBlock)b.Child!).Foreground = onAccent ? Brushes.White : Brush.Parse(c.SecondaryText);
    });

    // Page building blocks from spec ③: grey group title, 46 px rows with hairlines, dot statuses, notes and the warning bar.
    public static TextBlock GroupTitle(string text) => new()
    {
        Text = text, FontSize = DesignTokens.Caption, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse(DesignTokens.Hint),
        VerticalAlignment = VerticalAlignment.Center
    };
    public static Grid GroupHeader(string text, Control? trailing = null)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 14, 0, 2), MinHeight = 22 };
        header.Children.Add(GroupTitle(text));
        if (trailing is not null) { trailing.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(trailing, 1); header.Children.Add(trailing); }
        return header;
    }
    public static StackPanel Rows(params Control[] rows)
    {
        var panel = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) panel.Children.Add(new Border { Height = 1, Background = Brush.Parse(DesignTokens.Hairline) });
            panel.Children.Add(rows[i]);
        }
        return panel;
    }
    public static TextBlock Description(string text) => new()
    {
        Text = text, FontSize = DesignTokens.Description, LineHeight = 18, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#5F6270")
    };
    // Coloured dot plus a word: 可用, 冲突, 未授权.
    public static StackPanel Status(string text, string color)
    {
        var brush = Brush.Parse(color);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = brush, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = text, FontSize = DesignTokens.Caption, Foreground = brush, VerticalAlignment = VerticalAlignment.Center }
        } };
    }
    public static Border Note(string text) => new()
    {
        CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12), Background = Brush.Parse("#F7F6FB"),
        Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Children =
        {
            new LineIcon(IconKind.Info, brush: Brush.Parse(DesignTokens.Hint)) { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) },
            Column(Description(text), 1)
        } }
    };
    // Yellow attention bar above a page's hero; its button becomes the page's only primary action.
    public static Border WarningBar(IconKind icon, string title, string detail, Button action)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#1A1B22") },
            new TextBlock { Text = detail, FontSize = DesignTokens.Caption, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#6B5A3A") }
        } };
        action.MinHeight = 30; action.Height = 30; action.Padding = new Thickness(12, 0);
        return new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Background = Brush.Parse("#FFF6E6"),
            BorderBrush = Brush.Parse("#F6E2B8"), BorderThickness = new Thickness(1),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Children =
            {
                new LineIcon(icon, brush: Brush.Parse(DesignTokens.Attention)) { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center },
                Column(text, 1), Column(action, 2)
            } }
        };
    }
    // Soft square behind a tool icon. Each tool keeps one colour everywhere.
    public static Border IconChip(IconKind icon, ToolTone tone, double size = 28, double glyph = 16)
    {
        var (background, foreground) = tone switch
        {
            ToolTone.Amber => ("#FFF3DA", "#B7791F"), ToolTone.Blue => ("#E7F0FF", "#2F6BDB"),
            ToolTone.Red => ("#FFE9EA", "#D9363E"), ToolTone.Green => ("#E2F5EC", "#1C8A58"), ToolTone.Grey => ("#F0EFF5", "#5F6270"),
            _ => ("#F2EDFF", DesignTokens.Accent)
        };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size >= 40 ? 12 : 8),
            Background = tone == ToolTone.Violet
                ? new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#F2EDFF"), 0), new GradientStop(Color.Parse("#E3D9FF"), 1) } }
                : Brush.Parse(background),
            Child = new LineIcon(icon, brush: Brush.Parse(foreground)) { Width = glyph, Height = glyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
    }
    public static T Column<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }

    // Result windows (spec ④) share the main window's structure: tinted window → header → white sheet → bottom bar.
    public static Grid ResultHeader(IconKind icon, ToolTone tone, string title, TextBlock subtitle, Control? trailing = null)
    {
        subtitle.FontSize = DesignTokens.Caption; subtitle.LineHeight = 16;
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = title, FontSize = DesignTokens.SectionTitle, LineHeight = 22, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#1A1B22") },
            subtitle
        } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Margin = new Thickness(20, 4, 20, 14),
            Children = { IconChip(icon, tone, 40, 20), Column(titles, 1) } };
        if (trailing is not null) { trailing.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(Column(trailing, 2)); }
        return header;
    }
    public static Border Sheet(Control content) => new()
    {
        Child = content, Background = Brushes.White, CornerRadius = new CornerRadius(DesignTokens.PanelRadius), Margin = new Thickness(12, 0, 12, 0),
        ClipToBounds = true, BoxShadow = BoxShadows.Parse("0 0 0 1 #0D101220, 0 10 30 0 #0F101220")
    };
    // Bottom bar: status on the left, buttons on the right with the primary one last.
    public static Grid ResultBar(Control status, params Control[] actions)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var action in actions) { action.VerticalAlignment = VerticalAlignment.Center; buttons.Children.Add(action); }
        status.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Margin = new Thickness(20, 12, 20, 14),
            Children = { status, Column(buttons, 1) } };
    }
    // The window's own top strip: 16 px mark and title, sitting under the system caption buttons.
    public static Grid TitleStrip(string title, Bitmap brand) => new()
    {
        Height = 34, IsHitTestVisible = false, Children =
        {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(OperatingSystem.IsMacOS() ? 78 : 14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, Children =
                {
                    new Image { Source = brand, Width = 16, Height = 16 },
                    new TextBlock { Text = title, FontSize = 12, Foreground = Brush.Parse("#3B3D49"), VerticalAlignment = VerticalAlignment.Center }
                } }
        }
    };
    public static Bitmap Brand()
    {
        using var asset = AssetLoader.Open(new Uri("avares://Tbox.Desktop/Assets/StoreLogo.png"));
        return new Bitmap(asset);
    }
    // Extends a result window under the title bar so the strip, header and sheet sit on one tinted surface.
    public static void ExtendIntoTitleBar(Window window)
    {
        window.ExtendClientAreaToDecorationsHint = true; window.ExtendClientAreaTitleBarHeightHint = 34;
    }

    // 2–4 mutually exclusive choices (spec ②). The selected segment is a raised white chip.
    public static Border Segmented(IReadOnlyList<string> options, int selected, Action<int> changed, out Func<int> current, out Action<bool> enable)
    {
        var buttons = new List<Button>();
        var chips = new List<Border>();
        var index = selected;
        void Paint()
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                var on = i == index;
                chips[i].Background = on ? Brushes.White : Brushes.Transparent;
                chips[i].BoxShadow = on ? BoxShadows.Parse("0 1 2 0 #1A101220, 0 0 0 1 #0A101220") : default;
                buttons[i].FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
                buttons[i].Foreground = Brush.Parse(on ? "#1A1B22" : "#5F6270");
                buttons[i].Resources["ButtonBackgroundPointerOver"] = on ? Brushes.Transparent : new SolidColorBrush(Colors.White, .6);
            }
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        for (var i = 0; i < options.Count; i++)
        {
            var position = i;
            var button = new Button { Content = options[i], Height = 26, MinHeight = 26, Padding = new Thickness(12, 0), FontSize = 13,
                CornerRadius = new CornerRadius(7), BorderThickness = default, Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(button, options[i]);
            button.Resources["ButtonBackgroundPressed"] = Brushes.Transparent;
            foreach (var state in new[] { "PointerOver", "Pressed" }) button.Resources["ButtonForeground" + state] = Brush.Parse("#1A1B22");
            button.Click += (_, _) => { if (index == position) return; index = position; Paint(); changed(position); };
            var chip = new Border { CornerRadius = new CornerRadius(7), Child = button };
            buttons.Add(button); chips.Add(chip); row.Children.Add(chip);
        }
        Paint();
        current = () => index;
        var surface = new Border { Padding = new Thickness(3), CornerRadius = new CornerRadius(9), Background = Brush.Parse("#F0EFF5"), Child = row,
            VerticalAlignment = VerticalAlignment.Center };
        enable = on => { surface.IsEnabled = on; surface.Opacity = on ? 1 : .5; };
        return surface;
    }

    // Soft violet banner holding a page's main entry (spec ③).
    public static Border Hero(Control content, double minHeight = 112) => new()
    {
        MinHeight = minHeight, CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 18), ClipToBounds = true,
        BorderBrush = Brush.Parse("#ECE6FD"), BorderThickness = new Thickness(1), Child = content,
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, .45, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#F2EEFF"), 0), new GradientStop(Color.Parse("#F8F6FF"), .55), new GradientStop(Color.Parse("#FDFDFF"), 1) }
        }
    };

    public static void Configure(Window window, string title, int width, int height)
    {
        window.Title = title;
        window.Width = width;
        window.Height = height;
        window.FontSize = DesignTokens.Body;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Themed(window, (w, c) => { w.Background = Brush.Parse(c.Background); w.Foreground = Brush.Parse(c.Text); });
    }

    public static TextBlock Caption(string text) =>
        Themed(Text(text, DesignTokens.Caption), (b, c) => b.Foreground = Brush.Parse(c.SecondaryText));

    // Applies theme colors now and again whenever the control's effective theme changes.
    public static T Themed<T>(T control, Action<T, ThemeColors> apply) where T : Control
    {
        void Update() => apply(control, DesignTokens.Colors(control.ActualThemeVariant == ThemeVariant.Dark));
        control.ActualThemeVariantChanged += (_, _) => Update(); Update(); return control;
    }
}

// A button drawn as a surface (sidebar item, feature card, thumbnail) rather than as a standard push button.
internal sealed class SurfaceButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    public SurfaceButton(string name, Action? action = null)
    {
        AutomationProperties.SetName(this, name); ToolTip.SetTip(this, name);
        Padding = default; BorderThickness = default; Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Stretch; HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        if (action is not null) Click += (_, e) => { if (ReferenceEquals(e.Source, this)) action(); };
    }
}

// A settings row: optional icon, title with an optional description, and a trailing control.
internal sealed class SettingsRow : Grid
{
    private readonly TextBlock _description;
    public SettingsRow(string title, Control? input, string? description = null, IconKind? icon = null)
    {
        MinHeight = 40; ColumnSpacing = 12; Margin = new Thickness(16, 8);
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"); RowDefinitions = new RowDefinitions("Auto,Auto");
        if (icon is { } kind)
        {
            var glyph = new LineIcon(kind) { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRowSpan(glyph, 2); Children.Add(glyph);
        }
        else ColumnDefinitions[0].Width = new GridLength(0);
        _description = Ui.Caption(description ?? "");
        _description.IsVisible = description is not null;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { Ui.Text(title), _description } };
        Grid.SetColumn(text, 1); Grid.SetRowSpan(text, 2); Children.Add(text);
        if (input is null) return;
        input.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(input, 2); Grid.SetRowSpan(input, 2); Children.Add(input);
        SizeChanged += (_, e) =>
        {
            // Narrow windows move the control under the title instead of squeezing the text.
            var stacked = e.NewSize.Width < 300;
            Grid.SetColumn(input, stacked ? 1 : 2); Grid.SetRow(input, stacked ? 1 : 0); Grid.SetRowSpan(input, stacked ? 1 : 2);
            Grid.SetRowSpan(text, stacked ? 1 : 2);
            input.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            input.Margin = stacked ? new Thickness(0, 6, 0, 0) : default;
        };
    }
    public string? Description
    {
        get => _description.Text;
        set { _description.Text = value; _description.IsVisible = !string.IsNullOrWhiteSpace(value); }
    }
}

// Tool colours from spec ①: pin amber, long capture blue, recording red, OCR green, page headers violet.
internal enum ToolTone { Violet, Amber, Blue, Red, Green, Grey }
