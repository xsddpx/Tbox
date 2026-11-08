using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Tbox.Desktop.Views;

// One set of tokens for both Windows and macOS. Platform code must not restyle pages.
internal static class DesignTokens
{
    public const double PageTitle = 28, SectionTitle = 16, Body = 14, Caption = 12;
    public const double CardRadius = 12, ControlRadius = 8, TaskRadius = 8, PanelRadius = 14;
    public const double ControlHeight = 36;
    // Amethyst from the approved app icon; the dark variant is lightened to keep AA contrast.
    public const string Accent = "#6D4AE8";
    public const string TaskBackground = "#20242D", TaskBorder = "#4B5363";
    public const string Recording = "#E5484D", Ready = "#1F9D61";
    public static ThemeColors Colors(bool dark) => dark
        ? new("#131318", "#1D1D24", "#18181E", "#2A2A33", "#30303A", "#F2F2F6", "#A6A9B6", "#2C2645", "#B4A0FF", "#F2B65A")
        : new("#F6F6F9", "#FFFFFF", "#F1F1F6", "#EBEBF2", "#E3E3EC", "#17181F", "#5E6270", "#F0ECFF", Accent, "#9A6A12");
    public static FluentTheme CreateTheme()
    {
        var theme = new FluentTheme();
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var c = Colors(variant == ThemeVariant.Dark);
            theme.Palettes.Add(variant, new ColorPaletteResources
            {
                Accent = Color.Parse(c.Accent), RegionColor = Color.Parse(c.Background),
                BaseHigh = Color.Parse(c.Text), BaseMedium = Color.Parse(c.SecondaryText),
                ChromeLow = Color.Parse(c.Surface), ChromeMediumLow = Color.Parse(c.SecondarySurface),
                ChromeHigh = Color.Parse(c.Hover), ChromeDisabledLow = Color.Parse(c.Border)
            });
        }
        return theme;
    }
}

internal sealed record ThemeColors(string Background, string Surface, string SecondarySurface, string Hover,
    string Border, string Text, string SecondaryText, string AccentSurface, string Accent, string Warning);
