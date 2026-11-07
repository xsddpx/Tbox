using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Tbox.Desktop.Views;

// One set of tokens for both Windows and macOS. Platform code must not restyle pages.
internal static class DesignTokens
{
    public const double PageTitle = 28, SectionTitle = 16, Body = 14, Caption = 12;
    public const double CardRadius = 12, ControlRadius = 8, TaskRadius = 8;
    public const double SidebarWidth = 224, ControlHeight = 36;
    public const string Accent = "#5265DB";
    public const string TaskBackground = "#20242D", TaskBorder = "#4B5363";
    public static ThemeColors Colors(bool dark) => dark
        ? new("#15181E", "#20242D", "#1A1E26", "#303746", "#394252", "#F0F3F8", "#AFB8C8", "#2B3455", "#A5B2FF")
        : new("#F5F7FA", "#FFFFFF", "#EEF1F6", "#E7ECF4", "#DFE5EF", "#202838", "#5E6A7D", "#EDF0FF", Accent);
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
    string Border, string Text, string SecondaryText, string AccentSurface, string Accent);
