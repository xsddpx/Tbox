using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Tbox.Core;
using Windows.UI;

namespace Tbox.App;

internal sealed record AnnotationToolbarActions(
    Func<Task> RecognizeText,
    Func<Task> Save,
    Func<Task> Copy,
    string CopyToolTip,
    Func<Task> Terminal,
    string TerminalGlyph,
    string TerminalToolTip,
    bool TerminalUsesFluentIcon = true,
    Func<Task>? StartLongCapture = null,
    Func<Task>? StartRecording = null);

internal sealed class AnnotationToolbar : IDisposable
{
    internal const double ButtonWidth = TboxUi.TaskButtonWidth;
    internal const double ButtonHeight = TboxUi.TaskButtonHeight;
    internal const double SymbolIconSize = 17;
    internal const double FluentIconSize = 14;
    internal const double IconBoxSize = TboxUi.TaskIconBoxSize;

    private readonly CaptureAnnotationController _annotations;
    private readonly Border _view;
    private readonly Dictionary<AnnotationTool, Button> _toolButtons = [];
    private readonly Button _colorButton;
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private readonly TextBlock _widthText;
    private bool _disposed;

    internal AnnotationToolbar(
        CaptureAnnotationController annotations,
        AnnotationToolbarActions actions,
        Visibility initialVisibility)
    {
        _annotations = annotations;
        _view = CreateToolbarRoot(initialVisibility);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        AddToolButton(tools, AnnotationTool.Rectangle, "□", "矩形（Shift：正方形）");
        AddToolButton(tools, AnnotationTool.Ellipse, "○", "椭圆（Shift：圆）");
        AddToolButton(tools, AnnotationTool.Arrow, "➜", "箭头（Shift：吸附 45°）");
        AddToolButton(tools, AnnotationTool.Number, "①", "序号（点击自动递增）");
        AddToolButton(tools, AnnotationTool.Pen, "\uE76D", "画笔（B）", useFluentIcon: true);
        AddToolButton(tools, AnnotationTool.Mosaic, "▦", "马赛克");
        AddToolButton(tools, AnnotationTool.Text, "T", "文字（T，Ctrl+Enter 提交）");
        AddToolButton(tools, AnnotationTool.Eraser, "\uE75C", "橡皮擦（只擦除标注）", useFluentIcon: true);
        tools.Children.Add(CreateSeparator());

        _colorButton = CreateButton("\uE790", "标注颜色", useFluentIcon: true);
        var colorFlyout = new Flyout { FlyoutPresenterStyle = CreateCompactFlyoutPresenterStyle() };
        var colors = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Padding = new Thickness(8)
        };
        foreach (var choice in new (uint Value, string Name)[]
                 {
                     (0xFFFF3B30, "红色"),
                     (0xFFFFCC00, "黄色"),
                     (0xFF34C759, "绿色"),
                     (0xFF007AFF, "蓝色"),
                     (0xFF000000, "黑色"),
                     (0xFFFFFFFF, "白色")
                 })
        {
            var swatch = CreateButton("●", choice.Name, 32, false, 32, 16, 20);
            SetIconColor(swatch, ToColor(choice.Value));
            swatch.Click += (_, _) =>
            {
                _annotations.SetColor(choice.Value);
                colorFlyout.Hide();
            };
            colors.Children.Add(swatch);
        }
        colorFlyout.Content = colors;
        _colorButton.Flyout = colorFlyout;
        tools.Children.Add(_colorButton);

        var thinner = CreateButton("\uE738", "减小粗细（1）", useFluentIcon: true);
        thinner.Click += (_, _) => _annotations.AdjustWidth(-1);
        tools.Children.Add(thinner);
        _widthText = new TextBlock
        {
            Text = "3",
            Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 24,
            FontSize = 14,
            TextAlignment = TextAlignment.Center
        };
        tools.Children.Add(_widthText);
        var thicker = CreateButton("\uE710", "增大粗细（2）", useFluentIcon: true);
        thicker.Click += (_, _) => _annotations.AdjustWidth(1);
        tools.Children.Add(thicker);
        tools.Children.Add(CreateSeparator());

        _undoButton = CreateButton("\uE7A7", "撤销（Ctrl+Z）", useFluentIcon: true);
        _undoButton.Click += (_, _) => _annotations.Undo();
        tools.Children.Add(_undoButton);
        _redoButton = CreateButton("\uE7A6", "重做（Ctrl+Y）", useFluentIcon: true);
        _redoButton.Click += (_, _) => _annotations.Redo();
        tools.Children.Add(_redoButton);
        tools.Children.Add(CreateSeparator());

