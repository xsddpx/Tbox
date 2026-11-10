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

namespace Tbox.Desktop.Views;

// Shared B toolbar: ten tools | undo | outputs, more | finish. Properties are a temporary horizontal strip.
public sealed class AnnotationToolbar : Border
{
    private static readonly uint[] Palette = [0xFFE5484D, 0xFFF5B400, 0xFF1F9D61, 0xFF3D7BFF, 0xFF6D4AE8, 0xFF1A1B22];
    private const double PropertyControlHeight = 32;
    private static readonly (AnnotationTool Tool, IconKind Icon, string Name)[] Specs =
    [
        (AnnotationTool.Rectangle, IconKind.Rectangle, "矩形"), (AnnotationTool.Ellipse, IconKind.Ellipse, "椭圆"),
        (AnnotationTool.Arrow, IconKind.Arrow, "箭头"), (AnnotationTool.Line, IconKind.Line, "直线"),
        (AnnotationTool.Pen, IconKind.Pen, "画笔 · B"), (AnnotationTool.Highlighter, IconKind.Highlighter, "高亮"),
        (AnnotationTool.Mosaic, IconKind.Mosaic, "马赛克"), (AnnotationTool.Text, IconKind.Text, "文字 · T"),
        (AnnotationTool.Number, IconKind.Number, "序号"), (AnnotationTool.Eraser, IconKind.Eraser, "橡皮擦")
    ];
    private readonly AnnotationEditor _editor;
    private readonly Dictionary<AnnotationTool, Button> _tools = [];
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly StackPanel _menu = new() { Spacing = 2 };
    private readonly Button _undo, _redo, _more;
    private readonly List<(uint Color, Button Button, Border Ring)> _swatches = [];
    private AnnotationTool? _propertyTool;
    private Slider? _size;
    private TextBlock? _value;
    private bool _syncing;
    internal Popup PropertiesPopup { get; }
    internal Popup MorePopup { get; }
    internal Border PropertiesSurface { get; } = Ui.ToolbarSurface(new StackPanel(), 5);
    internal StackPanel MainRow => _row;

