using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Tbox.App;

internal readonly record struct TboxUiPalette(
    Color Background,
    Color SurfacePrimary,
    Color SurfaceSecondary,
    Color SurfaceHover,
    Color BorderDefault,
    Color BorderSubtle,
    Color TextPrimary,
    Color TextSecondary,
    Color TextDisabled,
    bool IsDark);

internal static class TboxUi
{
    internal const double PageTitleFontSize = 28;
    internal const double SectionTitleFontSize = 16;
    internal const double BodyFontSize = 14;
    internal const double CaptionFontSize = 12;
    internal const double StandardControlHeight = 32;
    internal const double TaskButtonWidth = 30;
    internal const double TaskButtonHeight = 32;
    internal const double TaskIconBoxSize = 20;
    internal const double CardCornerRadius = 8;
    internal const double ControlCornerRadius = 5;
    internal const double TaskCornerRadius = 8;

    internal static readonly Color ScreenshotModeColor = Color.FromArgb(255, 0, 120, 212);
    internal static readonly Color LongCaptureModeColor = Color.FromArgb(255, 255, 204, 0);
    internal static readonly Color RecordingModeColor = Color.FromArgb(255, 255, 59, 48);
    internal static readonly Color SuccessColor = Color.FromArgb(255, 52, 199, 89);
    internal static readonly Color TaskBackgroundColor = Color.FromArgb(248, 32, 32, 32);
    internal static readonly Color TaskBorderColor = Color.FromArgb(255, 112, 112, 112);
    internal static readonly Color TaskHoverColor = Color.FromArgb(255, 58, 58, 58);
    internal static readonly Color TaskPressedColor = Color.FromArgb(255, 76, 76, 76);
    internal static readonly Color TaskDividerColor = Color.FromArgb(255, 90, 90, 90);
    internal static readonly Color TaskDisabledColor = Color.FromArgb(255, 105, 105, 105);

    private static string _themePreference = "system";

