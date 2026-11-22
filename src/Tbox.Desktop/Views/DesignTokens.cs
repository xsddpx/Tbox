using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Tbox.Desktop.Views;

// One set of tokens for both Windows and macOS. Platform code must not restyle pages.
internal static class DesignTokens
{
    public const double PageTitle = 23, SectionTitle = 16, Body = 14, Description = 12.5, Caption = 12;
    public const double CardRadius = 12, ControlRadius = 8, TaskRadius = 10, PanelRadius = 14;
    public const double ControlHeight = 32;
    // Neutral action surfaces keep state colours reserved for capture and recording.
    public const string Accent = "#191E26";
    // Task surfaces (capture toolbars, recording controls, playback) stay dark over any content.
    public const string TaskBackground = "#22272F", TaskBorder = "#414852", TaskHover = "#343B45", TaskText = "#F4F6F8";
    public const string TaskSelected = "#E3E8EE";
    public const string Recording = "#E5484D", Ready = "#1F9D61";
    // Selection colour tells the mode (spec ⑤): capture blue, long-capture yellow, recording red.
    public const string CaptureBlue = "#3D7BFF", LongYellow = "#E8A13A", TaskSecondaryText = "#A6A9B6";
    // Light-only page colors from spec ①: group titles, row hairlines, statuses and the yellow attention state.
    public const string Hint = "#626C78", Hairline = "#F1F0F5", Error = "#D9363E", Attention = "#B7791F", AttentionDot = "#E8A13A";
    public static ThemeColors Colors(bool dark) => dark
        ? new("#131318", "#22272F", "#18181E", "#2A2A33", "#30303A", "#F4F6F8", "#A6A9B6", "#343B45", "#E2E7EE", "#F2B65A")
        : new("#EDF0F4", "#FFFFFF", "#F5F7F9", "#E4E8ED", "#DBE0E6", "#161B22", "#54606E", "#E6EBEF", Accent, "#9A6A12");

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
            // Theme dictionaries on the theme itself are found before Fluent's own control resources.
            theme.Resources.ThemeDictionaries[variant] = ControlResources(c, variant == ThemeVariant.Dark);
        }
        theme.Add(new Style(x => x.OfType<ComboBox>()) { Setters = {
            new Setter(ComboBox.PaddingProperty, new Thickness(10, 4, 4, 4)),
            new Setter(ComboBox.VerticalContentAlignmentProperty, Avalonia.Layout.VerticalAlignment.Center) } });
        theme.Resources["ControlCornerRadius"] = new CornerRadius(ControlRadius);
        theme.Resources["OverlayCornerRadius"] = new CornerRadius(CardRadius);
        theme.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
        // Later styles in the theme win over Fluent's own; check boxes keep a square-ish box under the 8 px control radius.
        theme.Add(new Style(x => x.OfType<CheckBox>()) { Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)) } });
        // Task popovers (the colour picker) draw their own dark surface, so the presenter itself is bare.
        theme.Add(new Style(x => x.OfType<FlyoutPresenter>().Class("task")) { Setters =
        {
            new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent), new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(0)), new Setter(Avalonia.Layout.Layoutable.MinWidthProperty, 0d), new Setter(Avalonia.Layout.Layoutable.MinHeightProperty, 0d)
        } });
        return theme;
    }

    // Every window uses the same quiet controls as the tray panel: tinted surfaces, hairline borders and amethyst state.
    private static ResourceDictionary ControlResources(ThemeColors c, bool dark)
    {
        IBrush B(string color, double opacity = 1) => new SolidColorBrush(Color.Parse(color), opacity);
        var surface = B(c.Surface); var secondary = B(c.SecondarySurface); var hover = B(c.Hover);
        var border = B(c.Border); var text = B(c.Text); var muted = B(c.SecondaryText);
        var disabledText = B(c.SecondaryText, .55); var accent = B(c.Accent);
        // A filled accent always uses the brand amethyst with white text, as on the tray panel's capture tile.
        var fill = B(Accent); var onFill = Brushes.White;
        var transparent = Brushes.Transparent;
        var r = new ResourceDictionary();
        void Set(object value, params string[] keys) { foreach (var key in keys) r[key] = value; }

        // Light secondary buttons are white with a soft outline (spec ②); dark task surfaces keep tinted buttons.
        var outline = dark ? border : B("#101220", .12);
        Set(dark ? secondary : surface, "ButtonBackground", "ToggleButtonBackground", "RepeatButtonBackground");
        Set(dark ? hover : secondary, "ButtonBackgroundPointerOver", "ToggleButtonBackgroundPointerOver", "RepeatButtonBackgroundPointerOver");
        Set(dark ? border : hover, "ButtonBackgroundPressed", "ToggleButtonBackgroundPressed", "RepeatButtonBackgroundPressed");
        Set(dark ? secondary : surface, "ButtonBackgroundDisabled", "ToggleButtonBackgroundDisabled");
        Set(outline, "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled",
            "ToggleButtonBorderBrush", "ToggleButtonBorderBrushPointerOver", "ToggleButtonBorderBrushPressed", "ToggleButtonBorderBrushDisabled");
        Set(text, "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed",
            "ToggleButtonForeground", "ToggleButtonForegroundPointerOver", "ToggleButtonForegroundPressed");
        Set(disabledText, "ButtonForegroundDisabled", "ToggleButtonForegroundDisabled", "ToggleButtonForegroundCheckedDisabled");
        Set(new Thickness(1), "ButtonBorderThemeThickness", "ToggleButtonBorderThemeThickness");

        Set(fill, "AccentButtonBackground", "ToggleButtonBackgroundChecked");
        Set(B("#303742"), "AccentButtonBackgroundPointerOver", "ToggleButtonBackgroundCheckedPointerOver");
        Set(B("#11161B"), "AccentButtonBackgroundPressed", "ToggleButtonBackgroundCheckedPressed");
        Set(B("#CFC5F5"), "AccentButtonBackgroundDisabled", "ToggleButtonBackgroundCheckedDisabled");
        Set(transparent, "AccentButtonBorderBrush", "AccentButtonBorderBrushPointerOver", "AccentButtonBorderBrushPressed", "AccentButtonBorderBrushDisabled",
            "ToggleButtonBorderBrushChecked", "ToggleButtonBorderBrushCheckedPointerOver", "ToggleButtonBorderBrushCheckedPressed", "ToggleButtonBorderBrushCheckedDisabled");
        Set(onFill, "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed", "AccentButtonForegroundDisabled",
            "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed");

        Set(surface, "ComboBoxBackground", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundUnfocused", "ComboBoxBackgroundPressed",
            "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused");
        Set(secondary, "ComboBoxBackgroundDisabled", "TextControlBackgroundDisabled");
        Set(border, "ComboBoxBorderBrush", "ComboBoxBorderBrushDisabled", "TextControlBorderBrush", "TextControlBorderBrushDisabled");
        Set(B(c.SecondaryText, .6), "ComboBoxBorderBrushPointerOver", "TextControlBorderBrushPointerOver");
        Set(accent, "ComboBoxBorderBrushPressed", "TextControlBorderBrushFocused", "ComboBoxBackgroundBorderBrushFocused");
        Set(text, "ComboBoxForeground", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed",
            "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused");
        Set(muted, "ComboBoxDropDownGlyphForeground", "ComboBoxDropDownGlyphForegroundFocused", "ComboBoxDropDownGlyphForegroundFocusedPressed",
            "ComboBoxPlaceHolderForeground", "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver",
            "TextControlPlaceholderForegroundFocused");
        Set(disabledText, "ComboBoxForegroundDisabled", "ComboBoxDropDownGlyphForegroundDisabled", "TextControlForegroundDisabled");
        Set(new SolidColorBrush(Color.Parse(c.Accent), .28), "TextControlSelectionHighlightColor");

        // Drop-downs, menus and flyouts share the tray panel surface.
        Set(surface, "ComboBoxDropDownBackground", "MenuFlyoutPresenterBackground", "FlyoutPresenterBackground");
        Set(border, "ComboBoxDropDownBorderBrush", "MenuFlyoutPresenterBorderBrush", "FlyoutBorderThemeBrush");
        Set(new Thickness(4), "MenuFlyoutPresenterThemePadding", "ComboBoxDropdownBorderPadding");
        Set(transparent, "ComboBoxItemBackground", "MenuFlyoutItemBackground");
        Set(hover, "ComboBoxItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPointerOver", "ComboBoxItemBackgroundSelectedPointerOver");
        Set(border, "ComboBoxItemBackgroundPressed", "MenuFlyoutItemBackgroundPressed", "ComboBoxItemBackgroundSelectedPressed");
        Set(B(c.AccentSurface), "ComboBoxItemBackgroundSelected");
        Set(text, "ComboBoxItemForeground", "ComboBoxItemForegroundPointerOver", "ComboBoxItemForegroundPressed",
            "ComboBoxItemForegroundSelected", "ComboBoxItemForegroundSelectedPointerOver", "ComboBoxItemForegroundSelectedPressed",
            "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed");
        Set(muted, "MenuFlyoutItemKeyboardAcceleratorTextForeground", "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver",
            "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed");

        Set(fill, "ToggleSwitchFillOn", "ToggleSwitchStrokeOn", "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver",
            "CheckBoxCheckBackgroundFillCheckedPressed");
        Set(B("#303742"), "ToggleSwitchFillOnPointerOver", "ToggleSwitchStrokeOnPointerOver");
        Set(B("#11161B"), "ToggleSwitchFillOnPressed", "ToggleSwitchStrokeOnPressed");
        // An off switch is a flat grey track with a white knob, as in spec ②.
        var offTrack = dark ? hover : B("#D9D8E2");
        Set(offTrack, "ToggleSwitchFillOff", "ToggleSwitchFillOffPointerOver", "ToggleSwitchFillOffPressed");
        Set(dark ? muted : offTrack, "ToggleSwitchStrokeOff", "ToggleSwitchStrokeOffPointerOver", "ToggleSwitchStrokeOffPressed");
        Set(dark ? muted : onFill, "ToggleSwitchKnobFillOff", "ToggleSwitchKnobFillOffPointerOver", "ToggleSwitchKnobFillOffPressed");
        Set(B(dark ? c.Hover : "#E6E5EC"), "ToggleSwitchFillOffDisabled", "ToggleSwitchStrokeOffDisabled", "ToggleSwitchFillOnDisabled", "ToggleSwitchStrokeOnDisabled");
        Set(B(dark ? c.SecondaryText : "#F5F7F9"), "ToggleSwitchKnobFillOffDisabled", "ToggleSwitchKnobFillOnDisabled");
        Set(onFill, "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed");

        Set(surface, "CheckBoxCheckBackgroundFillUnchecked", "CheckBoxCheckBackgroundFillUncheckedPointerOver");
        Set(hover, "CheckBoxCheckBackgroundFillUncheckedPressed");
        Set(muted, "CheckBoxCheckBackgroundStrokeUnchecked", "CheckBoxCheckBackgroundStrokeUncheckedPointerOver", "CheckBoxCheckBackgroundStrokeUncheckedPressed");
        Set(onFill, "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver",
            "CheckBoxCheckGlyphForegroundCheckedPressed");
        Set(text, "CheckBoxForegroundUnchecked", "CheckBoxForegroundUncheckedPointerOver", "CheckBoxForegroundUncheckedPressed",
            "CheckBoxForegroundChecked", "CheckBoxForegroundCheckedPointerOver", "CheckBoxForegroundCheckedPressed");
        Set(transparent, "CheckBoxBackgroundUnchecked", "CheckBoxBackgroundUncheckedPointerOver", "CheckBoxBackgroundUncheckedPressed",
            "CheckBoxBackgroundChecked", "CheckBoxBackgroundCheckedPointerOver", "CheckBoxBackgroundCheckedPressed");

        Set(accent, "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
            "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed");
        Set(border, "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillPressed");
        Set(accent, "SystemControlHighlightAccentBrush");
        return r;
    }
}

internal sealed record ThemeColors(string Background, string Surface, string SecondarySurface, string Hover,
    string Border, string Text, string SecondaryText, string AccentSurface, string Accent, string Warning);