    public AnnotationToolbar(AnnotationEditor editor, Action prepare)
    {
        _editor = editor;
        Background = Ui.ToolbarBackground; Padding = new Thickness(5); CornerRadius = new CornerRadius(DesignTokens.TaskRadius);
        BorderBrush = Ui.ToolbarBorder; BorderThickness = new Thickness(1);
        PropertiesPopup = CreatePopup(PropertiesSurface);
        MorePopup = CreatePopup(Ui.ToolbarSurface(_menu, 5));
        foreach (var (tool, icon, name) in Specs)
        {
            var button = IconButton(icon, name, () => { prepare(); editor.Select(editor.Tool == tool ? null : tool); });
            _tools.Add(tool, button); _row.Children.Add(button);
        }
        _row.Children.Add(Ui.ToolbarDivider());
        _undo = IconButton(IconKind.Undo, $"撤销 · {ShortcutLabels.Command}Z", editor.Undo);
        _row.Children.Add(_undo); _row.Children.Add(Ui.ToolbarDivider());
        _more = IconButton(IconKind.More, "更多操作", OpenMore);
        _actions.Children.Add(_more); _row.Children.Add(_actions);
        _redo = MenuButton(IconKind.Redo, $"重做 · {ShortcutLabels.Command}{(OperatingSystem.IsMacOS() ? "⇧Z" : "Y")}", editor.Redo);
        _menu.Children.Add(_redo);
        Child = new Grid { Children = { _row, PropertiesPopup, MorePopup } };
        editor.Changed += Refresh;
        DetachedFromVisualTree += (_, _) => ClosePopovers();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && !IsVisible) ClosePopovers(); };
        PropertiesSurface.AddHandler(KeyDownEvent, PopupKeys, RoutingStrategies.Bubble);
        _menu.AddHandler(KeyDownEvent, PopupKeys, RoutingStrategies.Bubble);
        Refresh();
    }

    internal void Complete(Button[] outputs, Button finish, params (IconKind Icon, string Name, Action Action)[] modes)
    {
        foreach (var button in outputs) _actions.Children.Insert(_actions.Children.Count - 1, button);
        foreach (var (icon, name, action) in modes) _menu.Children.Insert(_menu.Children.Count - 1, MenuButton(icon, name, action));
        _row.Children.Add(Ui.ToolbarDivider()); _row.Children.Add(finish);
    }

    private Popup CreatePopup(Control content) => new()
    {
        Child = content, PlacementTarget = this, Placement = PlacementMode.Custom,
        // Avalonia clips PlacementRect to the target before positioning. Inflate the computed anchor
        // in the callback so a vertical flip preserves the six-unit gap on BOTH sides.
        CustomPopupPlacementCallback = placement =>
        {
            placement.AnchorRectangle = placement.AnchorRectangle.Inflate(new Thickness(0, 6));
            placement.Anchor = PopupAnchor.BottomLeft; placement.Gravity = PopupGravity.BottomRight;
        },
        PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipY | PopupPositionerConstraintAdjustment.SlideX,
        IsLightDismissEnabled = true, OverlayDismissEventPassThrough = true, Topmost = true
    };

    private void Open(Popup popup, Button anchor)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || Bounds.Height <= 0 || !IsEffectivelyVisible) return;
        var x = anchor.TranslatePoint(default, this)?.X ?? 0;
        popup.PlacementRect = new Rect(x, 0, 1, Bounds.Height);
        // Capture overlays fill one display; pins use a native popup so it can extend beyond their small toolbar window.
        popup.ShouldUseOverlayLayer = top is CaptureWindow;
        popup.IsOpen = true;
    }

    private void OpenMore()
    {
        var opening = !MorePopup.IsOpen;
        ClosePopovers();
        if (opening) Open(MorePopup, _more);
    }

    internal bool DismissPopover()
    {
        if (!PropertiesPopup.IsOpen && !MorePopup.IsOpen) return false;
        var focus = MorePopup.IsOpen ? _more : _editor.Tool is { } tool && _tools.TryGetValue(tool, out var button) ? button : _more;
        ClosePopovers(); focus.Focus(); return true;
    }

    internal void ClosePopovers() { PropertiesPopup.IsOpen = false; MorePopup.IsOpen = false; }

    private void PopupKeys(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DismissPopover(); e.Handled = true; }
        // Enter on a slider or swatch adjusts properties; never finish the underlying screenshot.
        else if (e.Key == Key.Enter) e.Handled = true;
    }

    private Button MenuButton(IconKind icon, string name, Action action)
    {
        var button = Ui.ToolbarButton(icon, name, () => { ClosePopovers(); action(); });
        button.Width = 190; button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Padding = new Thickness(8, 0);
        var text = Ui.Text(name, 12); text.Foreground = Ui.ToolbarText;
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { new LineIcon(icon, brush: Ui.ToolbarText) { Width = 18, Height = 18 }, text } };
        return button;
    }

    private void Refresh()
    {
        _undo.IsEnabled = _editor.Document.CanUndo; _redo.IsEnabled = _editor.Document.CanRedo;
        _undo.Opacity = _undo.IsEnabled ? 1 : .35; _redo.Opacity = _redo.IsEnabled ? 1 : .35;
        if (_propertyTool != _editor.Tool)
        {
            // Drawing/size changes must not replace hover resources or restart transitions.
            foreach (var (candidate, control) in _tools) Ui.SetToolbarSelected(control, _editor.Tool == candidate);
            _propertyTool = _editor.Tool;
            ClosePopovers();
            if (_editor.Tool is { } tool && _tools.TryGetValue(tool, out var button))
            { BuildProperties(tool); Open(PropertiesPopup, button); }
        }
        UpdateProperties();
    }

    private void BuildProperties(AnnotationTool tool)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var name = Specs.First(s => s.Tool == tool).Name.Split(" · ")[0];
        var title = Label(name);
        title.MinWidth = 36;
        title.TextAlignment = TextAlignment.Center;
        // Match the gap before the divider with equal space on the leading side.
        title.Margin = new Thickness(4, 0, 0, 0);
        row.Children.Add(title); _swatches.Clear();
        if (tool is not (AnnotationTool.Mosaic or AnnotationTool.Eraser))
        {
            row.Children.Add(Ui.ToolbarDivider());
            var colors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var color in Palette)
            {
                var ring = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Padding = new Thickness(2),
                    BorderThickness = new Thickness(1), Child = new Border { CornerRadius = new CornerRadius(6),
                        BorderBrush = Brush.Parse("#808080"), BorderThickness = new Thickness(.5), Background = new SolidColorBrush(Color.FromUInt32(color)) } };
                var swatch = new ToggleButton { Width = 26, Height = PropertyControlHeight, MinWidth = 26, MinHeight = PropertyControlHeight,
                    HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                    Padding = default, Background = Brushes.Transparent, BorderThickness = default, Content = ring };
                AutomationProperties.SetName(swatch, $"颜色 {color & 0xFFFFFF:X6}"); ToolTip.SetTip(swatch, $"颜色 #{color & 0xFFFFFF:X6}");
                var hover = Brush.Parse(DesignTokens.TaskHover);
                var pressed = Brush.Parse(DesignTokens.TaskBorder);
                swatch.Resources["ToggleButtonBackgroundChecked"] = Brushes.Transparent;
                swatch.Resources["ToggleButtonBackgroundPointerOver"] = swatch.Resources["ToggleButtonBackgroundCheckedPointerOver"] = hover;
                swatch.Resources["ToggleButtonBackgroundPressed"] = swatch.Resources["ToggleButtonBackgroundCheckedPressed"] = pressed;
                swatch.Click += (_, _) => { _editor.Color = color; UpdateProperties(); };
                _swatches.Add((color, swatch, ring)); colors.Children.Add(swatch);
            }
            row.Children.Add(colors);
        }
        row.Children.Add(Ui.ToolbarDivider());
        var sizeName = tool switch { AnnotationTool.Text => "字号", AnnotationTool.Number => "序号大小",
            AnnotationTool.Mosaic => "颗粒大小", AnnotationTool.Eraser => "擦除范围", _ => "线宽" };
        row.Children.Add(Label(sizeName));
        var textSize = tool is AnnotationTool.Text or AnnotationTool.Number;
        _size = new Slider { Minimum = textSize ? 10 : 1, Maximum = textSize ? 120 : 32,
            TickFrequency = textSize ? 2 : 1, IsSnapToTickEnabled = true, Value = _editor.CurrentSize,
            Width = 80, Height = PropertyControlHeight, MinHeight = PropertyControlHeight,
            Padding = default, BorderThickness = default, VerticalAlignment = VerticalAlignment.Center };
        // Fluent's default 15 + 20 + 15 layout overflows a 32-unit toolbar control.
        const double thumbSize = 12;
        var sliderInset = new GridLength((PropertyControlHeight - thumbSize) / 2);
        _size.Resources["SliderPreContentMargin"] = _size.Resources["SliderPostContentMargin"] = sliderInset;
        _size.Resources["SliderHorizontalThumbWidth"] = _size.Resources["SliderHorizontalThumbHeight"] = thumbSize;
        AutomationProperties.SetName(_size, sizeName);
        _size.ValueChanged += (_, _) => { if (!_syncing) _editor.SetSize(_size.Value); };
        row.Children.Add(_size);
        _value = Label(""); _value.MinWidth = 39; row.Children.Add(_value);
        row.Children.Add(Ui.ToolbarDivider());
        row.Children.Add(IconButton(IconKind.Close, "收起属性 · Esc", () => DismissPopover()));
        PropertiesSurface.Child = row;
    }

    private void UpdateProperties()
    {
        if (_size is null || _value is null) return;
        _syncing = true;
        _size.Value = _editor.CurrentSize;
        var value = _editor.Tool switch { AnnotationTool.Mosaic => Math.Max(6, _editor.StrokeWidth * 2),
            AnnotationTool.Eraser => _editor.StrokeWidth * 3, AnnotationTool.Highlighter => _editor.StrokeWidth * 6, _ => _editor.CurrentSize };
        _value.Text = $"{value:0} px";
        foreach (var (color, button, ring) in _swatches)
        {
            ring.BorderBrush = color == _editor.Color ? Brushes.White : Brushes.Transparent;
            ((ToggleButton)button).IsChecked = color == _editor.Color;
        }
        _syncing = false;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 11, Foreground = Ui.ToolbarText, VerticalAlignment = VerticalAlignment.Center };

    internal static Button IconButton(IconKind icon, string tooltip, Action action, IBrush? brush = null)
    {
        var button = Ui.ToolbarButton(icon, tooltip, action, brush);
        if (icon == IconKind.Copy)
        {
            button.Background = Brush.Parse("#41434E");
            button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse("#565762");
        }
        return button;
    }
}
