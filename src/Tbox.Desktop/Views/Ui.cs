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
    public static readonly IBrush ToolbarBackground = Brush.Parse("#252525");
    public static TextBlock Text(string text, double size = 14) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap
    };
    public static Button Button(string text, Action action, string? tooltip = null)
    {
        var button = new Button { Content = text, MinHeight = 32, Padding = new Thickness(12, 6), VerticalAlignment = VerticalAlignment.Center };
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
            Child = content, Padding = new Thickness(16), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1)
        };
        void Update()
        {
            var dark = card.ActualThemeVariant == ThemeVariant.Dark;
            card.Background = Brush.Parse(dark ? "#292929" : "#FFFEFC");
            card.BorderBrush = Brush.Parse(dark ? "#414141" : "#DED9D4");
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
    private static void ApplyBackground(Window window) => window.Background =
        Brush.Parse(window.ActualThemeVariant == ThemeVariant.Dark ? "#202020" : "#F4F1EE");
}
