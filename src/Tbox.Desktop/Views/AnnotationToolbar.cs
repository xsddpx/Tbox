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
        Background = Ui.ToolbarBackground; Padding = new Thickness(4); CornerRadius = new CornerRadius(8);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var toolSpecs = new (AnnotationTool Tool, string Icon, string Name)[]
        {
            (AnnotationTool.Rectangle, "□", "矩形"), (AnnotationTool.Ellipse, "○", "椭圆"),
            (AnnotationTool.Line, "╱", "直线"), (AnnotationTool.Polyline, "⌁", "折线 · 双击完成"),
            (AnnotationTool.Arrow, "↗", "箭头"), (AnnotationTool.Number, "①", "序号"),
            (AnnotationTool.Pen, "✎", "画笔 · B"), (AnnotationTool.Highlighter, "▰", "高亮"),
            (AnnotationTool.Mosaic, "▦", "马赛克"), (AnnotationTool.Text, "T", "文字 · T"),
            (AnnotationTool.Eraser, "◇", "对象橡皮擦")
        };
        foreach (var (tool, icon, name) in toolSpecs)
        {
            var button = IconButton(icon, name, () => { prepare(); editor.Select(editor.Tool == tool ? null : tool); });
            _tools.Add(tool, button); row.Children.Add(button);
        }
        row.Children.Add(new Separator { Width = 1, Margin = new Thickness(4, 0) });
        var colors = new WrapPanel { MaxWidth = 132, Margin = new Thickness(6) };
        var colorButton = IconButton("●", "颜色", () => { });
        colorButton.Foreground = new SolidColorBrush(Color.FromUInt32(editor.Color));
        var flyout = new Flyout { Content = colors };
        colorButton.Flyout = flyout;
        foreach (var color in new uint[] { 0xFFFF3B30, 0xFFFFCC00, 0xFF34C759, 0xFF0078D4, 0xFFAF52DE, 0xFFFFFFFF, 0xFF000000, 0xFFFF9500 })
        {
            var swatch = new Button { Width = 26, Height = 26, Margin = new Thickness(3), Padding = new Thickness(0),
                Content = new Border { Background = new SolidColorBrush(Color.FromUInt32(color)), Width = 16, Height = 16, CornerRadius = new CornerRadius(8) } };
            Avalonia.Automation.AutomationProperties.SetName(swatch, $"颜色 {color:X8}");
            swatch.Click += (_, _) => { editor.Color = color; colorButton.Foreground = new SolidColorBrush(Color.FromUInt32(color)); flyout.Hide(); };
            colors.Children.Add(swatch);
        }
        row.Children.Add(colorButton);
        row.Children.Add(IconButton("−", "减小 · 1", () => editor.ChangeSize(-1)));
        row.Children.Add(IconButton("+", "增大 · 2", () => editor.ChangeSize(1)));
        _undo = IconButton("↶", "撤销 · ⌘Z", editor.Undo); row.Children.Add(_undo);
        _redo = IconButton("↷", "重做 · ⌘⇧Z", editor.Redo); row.Children.Add(_redo);
        row.Children.Add(new Separator { Width = 1, Margin = new Thickness(4, 0) });
        row.Children.Add(Actions); Child = row;
        editor.Changed += Refresh;
        Refresh();
        void Refresh()
        {
            foreach (var (tool, button) in _tools)
            {
                // Change only selected state; leave the stable visual tree and hover state intact.
                button.Classes.Set("accent", editor.Tool == tool);
            }
            _undo.IsEnabled = editor.Document.CanUndo; _redo.IsEnabled = editor.Document.CanRedo;
        }
    }

    public static Button IconButton(string icon, string tooltip, Action action)
    {
        var button = Ui.Button(icon, action, tooltip);
        button.Width = 30; button.Height = 32; button.MinWidth = 30; button.MinHeight = 32;
        button.Padding = new Thickness(0); button.FontSize = 17;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        return button;
    }
}
