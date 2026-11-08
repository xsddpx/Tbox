using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tbox.Core;
using Tbox.Desktop.Models;

namespace Tbox.Desktop.Views;

public sealed class AnnotationToolbar : Border
{
    private readonly Dictionary<AnnotationTool, Button> _tools = [];
    private readonly Button _undo;
    private readonly Button _redo;
    public StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 2 };

    public AnnotationToolbar(AnnotationEditor editor, Action prepare)
    {
        Background = Ui.ToolbarBackground; Padding = new Thickness(4); CornerRadius = new CornerRadius(DesignTokens.TaskRadius);
        BorderBrush = Ui.ToolbarBorder; BorderThickness = new Thickness(1);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var toolSpecs = new (AnnotationTool Tool, IconKind Icon, string Name)[]
        {
            (AnnotationTool.Rectangle, IconKind.Rectangle, "矩形"), (AnnotationTool.Ellipse, IconKind.Ellipse, "椭圆"),
            (AnnotationTool.Line, IconKind.Line, "直线"), (AnnotationTool.Polyline, IconKind.Polyline, "折线 · 双击完成"),
            (AnnotationTool.Arrow, IconKind.Arrow, "箭头"), (AnnotationTool.Number, IconKind.Number, "序号"),
            (AnnotationTool.Pen, IconKind.Pen, "画笔 · B"), (AnnotationTool.Highlighter, IconKind.Highlighter, "高亮"),
            (AnnotationTool.Mosaic, IconKind.Mosaic, "马赛克"), (AnnotationTool.Text, IconKind.Text, "文字 · T"),
            (AnnotationTool.Eraser, IconKind.Eraser, "对象橡皮擦")
        };
        foreach (var (tool, icon, name) in toolSpecs)
        {
            var button = IconButton(icon, name, () => { prepare(); editor.Select(editor.Tool == tool ? null : tool); });
            _tools.Add(tool, button); row.Children.Add(button);
        }
        row.Children.Add(Ui.ToolbarDivider());
        var colors = new WrapPanel { MaxWidth = 132, Margin = new Thickness(6) };
        var swatchDot = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), BorderBrush = Ui.ToolbarText,
            BorderThickness = new Thickness(1.5), Background = new SolidColorBrush(Color.FromUInt32(editor.Color)) };
        var colorButton = Ui.ToolbarButton("", "颜色", () => { });
        colorButton.Content = swatchDot;
        var flyout = new Flyout { Content = colors };
        colorButton.Flyout = flyout;
        foreach (var color in new uint[] { 0xFFFF3B30, 0xFFFFCC00, 0xFF34C759, 0xFF0078D4, 0xFFAF52DE, 0xFFFFFFFF, 0xFF000000, 0xFFFF9500 })
        {
            var swatch = new Button { Width = 26, Height = 26, Margin = new Thickness(3), Padding = new Thickness(0),
                Content = new Border { Background = new SolidColorBrush(Color.FromUInt32(color)), Width = 16, Height = 16, CornerRadius = new CornerRadius(8) } };
            Avalonia.Automation.AutomationProperties.SetName(swatch, $"颜色 {color:X8}");
            swatch.Click += (_, _) => { editor.Color = color; swatchDot.Background = new SolidColorBrush(Color.FromUInt32(color)); flyout.Hide(); };
            colors.Children.Add(swatch);
        }
        row.Children.Add(colorButton);
        row.Children.Add(IconButton(IconKind.Minus, "减小 · 1", () => editor.ChangeSize(-1)));
        row.Children.Add(IconButton(IconKind.Plus, "增大 · 2", () => editor.ChangeSize(1)));
        _undo = IconButton(IconKind.Undo, "撤销 · ⌘Z", editor.Undo); row.Children.Add(_undo);
        _redo = IconButton(IconKind.Redo, "重做 · ⌘⇧Z", editor.Redo); row.Children.Add(_redo);
        row.Children.Add(Ui.ToolbarDivider());
        row.Children.Add(Actions); Child = row;
        editor.Changed += Refresh;
        Refresh();
        void Refresh()
        {
            foreach (var (tool, button) in _tools)
            {
                // Change only selected state; leave the stable visual tree and hover state intact.
                var selected = editor.Tool == tool;
                button.Classes.Set("selected", selected);
                button.Background = selected ? Brush.Parse(DesignTokens.Accent) : Brushes.Transparent;
                button.Resources["ButtonBackgroundPointerOver"] = selected ? Brush.Parse(DesignTokens.Accent) : Brush.Parse(DesignTokens.TaskHover);
            }
            _undo.IsEnabled = editor.Document.CanUndo; _redo.IsEnabled = editor.Document.CanRedo;
            _undo.Opacity = _undo.IsEnabled ? 1 : .4; _redo.Opacity = _redo.IsEnabled ? 1 : .4;
        }
    }

    internal static Button IconButton(IconKind icon, string tooltip, Action action, IBrush? brush = null) =>
        Ui.ToolbarButton(icon, tooltip, action, brush);
}
