using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Tbox.Core;
using Tbox.Desktop.Models;

namespace Tbox.Desktop.Views;

// The first row shared by capture and pin editing: tools | colour, stroke size | undo, redo.
public sealed class AnnotationToolbar : Border
{
    // Colours from spec ⑤, in swatch order.
    public static readonly uint[] Palette = [0xFFE5484D, 0xFFF5B400, 0xFF1F9D61, 0xFF3D7BFF, 0xFF6D4AE8, 0xFF1A1B22, 0xFFFFFFFF];
    private static readonly AnnotationTool[] Tools =
    [
        AnnotationTool.Rectangle, AnnotationTool.Ellipse, AnnotationTool.Arrow, AnnotationTool.Line, AnnotationTool.Pen,
        AnnotationTool.Highlighter, AnnotationTool.Mosaic, AnnotationTool.Text, AnnotationTool.Number, AnnotationTool.Eraser
    ];
    private static readonly Dictionary<AnnotationTool, (IconKind Icon, string Name)> Specs = new()
    {
        [AnnotationTool.Rectangle] = (IconKind.Rectangle, "矩形"), [AnnotationTool.Ellipse] = (IconKind.Ellipse, "椭圆"),
        [AnnotationTool.Arrow] = (IconKind.Arrow, "箭头"), [AnnotationTool.Line] = (IconKind.Line, "直线"),
        [AnnotationTool.Polyline] = (IconKind.Polyline, "折线 · 双击完成"),
        [AnnotationTool.Pen] = (IconKind.Pen, "画笔 · B"), [AnnotationTool.Highlighter] = (IconKind.Highlighter, "高亮"),
        [AnnotationTool.Mosaic] = (IconKind.Mosaic, "马赛克"), [AnnotationTool.Text] = (IconKind.Text, "文字 · T"),
        [AnnotationTool.Number] = (IconKind.Number, "序号"), [AnnotationTool.Eraser] = (IconKind.Eraser, "橡皮擦")
    };
    private readonly Dictionary<AnnotationTool, Button> _tools = [];
    private readonly Button _undo;
    private readonly Button _redo;
    public AnnotationToolbar(AnnotationEditor editor, Action prepare)
    {
        Background = Ui.ToolbarBackground; Padding = new Thickness(4); CornerRadius = new CornerRadius(DesignTokens.TaskRadius);
        BorderBrush = Ui.ToolbarBorder; BorderThickness = new Thickness(1);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var tool in Tools)
        {
            var (icon, name) = Specs[tool];
            var button = IconButton(icon, name, () => { prepare(); editor.Select(editor.Tool == tool ? null : tool); });
            _tools.Add(tool, button); row.Children.Add(button);
        }
        row.Children.Add(Ui.ToolbarDivider());
        row.Children.Add(ColorButton(editor));
        row.Children.Add(IconButton(IconKind.Minus, "减小粗细 · 1", () => editor.ChangeSize(-1)));
        row.Children.Add(IconButton(IconKind.Plus, "增大粗细 · 2", () => editor.ChangeSize(1)));
        row.Children.Add(Ui.ToolbarDivider());
        _undo = IconButton(IconKind.Undo, $"撤销 · {ShortcutLabels.Command}Z", editor.Undo); row.Children.Add(_undo);
        _redo = IconButton(IconKind.Redo, $"重做 · {ShortcutLabels.Command}{(OperatingSystem.IsMacOS() ? "⇧Z" : "Y")}", editor.Redo); row.Children.Add(_redo);
        Child = row;
        editor.Changed += Refresh;
        Refresh();
        void Refresh()
        {
            // Change only selected state; leave the stable visual tree and hover state intact.
            foreach (var (tool, button) in _tools) Ui.SetToolbarSelected(button, editor.Tool == tool);
            _undo.IsEnabled = editor.Document.CanUndo; _redo.IsEnabled = editor.Document.CanRedo;
            _undo.Opacity = _undo.IsEnabled ? 1 : .35; _redo.Opacity = _redo.IsEnabled ? 1 : .35;
        }
    }

    // A filled dot with a white ring; the popover is the same dark surface as the toolbar.
    private static Button ColorButton(AnnotationEditor editor)
    {
        var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1.5), Background = new SolidColorBrush(Color.FromUInt32(editor.Color)) };
        var button = Ui.ToolbarButton(IconKind.Pen, "颜色", () => { });
        button.Content = dot;
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var popup = new Flyout { Placement = PlacementMode.Bottom, Content = Ui.ToolbarSurface(swatches, 8) };
        popup.FlyoutPresenterClasses.Add("task");
        button.Flyout = popup;
        var rings = new List<(uint Color, Border Ring)>();
        void Mark() { foreach (var (color, ring) in rings) ring.BorderBrush = color == editor.Color ? Brushes.White : Brushes.Transparent; }
        foreach (var color in Palette)
        {
            var swatch = new Button { Width = 26, Height = 26, MinWidth = 26, MinHeight = 26, Padding = new Thickness(0), CornerRadius = new CornerRadius(13),
                Background = Brushes.Transparent, BorderThickness = default };
            var ring = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1.5), Padding = new Thickness(2.5),
                Child = new Border { CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Color.FromUInt32(color)) } };
            swatch.Content = ring; rings.Add((color, ring));
            swatch.Resources["ButtonBackgroundPointerOver"] = Brush.Parse(DesignTokens.TaskHover);
            swatch.Resources["ButtonBackgroundPressed"] = Brush.Parse(DesignTokens.TaskBorder);
            AutomationProperties.SetName(swatch, $"颜色 {color & 0xFFFFFF:X6}");
            swatch.Click += (_, _) => { editor.Color = color; dot.Background = new SolidColorBrush(Color.FromUInt32(color)); Mark(); popup.Hide(); };
            swatches.Children.Add(swatch);
        }
        Mark();
        return button;
    }

    internal static Button IconButton(IconKind icon, string tooltip, Action action, IBrush? brush = null) =>
        Ui.ToolbarButton(icon, tooltip, action, brush);
}
