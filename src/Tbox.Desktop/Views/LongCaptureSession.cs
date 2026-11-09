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
    private readonly TextBlock _size = Ui.ToolbarLabel("…");
    private readonly TextBlock _progress = new() { Text = "向下滚动开始拼接", FontSize = 11, FontWeight = FontWeight.SemiBold,
        Foreground = Brush.Parse(DesignTokens.TaskSecondaryText), Margin = new Thickness(4, 8, 4, 2) };
    private int _frames;
    private readonly List<Button> _actions = [];
    private Task _loop = Task.CompletedTask;
    private LongCaptureBuffer? _buffer;
    private Picture? _previewPicture;
    private bool _finished;

    public LongCaptureSession(DesktopController controller, DesktopPlacement region, Action closed)
    {
        _controller = controller; _region = region; _closed = closed;
        // Yellow selection; the area is fixed once scrolling starts, so no resize handles.
        _border = Floating("Tbox 长截图区域", new SelectionOutline(Ui.LongYellow, handles: false));
        _border.Background = Brushes.Transparent; _border.ShowActivated = false;
        // Size | pin, full-screen preview, save, copy | cancel (spec ⑤).
        var bar = Ui.Toolbar([_size],
            [Action(IconKind.Pin, $"贴图 · {ShortcutLabels.Command}T", CaptureAction.Pin), Action(IconKind.FullScreen, "全屏预览", null),
             Action(IconKind.Save, $"保存 · {ShortcutLabels.Command}S", CaptureAction.Save), Action(IconKind.Copy, $"复制 · {ShortcutLabels.Command}C / Enter", CaptureAction.Copy)],
            [AnnotationToolbar.IconButton(IconKind.Close, "取消 · Esc", Stop)]);
        _size.MinWidth = 76; _size.TextAlignment = TextAlignment.Center;
        bar.Measure(Size.Infinity);
        _toolbar = Floating("Tbox 长截图工具条", bar);
        _toolbar.Width = bar.DesiredSize.Width; _toolbar.Height = bar.DesiredSize.Height;
        _toolbar.KeyDown += (_, e) =>
        {
            var command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            if (e.Key == Key.Escape) Stop();
            else if (e.Key == Key.Enter || command && e.Key == Key.C) _controller.Run(() => FinishAsync(CaptureAction.Copy));
            else if (command && e.Key == Key.S) _controller.Run(() => FinishAsync(CaptureAction.Save));
            else if (command && e.Key == Key.T) _controller.Run(() => FinishAsync(CaptureAction.Pin));
            else return;
            e.Handled = true;
        };
        var frame = new Border { CornerRadius = new CornerRadius(6), Background = Brushes.White, ClipToBounds = true, Child = _surface };
        var preview = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { frame, _progress } };
        Grid.SetRow(_progress, 1);
        _preview = Floating("Tbox 长截图实时预览", new Border
        {
            Background = Ui.ToolbarBackground, BorderBrush = Ui.ToolbarBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(DesignTokens.TaskRadius), Padding = new Thickness(6), Child = preview
        });
        _preview.Width = 180; _preview.Height = 360; _preview.ShowActivated = false;
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
        var placement = PinToolbarPlacementPolicy.PlaceTaskToolbar(new PinWindowBounds((int)_region.Left, (int)_region.Top, (int)_region.Width, (int)_region.Height),
            new PinWindowBounds((int)work.Left, (int)work.Top, (int)work.Width, (int)work.Height),
            (int)Math.Ceiling(_toolbar.Width * scale), (int)Math.Ceiling(_toolbar.Height * scale));
        platform.PlaceWindow(_toolbar.TryGetPlatformHandle()!.Handle,
            new DesktopPlacement(placement.X, placement.Y, placement.Width, placement.Height), false);
        platform.PrepareCaptureOverlay(_toolbar.TryGetPlatformHandle()!.Handle, false);
        _preview.Show();
        var width = Math.Min(180 * scale, work.Width); var height = Math.Min(Math.Max(300 * scale, _region.Height), work.Height);
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
                    if (result is { Decision: ScrollCaptureFrameDecision.Started or ScrollCaptureFrameDecision.Appended }) _frames++;
                    _progress.Text = result is { Decision: ScrollCaptureFrameDecision.LimitReached } ? $"已拼接 {_frames} 屏 · 已达上限" : $"已拼接 {_frames} 屏 · 继续滚动";
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

    // Outputs stay disabled until the first frame arrives; null opens the full-screen preview.
    private Button Action(IconKind icon, string label, CaptureAction? action)
    {
        var button = AnnotationToolbar.IconButton(icon, label, () => _controller.Run(() => FinishAsync(action)));
        button.IsEnabled = false; _actions.Add(button);
        return button;
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
        context.DrawRectangle(null, new Pen(Ui.LongYellow, 2),
            new Rect(left, ViewportTop * ratio, size.Width, Math.Min(ViewportHeight * ratio, size.Height)));
    }
}
