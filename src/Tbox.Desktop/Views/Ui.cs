using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Tbox.Desktop.Views;

internal static class Ui
{
    public static readonly IBrush Blue = Brush.Parse("#0078D4");
    public static readonly IBrush ToolbarBackground = Brush.Parse(DesignTokens.TaskBackground);
    public static TextBlock Text(string text, double size = 14) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap
    };
    public static Button Button(string text, Action action, string? tooltip = null)
    {
        var button = new Button { Content = text, MinHeight = DesignTokens.ControlHeight, Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(DesignTokens.ControlRadius), VerticalAlignment = VerticalAlignment.Center };
        if (tooltip is not null) ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip ?? text);
        button.Click += (_, _) => action();
        return button;
    }
    public static StackPanel Row(params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }
    public static Border Card(Control content)
    {
        var card = new Border
        {
            Child = content, Padding = new Thickness(24), CornerRadius = new CornerRadius(DesignTokens.CardRadius),
            BorderThickness = new Thickness(1)
        };
        void Update()
        {
            var colors = DesignTokens.Colors(card.ActualThemeVariant == ThemeVariant.Dark);
            card.Background = Brush.Parse(colors.Surface);
            card.BorderBrush = Brush.Parse(colors.Border);
        }
        card.ActualThemeVariantChanged += (_, _) => Update(); Update();
        return card;
    }
    public static void Configure(Window window, string title, int width, int height)
    {
        window.Title = title;
        window.Width = width;
        window.Height = height;
        window.FontSize = 14;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ActualThemeVariantChanged += (_, _) => ApplyBackground(window);
        ApplyBackground(window);
    }
    private static void ApplyBackground(Window window)
    {
        var colors = DesignTokens.Colors(window.ActualThemeVariant == ThemeVariant.Dark);
        window.Background = Brush.Parse(colors.Background); window.Foreground = Brush.Parse(colors.Text);
    }

    public static TextBlock Caption(string text)
    {
        var block = Text(text, DesignTokens.Caption);
        void Update() => block.Foreground = Brush.Parse(DesignTokens.Colors(block.ActualThemeVariant == ThemeVariant.Dark).SecondaryText);
        block.ActualThemeVariantChanged += (_, _) => Update(); Update(); return block;
    }
}
