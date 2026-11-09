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
    private readonly TextBlock _status = Ui.Description("");
    private readonly Border _statusDot = new() { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly Image _original = new() { Stretch = Stretch.Uniform };
    private readonly Grid _workspace = new()
    {
        ColumnDefinitions = new ColumnDefinitions("*,0"),
        RowDefinitions = new RowDefinitions("*,0")
    };
    private readonly Border _sourceCard;
    private readonly Border _editorCard;
    private readonly Border _sourceDivider = new() { Background = Brush.Parse(DesignTokens.Hairline) };
    private readonly TextBlock _sourceLabel = new() { FontSize = DesignTokens.Caption, Foreground = Brush.Parse(DesignTokens.Hint) };
    private readonly Control _empty;
    private readonly Button _sourceToggle;
    private readonly Button _copy;
    private readonly Button _recognizeAgain;
    private readonly LatestOperation _requests = new();
    private bool? _showOriginal;
    private Picture? _picture;

    public OcrWindow(DesktopController controller)
    {
        _controller = controller;
        // The editor sits inside the sheet, so it draws no border or fill of its own.
        foreach (var state in new[] { "", "PointerOver", "Focused" }) _editor.Resources["TextControlBackground" + state] = Brushes.Transparent;
        _editor.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        _editor.Resources["TextControlSelectionHighlightColor"] = new SolidColorBrush(Color.Parse(DesignTokens.Accent), .2);
        _editor.LineHeight = 24;
        Ui.Configure(this, "文字识别 · Tbox", 820, 560);
        MinWidth = 460; MinHeight = 360;
        Ui.ExtendIntoTitleBar(this);
        var brand = Ui.Brand();

        // Source: a dark canvas so any screenshot reads clearly against the white sheet.
        var canvas = new Border { CornerRadius = new CornerRadius(10), Background = Brush.Parse("#2A2B33"), Padding = new Thickness(12), ClipToBounds = true, Child = _original };
        var source = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 10, Children = { _sourceLabel, canvas } };
        Grid.SetRow(canvas, 1);
        _sourceCard = new Border { Padding = new Thickness(16), Child = source, IsVisible = false };
        AutomationProperties.SetName(_original, "识别原图");
        _sourceToggle = Ui.Button("显示原图", () =>
        {
            _showOriginal = !_sourceCard.IsVisible;
            ApplyWorkspaceLayout(ClientSize.Width);
        });
        _sourceToggle.IsEnabled = false;
        var topmost = new CheckBox { Content = "置顶", FontSize = 13, MinHeight = 0 };
        topmost.IsCheckedChanged += (_, _) => Topmost = topmost.IsChecked == true;
        Activated += (_, _) => topmost.IsChecked = Topmost;
        var header = Ui.ResultHeader(IconKind.Ocr, ToolTone.Green, "文字识别", Ui.Description("本机离线识别 · 可直接编辑"), Ui.Row(topmost, _sourceToggle));

        var editorLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*"), Margin = new Thickness(8, 8, 12, 8) };
        editorLayout.Children.Add(new LogicalLineGutter(_editor));
        Grid.SetColumn(_editor, 1);
        editorLayout.Children.Add(_editor);
        _editorCard = new Border { Child = editorLayout };
        _empty = EmptyState();
        _workspace.Children.Add(_sourceCard);
        _workspace.Children.Add(_sourceDivider);
        _workspace.Children.Add(_editorCard);
        _workspace.Children.Add(_empty);

        _copy = Ui.Primary("复制全部", () => controller.Run(async () =>
        {
            await (Clipboard ?? throw new InvalidOperationException("系统剪贴板不可用。")).SetTextAsync(_editor.Text ?? "");
            SetStatus($"{LineCount(_editor.Text)} 行 · 已复制当前全文", DesignTokens.Ready);
        }));
        _recognizeAgain = Ui.Button("识别原图", () => { if (_picture is { } picture) Recognize(new Picture(picture.Png) { CachePath = picture.CachePath }); });
        _recognizeAgain.IsEnabled = false;
        _status.FontSize = DesignTokens.Caption;
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _statusDot, _status } };
        var bar = Ui.ResultBar(status, _recognizeAgain, _copy);
        bar.Margin = new Thickness(20, 12, 20, 14);

        var strip = Ui.TitleStrip("文字识别 · Tbox", brand);
        var sheet = Ui.Sheet(_workspace);
        DockPanel.SetDock(strip, Dock.Top); DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom);
        Content = new DockPanel { Children = { strip, header, bar, sheet } };
        SizeChanged += (_, e) => ApplyWorkspaceLayout(e.NewSize.Width);
        Closed += (_, _) => { _requests.Dispose(); _original.Source = null; _picture?.Dispose(); brand.Dispose(); };
        _editor.TextChanged += (_, _) => RefreshEmpty();
        RefreshEmpty();
    }

    // Empty result: grey icon, one sentence of cause and the next step. Copying is disabled.
    private static Control EmptyState() => new StackPanel
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 4, IsVisible = false, Children =
        {
            new Border { Child = Ui.IconChip(IconKind.Ocr, ToolTone.Grey, 40, 20), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) },
            new TextBlock { Text = "未识别到文字", FontSize = DesignTokens.SectionTitle, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = "可显示原图检查清晰度，或重新截取更大的区域。", FontSize = 13, Foreground = Brush.Parse("#5F6270"), HorizontalAlignment = HorizontalAlignment.Center }
        }
    };

    private bool _recognizing;
    private void RefreshEmpty()
    {
        var empty = _picture is not null && !_recognizing && string.IsNullOrEmpty(_editor.Text) && !_sourceCard.IsVisible;
        _empty.IsVisible = empty; _editorCard.IsVisible = !empty;
        _copy.IsEnabled = !string.IsNullOrEmpty(_editor.Text);
    }

    private void SetStatus(string text, string? dot)
    {
        _status.Text = text;
        _statusDot.IsVisible = dot is not null;
        if (dot is not null) _statusDot.Background = Brush.Parse(dot);
    }

    private static int LineCount(string? text) => string.IsNullOrEmpty(text) ? 0 : OcrLineNumberPolicy.GetLogicalLineStarts(text).Count;

    private void ApplyWorkspaceLayout(double width)
    {
        var wide = width >= 720;
        var expanded = _picture is not null && (_showOriginal ?? wide);
        var sideBySide = expanded && wide;
        var stacked = expanded && !wide;
        _sourceCard.IsVisible = expanded;
        _sourceDivider.IsVisible = expanded;
        _sourceToggle.IsEnabled = _picture is not null;
        var toggleLabel = expanded ? "收起原图" : "显示原图";
        _sourceToggle.Content = toggleLabel;
        AutomationProperties.SetName(_sourceToggle, toggleLabel);
        _workspace.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        _workspace.ColumnDefinitions[1].Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _workspace.RowDefinitions[0].Height = new GridLength(stacked ? 35 : 1, GridUnitType.Star);
        _workspace.RowDefinitions[1].Height = stacked ? new GridLength(65, GridUnitType.Star) : new GridLength(0);
        // The hairline between source and editor runs along the source's right or bottom edge.
        _sourceDivider.Width = sideBySide ? 1 : double.NaN; _sourceDivider.Height = stacked ? 1 : double.NaN;
        _sourceDivider.HorizontalAlignment = sideBySide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        _sourceDivider.VerticalAlignment = stacked ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        Grid.SetColumn(_editorCard, sideBySide ? 1 : 0); Grid.SetRow(_editorCard, stacked ? 1 : 0);
        Grid.SetColumn(_empty, sideBySide ? 1 : 0); Grid.SetRow(_empty, stacked ? 1 : 0);
        RefreshEmpty();
    }

    public async void Recognize(Picture picture)
    {
        var operation = _requests.Begin();
        _original.Source = null; _picture?.Dispose(); _picture = picture;
        _original.Source = picture.Bitmap;
        _sourceLabel.Text = $"原图 · {picture.Bitmap.PixelSize.Width} × {picture.Bitmap.PixelSize.Height}";
        _recognizing = true; _recognizeAgain.IsEnabled = false;
        ApplyWorkspaceLayout(ClientSize.Width);
        _editor.Text = ""; SetStatus("识别中…", null);
        try
        {
            var result = await _controller.Platform.RecognizeAsync(picture.Png, operation.Token);
            if (!_requests.IsCurrent(operation)) return;
            _recognizing = false;
            _editor.Text = result.Text;
            if (string.IsNullOrWhiteSpace(result.Text)) SetStatus("0 行", DesignTokens.AttentionDot);
            else SetStatus($"{LineCount(result.Text)} 行 · 本机离线识别", DesignTokens.Ready);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_requests.IsCurrent(operation)) { _editor.Focus(); _editor.SelectAll(); }
            }, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (_requests.IsCurrent(operation)) SetStatus(error.Message, DesignTokens.Error); }
        finally
        {
            if (_requests.IsCurrent(operation)) { _recognizing = false; _recognizeAgain.IsEnabled = true; RefreshEmpty(); }
            _requests.Complete(operation);
        }
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
