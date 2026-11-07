using System.Globalization;
using Avalonia;
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
    private readonly TextBlock _status = Ui.Text("", 12);
    private readonly Image _original = new() { Stretch = Stretch.Uniform, Height = 180, IsVisible = false };
    private readonly LatestOperation _requests = new();
    private Picture? _picture;

    public OcrWindow(DesktopController controller)
    {
        _controller = controller;
        Ui.Configure(this, "文字识别 · Tbox", 800, 600);
        MinWidth = 460; MinHeight = 360;
        var expand = new CheckBox { Content = "显示原图" };
        expand.IsCheckedChanged += (_, _) => _original.IsVisible = expand.IsChecked == true;
        var topmost = new CheckBox { Content = "置顶" };
        topmost.IsCheckedChanged += (_, _) => Topmost = topmost.IsChecked == true;
        Activated += (_, _) => topmost.IsChecked = Topmost;
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(24) };
        var title = Ui.Text("文字识别", DesignTokens.PageTitle); title.FontWeight = FontWeight.SemiBold;
        var header = new DockPanel();
        var options = Ui.Row(expand, topmost); DockPanel.SetDock(options, Dock.Right);
        header.Children.Add(options); header.Children.Add(title);
        header.Margin = new Thickness(0, 0, 0, 12);
        layout.Children.Add(header);
        Grid.SetRow(_original, 1); layout.Children.Add(_original);
        var workspace = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*"), Margin = new Thickness(0, 12) };
        workspace.Children.Add(new LogicalLineGutter(_editor));
        Grid.SetColumn(_editor, 1); workspace.Children.Add(_editor);
        var editorCard = Ui.Card(workspace); editorCard.Padding = new Thickness(8); editorCard.Margin = new Thickness(0, 12);
        Grid.SetRow(editorCard, 2); layout.Children.Add(editorCard);
        var actions = new DockPanel { LastChildFill = true };
        var copy = Ui.Button("复制全部", () => controller.Run(async () =>
        {
            await (Clipboard ?? throw new InvalidOperationException("系统剪贴板不可用。")).SetTextAsync(_editor.Text ?? "");
            _status.Text = "已复制当前全文。";
        }));
        copy.Classes.Add("accent"); DockPanel.SetDock(copy, Dock.Right); actions.Children.Add(copy);
        actions.Children.Add(_status); Grid.SetRow(actions, 3); layout.Children.Add(actions);
        Content = layout;
        Closed += (_, _) => { _requests.Dispose(); _original.Source = null; _picture?.Dispose(); };
    }

    public async void Recognize(Picture picture)
    {
        var operation = _requests.Begin();
        _original.Source = null; _picture?.Dispose(); _picture = picture;
        _original.Source = picture.Bitmap;
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
                FlowDirection.LeftToRight, new Typeface(_editor.FontFamily), 14, Brushes.Gray);
            context.DrawText(text, new Point(Math.Max(0, Bounds.Width - text.Width - 8), y));
        }
    }
}
