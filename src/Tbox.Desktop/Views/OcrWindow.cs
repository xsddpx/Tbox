using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Core;
using Tbox.Desktop.Models;

namespace Tbox.Desktop.Views;

public sealed class OcrWindow : Window
{
    private readonly DesktopController _controller;
    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 14,
        Padding = new Thickness(8), BorderThickness = new Thickness(0), Watermark = "识别结果将在这里显示"
    };
    private readonly TextBlock _status = Ui.Caption("");
    private readonly Image _original = new() { Stretch = Stretch.Uniform };
    private readonly Grid _workspace = new()
    {
        ColumnDefinitions = new ColumnDefinitions("*,0"),
        RowDefinitions = new RowDefinitions("*,0")
    };
    private readonly Border _sourceCard;
    private readonly Border _editorCard;
    private readonly Button _sourceToggle;
    private readonly LatestOperation _requests = new();
    private bool? _showOriginal;
    private Picture? _picture;

    public OcrWindow(DesktopController controller)
    {
        _controller = controller;
        // The editor sits inside a card, so it draws no border or fill of its own.
        foreach (var state in new[] { "", "PointerOver", "Focused" }) _editor.Resources["TextControlBackground" + state] = Brushes.Transparent;
        _editor.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        Ui.Configure(this, "文字识别 · Tbox", 800, 600);
        MinWidth = 460; MinHeight = 360;
        var source = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 8 };
        var caption = Ui.Caption("原图");
        source.Children.Add(_original);
        Grid.SetRow(caption, 1);
        source.Children.Add(caption);
        _sourceCard = Ui.Card(source);
        _sourceCard.Padding = new Thickness(12);
        _sourceCard.IsVisible = false;
        AutomationProperties.SetName(_original, "识别原图");
        _sourceToggle = Ui.Button("显示原图", () =>
        {
            _showOriginal = !_sourceCard.IsVisible;
            ApplyWorkspaceLayout(ClientSize.Width);
        });
        _sourceToggle.IsEnabled = false;
        var topmost = new CheckBox { Content = "置顶" };
        topmost.IsCheckedChanged += (_, _) => Topmost = topmost.IsChecked == true;
        Activated += (_, _) => topmost.IsChecked = Topmost;
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(20, 14, 20, 16), RowSpacing = 12 };
        var header = Ui.Header("文字识别", "本机离线识别，可直接编辑", Ui.Row(_sourceToggle, topmost), out var brand);
        Closed += (_, _) => brand.Dispose();
        layout.Children.Add(header);

        var editorLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*") };
        editorLayout.Children.Add(new LogicalLineGutter(_editor));
        Grid.SetColumn(_editor, 1);
        editorLayout.Children.Add(_editor);
        _editorCard = Ui.Card(editorLayout);
        _editorCard.Padding = new Thickness(8);
        _workspace.Children.Add(_sourceCard);
        _workspace.Children.Add(_editorCard);
        Grid.SetRow(_workspace, 1);
        layout.Children.Add(_workspace);
        var actions = new DockPanel { LastChildFill = true };
        var copy = Ui.Primary("复制全部", () => controller.Run(async () =>
        {
            await (Clipboard ?? throw new InvalidOperationException("系统剪贴板不可用。")).SetTextAsync(_editor.Text ?? "");
            _status.Text = "已复制当前全文。";
        }));
        DockPanel.SetDock(copy, Dock.Right); actions.Children.Add(copy);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(0, 0, 12, 0);
        actions.Children.Add(_status); Grid.SetRow(actions, 2); layout.Children.Add(actions);
        Content = layout;
        SizeChanged += (_, e) => ApplyWorkspaceLayout(e.NewSize.Width);
        Closed += (_, _) => { _requests.Dispose(); _original.Source = null; _picture?.Dispose(); };
    }

    private void ApplyWorkspaceLayout(double width)
    {
        var wide = width >= 720;
        var expanded = _picture is not null && (_showOriginal ?? wide);
        var sideBySide = expanded && wide;
        var stacked = expanded && !wide;
        _sourceCard.IsVisible = expanded;
        _sourceToggle.IsEnabled = _picture is not null;
        var toggleLabel = expanded ? "收起原图" : "显示原图";
        _sourceToggle.Content = toggleLabel;
        AutomationProperties.SetName(_sourceToggle, toggleLabel);
        _workspace.ColumnDefinitions[0].Width = new GridLength(sideBySide ? 2 : 1, GridUnitType.Star);
        _workspace.ColumnDefinitions[1].Width = sideBySide ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        _workspace.RowDefinitions[0].Height = new GridLength(stacked ? 35 : 1, GridUnitType.Star);
        _workspace.RowDefinitions[1].Height = stacked ? new GridLength(65, GridUnitType.Star) : new GridLength(0);
        _workspace.ColumnSpacing = sideBySide ? 12 : 0;
        _workspace.RowSpacing = stacked ? 12 : 0;
        Grid.SetColumn(_editorCard, sideBySide ? 1 : 0);
        Grid.SetRow(_editorCard, stacked ? 1 : 0);
    }

    public async void Recognize(Picture picture)
    {
        var operation = _requests.Begin();
        _original.Source = null; _picture?.Dispose(); _picture = picture;
        _original.Source = picture.Bitmap;
        ApplyWorkspaceLayout(ClientSize.Width);
        _editor.Text = ""; _status.Text = "识别中…";
        try
        {
            var result = await _controller.Platform.RecognizeAsync(picture.Png, operation.Token);
            if (!_requests.IsCurrent(operation)) return;
            _editor.Text = result.Text;
            _status.Text = string.IsNullOrWhiteSpace(result.Text)
                ? "未识别到文字。可展开原图检查清晰度或重新选择区域。"
                : $"{OcrLineNumberPolicy.GetLogicalLineStarts(result.Text).Count} 行 · 本机离线识别";
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_requests.IsCurrent(operation)) { _editor.Focus(); _editor.SelectAll(); }
            }, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (_requests.IsCurrent(operation)) _status.Text = error.Message; }
        finally { _requests.Complete(operation); }
    }
}

internal sealed class LogicalLineGutter : Control
{
    private readonly TextBox _editor;
    public LogicalLineGutter(TextBox editor)
    {
        _editor = editor; ClipToBounds = true;
        editor.LayoutUpdated += (_, _) => InvalidateVisual();
        editor.TextChanged += (_, _) => InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var presenter = _editor.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (presenter is null || presenter.TranslatePoint(default, this) is not { } offset) return;
        var starts = OcrLineNumberPolicy.GetLogicalLineStarts(_editor.Text ?? "");
        for (var i = 0; i < starts.Count; i++)
        {
            var rect = presenter.TextLayout.HitTestTextPosition(starts[i]);
            var y = rect.Top + offset.Y;
            if (y < -24 || y > Bounds.Height) continue;
            var text = new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(_editor.FontFamily), 13,
                Brush.Parse(DesignTokens.Colors(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark).SecondaryText));
            context.DrawText(text, new Point(Math.Max(0, Bounds.Width - text.Width - 8), y));
        }
    }
}
