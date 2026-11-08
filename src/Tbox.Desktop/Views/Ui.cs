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
        var button = new Button { Content = text, MinHeight = DesignTokens.ControlHeight, Padding = new Thickness(14, 7),
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

    // Keyboard shortcut cap, the same chip the tray panel shows beside tools.
    public static Border Key(string text) => Themed(new Border
    {
        CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11 }
    }, (b, c) => { b.Background = Brush.Parse(c.Hover); ((TextBlock)b.Child!).Foreground = Brush.Parse(c.SecondaryText); });

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