        if (actions.StartLongCapture is not null)
        {
            var longCaptureButton = CreateButton("↕", "滚动长截图（手动滚动）", iconSize: 18);
            longCaptureButton.Click += async (_, _) => await actions.StartLongCapture();
            tools.Children.Add(longCaptureButton);
        }
        if (actions.StartRecording is not null)
        {
            var recordingButton = CreateButton("\uE714", "区域录屏", useFluentIcon: true);
            recordingButton.Click += async (_, _) => await actions.StartRecording();
            tools.Children.Add(recordingButton);
        }
        if (actions.StartLongCapture is not null || actions.StartRecording is not null)
        {
            tools.Children.Add(CreateSeparator());
        }

        var ocrButton = CreateButton("文", "文字识别", iconSize: 15);
        ocrButton.Click += async (_, _) => await actions.RecognizeText();
        tools.Children.Add(ocrButton);
        var saveButton = CreateButton("\uE792", "另存为（Ctrl+S）", useFluentIcon: true);
        saveButton.Click += async (_, _) => await actions.Save();
        tools.Children.Add(saveButton);
        var copyButton = CreateButton("\uE8C8", actions.CopyToolTip, useFluentIcon: true);
        copyButton.Click += async (_, _) => await actions.Copy();
        tools.Children.Add(copyButton);
        var terminalButton = CreateButton(
            actions.TerminalGlyph,
            actions.TerminalToolTip,
            useFluentIcon: actions.TerminalUsesFluentIcon);
        terminalButton.Click += async (_, _) => await actions.Terminal();
        tools.Children.Add(terminalButton);

        _view.Child = tools;
        _annotations.StateChanged += Refresh;
        Refresh();
    }

    internal Border View => _view;

    internal void Refresh()
    {
        foreach (var pair in _toolButtons)
        {
            ApplyButtonVisualState(pair.Value, pair.Key == _annotations.CurrentTool);
        }
        SetIconColor(_colorButton, ToColor(_annotations.ColorArgb));
        _widthText.Text = Math.Round(_annotations.CurrentWidth).ToString();
        _undoButton.IsEnabled = _annotations.CanUndo;
        _redoButton.IsEnabled = _annotations.CanRedo;
    }

    private void AddToolButton(
        StackPanel panel,
        AnnotationTool tool,
        string glyph,
        string tooltip,
        bool useFluentIcon = false)
    {
        var button = CreateButton(glyph, tooltip, useFluentIcon: useFluentIcon);
        button.Click += (_, _) => _annotations.SetTool(tool);
        _toolButtons[tool] = button;
        panel.Children.Add(button);
    }

    internal static Border CreateToolbarRoot(Visibility visibility) =>
        TboxUi.CreateTaskSurface(visibility: visibility);

    internal static Button CreateButton(
        string glyph,
        string tooltip,
        double width = ButtonWidth,
        bool useFluentIcon = false,
        double height = ButtonHeight,
        double? iconSize = null,
        double iconBoxSize = IconBoxSize)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily(useFluentIcon ? "Segoe Fluent Icons" : "Segoe UI Symbol"),
            FontSize = iconSize ?? (useFluentIcon ? FluentIconSize : SymbolIconSize),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconHost = new Grid
        {
            Width = iconBoxSize,
            Height = iconBoxSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconHost.Children.Add(icon);
        var button = new StableToolbarButton
        {
            Content = iconHost,
            Width = width,
            Height = height,
            Padding = new Thickness(0),
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.SetSelected(false);
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private static void ApplyButtonVisualState(Button button, bool selected)
    {
        if (button is StableToolbarButton stableButton)
        {
            stableButton.SetSelected(selected);
            return;
        }

        TboxUi.ApplyTaskButtonVisual(button, selected);
    }

    private sealed class StableToolbarButton : Button
    {
        private bool? _selected;

        internal void SetSelected(bool selected)
        {
            // Refresh can run while the pointer-over visual state is active. Replacing the
            // brushes in that state restarts the template transition and makes hover flicker.
            if (_selected == selected) return;
            _selected = selected;

            TboxUi.ApplyTaskButtonVisual(this, selected);
        }
    }

    private static void SetIconColor(Button button, Color color)
    {
        if (button.Content is Grid { Children.Count: > 0 } host && host.Children[0] is FontIcon icon)
        {
            icon.Foreground = new SolidColorBrush(color);
        }
    }

    internal static Border CreateSeparator() => new()
    {
        Width = 1,
        Height = 22,
        Margin = new Thickness(4),
        Background = TboxUi.Brush(TboxUi.TaskDividerColor)
    };

    private static Style CreateCompactFlyoutPresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        return style;
    }

    private static Color ToColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    public void Dispose()
    {
        if (_disposed) return;
        _annotations.StateChanged -= Refresh;
        _disposed = true;
    }
}