    internal static void SetThemePreference(string theme)
    {
        _themePreference = theme.ToLowerInvariant() switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => "system"
        };
    }

    internal static ElementTheme RequestedTheme => _themePreference switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    internal static bool IsDark => _themePreference switch
    {
        "light" => false,
        "dark" => true,
        _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
    };

    internal static TboxUiPalette Palette => IsDark
        ? new TboxUiPalette(
            Color.FromArgb(255, 32, 32, 32),
            Color.FromArgb(255, 41, 41, 41),
            Color.FromArgb(255, 50, 50, 50),
            Color.FromArgb(255, 58, 58, 58),
            Color.FromArgb(255, 65, 65, 65),
            Color.FromArgb(255, 54, 54, 54),
            Color.FromArgb(255, 245, 245, 245),
            Color.FromArgb(255, 194, 194, 194),
            Color.FromArgb(255, 119, 119, 119),
            true)
        : new TboxUiPalette(
            Color.FromArgb(255, 244, 241, 238),
            Color.FromArgb(255, 255, 254, 252),
            Color.FromArgb(255, 247, 244, 241),
            Color.FromArgb(255, 234, 231, 228),
            Color.FromArgb(255, 222, 217, 212),
            Color.FromArgb(255, 233, 228, 223),
            Color.FromArgb(255, 32, 32, 32),
            Color.FromArgb(255, 98, 94, 90),
            Color.FromArgb(255, 154, 150, 146),
            false);

    internal static SolidColorBrush Brush(Color color) => new(color);

    internal static Color AccentColor
    {
        get
        {
            try
            {
                if (Application.Current.Resources["SystemAccentColor"] is Color color) return color;
            }
            catch
            {
            }
            return ScreenshotModeColor;
        }
    }

    internal static Border CreateTaskSurface(UIElement? child = null, Visibility visibility = Visibility.Visible) => new()
    {
        RequestedTheme = ElementTheme.Dark,
        Background = Brush(TaskBackgroundColor),
        BorderBrush = Brush(TaskBorderColor),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(TaskCornerRadius),
        Padding = new Thickness(4),
        Visibility = visibility,
        Child = child
    };

    internal static void ApplyTaskButtonVisual(Button button, bool selected)
    {
        var accent = AccentColor;
        var selectedHover = Lighten(accent, 20);
        var selectedPressed = Darken(accent, 24);
        var selectedBorder = Lighten(accent, 72);
        button.Background = Brush(selected ? accent : Colors.Transparent);
        button.BorderBrush = Brush(Colors.Transparent);
        button.Resources["ButtonBackgroundPointerOver"] = Brush(selected ? selectedHover : TaskHoverColor);
        button.Resources["ButtonBackgroundPressed"] = Brush(selected ? selectedPressed : TaskPressedColor);
        button.Resources["ButtonBorderBrushPointerOver"] = Brush(selected ? selectedBorder : TaskDisabledColor);
        button.Resources["ButtonBorderBrushPressed"] = Brush(selected ? selectedBorder : TaskDisabledColor);
        button.Resources["ButtonBackgroundDisabled"] = Brush(Colors.Transparent);
        button.Resources["ButtonBorderBrushDisabled"] = Brush(Colors.Transparent);
        button.Resources["ButtonForegroundDisabled"] = Brush(TaskDisabledColor);
    }

    internal static void ApplyTaskToggleVisual(ToggleButton button, bool selected)
    {
        var accent = AccentColor;
        button.RequestedTheme = ElementTheme.Dark;
        button.Foreground = Brush(Colors.White);
        button.Background = Brush(selected ? accent : Colors.Transparent);
        button.BorderBrush = Brush(Colors.Transparent);
        button.Resources["ToggleButtonBackgroundPointerOver"] = Brush(selected ? Lighten(accent, 20) : TaskHoverColor);
        button.Resources["ToggleButtonBackgroundPressed"] = Brush(selected ? Darken(accent, 24) : TaskPressedColor);
        button.Resources["ToggleButtonBackgroundChecked"] = Brush(accent);
        button.Resources["ToggleButtonBackgroundCheckedPointerOver"] = Brush(Lighten(accent, 20));
        button.Resources["ToggleButtonBackgroundCheckedPressed"] = Brush(Darken(accent, 24));
        button.Resources["ToggleButtonBorderBrushPointerOver"] = Brush(TaskDisabledColor);
        button.Resources["ToggleButtonBorderBrushChecked"] = Brush(Colors.Transparent);
        button.Resources["ToggleButtonForegroundDisabled"] = Brush(TaskDisabledColor);
    }

    internal static void StyleStandardButton(Button button, string toolTip, bool primary = false)
    {
        button.RequestedTheme = RequestedTheme;
        button.MinHeight = StandardControlHeight;
        button.FontSize = BodyFontSize;
        button.CornerRadius = new CornerRadius(ControlCornerRadius);
        if (primary)
        {
            button.Background = Brush(AccentColor);
            button.Foreground = Brush(Colors.White);
        }
        ToolTipService.SetToolTip(button, toolTip);
    }

    internal static TextBlock CreateText(string text, double fontSize = BodyFontSize, bool secondary = false) => new()
    {
        Text = text,
        FontSize = fontSize,
        Foreground = Brush(secondary ? Palette.TextSecondary : Palette.TextPrimary),
        VerticalAlignment = VerticalAlignment.Center
    };

    internal static void ConfigureTitleBar(AppWindow appWindow, bool? dark = null)
    {
        if (appWindow.TitleBar is not { } titleBar) return;
        var useDark = dark ?? IsDark;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = useDark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = useDark ? Colors.White : Colors.Black;
    }

    private static Color Lighten(Color color, byte amount) => Color.FromArgb(
        color.A,
        (byte)Math.Min(255, color.R + amount),
        (byte)Math.Min(255, color.G + amount),
        (byte)Math.Min(255, color.B + amount));

    private static Color Darken(Color color, byte amount) => Color.FromArgb(
        color.A,
        (byte)Math.Max(0, color.R - amount),
        (byte)Math.Max(0, color.G - amount),
        (byte)Math.Max(0, color.B - amount));
}
