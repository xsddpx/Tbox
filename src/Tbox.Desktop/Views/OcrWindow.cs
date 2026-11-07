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
using Tbox.Desktop.Platform;

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
    private readonly Image _original = new() { Stretch = Stretch.Uniform, IsVisible = false };
    private readonly LatestOperation _requests = new();
    private Picture? _picture;
    internal RecognitionResult? Recognition { get; private set; }

    public OcrWindow(DesktopController controller)
    {
        _controller = controller;
        Ui.Configure(this, "文字识别 · Tbox", 800, 600);
        MinWidth = 460; MinHeight = 360;
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,0,65*,Auto"), Margin = new Thickness(24) };
        var expand = new CheckBox { Content = "显示原图" };
        expand.IsCheckedChanged += (_, _) =>
        {
            var expanded = expand.IsChecked == true;
            _original.IsVisible = expanded;
            layout.RowDefinitions[1].Height = expanded ? new GridLength(35, GridUnitType.Star) : new GridLength(0);
        };
        var topmost = new CheckBox { Content = "置顶" };
        topmost.IsCheckedChanged += (_, _) => Topmost = topmost.IsChecked == true;
        Activated += (_, _) => topmost.IsChecked = Topmost;
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
        Closed += (_, _) => { _requests.Dispose(); _original.Source = null; _picture?.Dispose(); _picture = null; Recognition = null; };
    }

    internal void Present(bool topmost, OcrWindowSource? source)
    {
        Topmost = topmost;
        var maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Minimized || (maximized && source is not null)) WindowState = WindowState.Normal;
        if (source is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            PlaceAtSource(source);
        }
        Show();
        // Decorations and desktop scaling are authoritative once the native
        // window is shown on the source display. Include the title bar in bounds.
        if (source is not null) PlaceAtSource(source);
        if (maximized) WindowState = WindowState.Maximized;
        Activate();
    }

    private void PlaceAtSource(OcrWindowSource source)
    {
        var frame = FrameSize ?? ClientSize;
        var decoration = new Size(Math.Max(0, frame.Width - ClientSize.Width), Math.Max(0, frame.Height - ClientSize.Height));
        var placement = source.Fit(new Size(Width, Height), decoration);
        MinWidth = Math.Min(460, placement.ClientSize.Width); MinHeight = Math.Min(360, placement.ClientSize.Height);
        Width = placement.ClientSize.Width; Height = placement.ClientSize.Height;
        Position = placement.Position;
    }

    public async void Recognize(Picture picture)
    {
        var operation = _requests.Begin();
        Recognition = null;
        _original.Source = null; _picture?.Dispose(); _picture = picture;
        _original.Source = picture.Bitmap;
        _editor.Text = ""; _status.Text = "识别中…";
        try
        {
            var result = await _controller.Platform.RecognizeAsync(picture.Png, operation.Token);
            if (!_requests.IsCurrent(operation)) return;
            Recognition = result;
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
