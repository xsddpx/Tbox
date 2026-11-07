using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Views;

internal sealed class LongCaptureSession
{
    private readonly DesktopController _controller;
    private readonly DesktopPlacement _region;
    private readonly Action _closed;
    private readonly CancellationTokenSource _stop = new();
    private readonly Window _border;
    private readonly Window _toolbar;
    private readonly Window _preview;
    private readonly LongPreview _surface = new();
    private readonly TextBlock _size = new() { Text = "…", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Button> _actions = [];
    private Task _loop = Task.CompletedTask;
    private LongCaptureBuffer? _buffer;
    private Picture? _previewPicture;
    private bool _finished;

    public LongCaptureSession(DesktopController controller, DesktopPlacement region, Action closed)
    {
        _controller = controller; _region = region; _closed = closed;
        _border = Floating("Tbox 长截图区域", new SelectionOutline(Brush.Parse("#F8C53A")));
        _border.Background = Brushes.Transparent; _border.ShowActivated = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttons.Children.Add(_size);
        Add(buttons, "⌖", "固定为贴图", () => FinishAsync(CaptureAction.Pin));
        Add(buttons, "↓", "保存图片", () => FinishAsync(CaptureAction.Save));
        Add(buttons, "⛶", "全屏预览", () => FinishAsync(null));
        var cancel = AnnotationToolbar.IconButton("×", "取消长截图 · Esc", Stop);
        buttons.Children.Add(cancel);
        Add(buttons, "⧉", "复制图片", () => FinishAsync(CaptureAction.Copy));
        var bar = new Border { Background = Ui.ToolbarBackground, Padding = new Thickness(8), CornerRadius = new CornerRadius(8), Child = buttons };
        bar.Measure(Size.Infinity);
        _toolbar = Floating("Tbox 长截图工具条", bar);
        _toolbar.Width = Math.Max(300, bar.DesiredSize.Width); _toolbar.Height = bar.DesiredSize.Height;
        _toolbar.KeyDown += (_, e) => { if (e.Key == Key.Escape) { Stop(); e.Handled = true; } };
        _preview = Floating("Tbox 长截图实时预览", new Border
        {
            Background = Brush.Parse("#242424"), BorderBrush = Brush.Parse("#666666"), BorderThickness = new Thickness(1),
            Padding = new Thickness(6), Child = _surface
        });
        _preview.Width = 240; _preview.Height = 480; _preview.ShowActivated = false;
        _preview.Closed += (_, _) => Stop();
    }

    public void Start()
    {
        _border.Show();
        var platform = _controller.Platform;
        var borderHandle = _border.TryGetPlatformHandle()!.Handle;
        var scale = platform.DesktopUnitsPerDip(borderHandle); var margin = 4 * scale;
        platform.PlaceWindow(borderHandle, new DesktopPlacement(_region.Left - margin, _region.Top - margin,
            _region.Width + margin * 2, _region.Height + margin * 2), false);
        platform.PrepareCaptureOverlay(borderHandle, true);
        _toolbar.Show();
        var work = platform.GetWorkArea(borderHandle);
        var placement = PinToolbarPlacementPolicy.PlaceAdjacent(new PinWindowBounds((int)_region.Left, (int)_region.Top, (int)_region.Width, (int)_region.Height),
            new PinWindowBounds((int)work.Left, (int)work.Top, (int)work.Width, (int)work.Height),
            (int)Math.Ceiling(_toolbar.Width * scale), (int)Math.Ceiling(_toolbar.Height * scale));
        platform.PlaceWindow(_toolbar.TryGetPlatformHandle()!.Handle,
            new DesktopPlacement(placement.X, placement.Y, placement.Width, placement.Height), false);
        platform.PrepareCaptureOverlay(_toolbar.TryGetPlatformHandle()!.Handle, false);
        _preview.Show();
        var width = Math.Min(240 * scale, work.Width); var height = Math.Min(600 * scale, work.Height);
        var left = _region.Left + _region.Width + 12 * scale;
        if (left + width > work.Left + work.Width) left = _region.Left - width - 12 * scale;
        left = Math.Clamp(left, work.Left, work.Left + work.Width - width);
        var top = Math.Clamp(_region.Top, work.Top, work.Top + work.Height - height);
        platform.PlaceWindow(_preview.TryGetPlatformHandle()!.Handle, new DesktopPlacement(left, top, width, height), false);
        platform.PrepareCaptureOverlay(_preview.TryGetPlatformHandle()!.Handle, false);
        _loop = CaptureLoopAsync();
    }

    private async Task CaptureLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var png = await _controller.Platform.CaptureRegionAsync(_region, _stop.Token);
                using var frame = new Picture(png);
                _buffer ??= new LongCaptureBuffer(frame.Bitmap.PixelSize.Width, frame.Bitmap.PixelSize.Height);
                if (frame.Bitmap.PixelSize.Width != _buffer.Width || frame.Bitmap.PixelSize.Height != _buffer.FrameHeight)
                    throw new InvalidOperationException("捕获尺寸已变化，可保存当前结果后重新框选。");
                var pixels = frame.GetPixels();
                var result = await Task.Run(() => _buffer.Add(pixels), _stop.Token);
                if (_finished) return;
                if (result is { Decision: ScrollCaptureFrameDecision.Started or ScrollCaptureFrameDecision.Appended or ScrollCaptureFrameDecision.Moved or ScrollCaptureFrameDecision.LimitReached })
                {
                    var next = Picture.FromPixels(_buffer.Complete(), _buffer.Width, _buffer.Height);
                    var old = _previewPicture; _previewPicture = next;
                    _surface.Picture = next; _surface.ViewportTop = _buffer.ViewportTop; _surface.ViewportHeight = _buffer.FrameHeight;
                    _surface.InvalidateVisual(); old?.Dispose();
                    _size.Text = $"{_buffer.Width} × {_buffer.Height}";
                    foreach (var button in _actions) button.IsEnabled = true;
                }
                if (result is { Decision: ScrollCaptureFrameDecision.LimitReached })
                {
                    Dispatcher.UIThread.Post(() => _controller.Run(() => FinishAsync(null))); break;
                }
                await Task.Delay(180, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_finished) _size.Text = "采集已停止";
            _controller.Main.SetStatus(error.Message);
            ToolTip.SetTip(_size, error.Message);
        }
    }

    private async Task FinishAsync(CaptureAction? action)
    {
        if (_finished || _buffer?.HasFrame != true) return;
        _finished = true; _stop.Cancel(); await _loop;
        using var picture = Picture.FromPixels(_buffer.Complete(), _buffer.Width, _buffer.Height);
        CloseWindows();
        if (action is { } requested) await _controller.CompleteCaptureAsync(picture, _region, requested);
        else
        {
            _controller.CaptureHistory.Remember(_region);
            _controller.ShowImageResult(new Picture(picture.Png), true);
        }
    }

    public void Stop()
    {
        if (_finished) return;
        _finished = true; _stop.Cancel(); CloseWindows();
    }

    private void CloseWindows()
    {
        _border.Close(); _toolbar.Close(); _preview.Close();
        _surface.Picture = null; _previewPicture?.Dispose(); _previewPicture = null;
        _closed();
        _ = _loop.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }

    private void Add(StackPanel panel, string icon, string label, Func<Task> action)
    {
        var button = AnnotationToolbar.IconButton(icon, label, () => _controller.Run(action));
        button.IsEnabled = false; _actions.Add(button); panel.Children.Add(button);
    }

    private static Window Floating(string title, Control content) => new()
    {
        Title = title, Content = content, SystemDecorations = SystemDecorations.None, CanResize = false,
        ShowInTaskbar = false, Topmost = true, RequestedThemeVariant = ThemeVariant.Dark,
        Background = Brushes.Transparent, TransparencyLevelHint = [WindowTransparencyLevel.Transparent]
    };
}

internal sealed class LongPreview : Control
{
    public Picture? Picture { get; set; }
    public int ViewportTop { get; set; }
    public int ViewportHeight { get; set; }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Picture is not { } picture) return;
        var ratio = Math.Min(Bounds.Width / picture.Bitmap.PixelSize.Width, Bounds.Height / picture.Bitmap.PixelSize.Height);
        var size = new Size(picture.Bitmap.PixelSize.Width * ratio, picture.Bitmap.PixelSize.Height * ratio);
        var left = (Bounds.Width - size.Width) / 2;
        context.DrawImage(picture.Bitmap, new Rect(left, 0, size.Width, size.Height));
        context.DrawRectangle(null, new Pen(Brush.Parse("#48D597"), 2),
            new Rect(left, ViewportTop * ratio, size.Width, Math.Min(ViewportHeight * ratio, size.Height)));
    }
}
