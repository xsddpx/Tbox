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

internal sealed class RecordingControlWindow : Window
{
    private readonly DesktopController _controller;
    private readonly RecordingArea _area;
    private readonly RecordingOperation _operation;
    private readonly Window _border;
    private readonly Border _bar;
    private readonly TextBlock _dot = Ui.Text("●"), _clock = Ui.Text("00:00"), _status = Ui.Text("", 12);
    private readonly ComboBox _fps = new() { ItemsSource = RecordingFrameRatePolicy.AllowedFrameRates.Select(f => $"{f} FPS").ToArray(), SelectedIndex = 1, MinWidth = 90 };
    private readonly CheckBox _audio = new() { Content = "系统声音", IsChecked = false };
    private readonly Button _start, _pause, _stop;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private Task? _cancelTask;
    private bool _busy, _closing, _allowClose, _released;
    public event Action? Released;

    public RecordingControlWindow(DesktopController controller, RecordingArea area)
    {
        _controller = controller; _area = area;
        _operation = new RecordingOperation((audio, fps) => controller.Platform.CreateRecorder(area, audio, fps));
        Title = "Tbox 录屏控制"; ConfigureFloating(this);
        _border = new Window { Title = "Tbox 录屏区域", Content = new SelectionOutline(Ui.RecordRed), ShowActivated = false };
        ConfigureFloating(_border);
        _dot.Foreground = Ui.RecordRed; _dot.VerticalAlignment = _clock.VerticalAlignment = VerticalAlignment.Center;
        _start = AnnotationToolbar.IconButton(IconKind.Record, "开始录制", () => controller.Run(StartAsync), Ui.RecordRed);
        _pause = AnnotationToolbar.IconButton(IconKind.Pause, "暂停 / 继续", TogglePause);
        _stop = AnnotationToolbar.IconButton(IconKind.Stop, "停止并预览", () => controller.Run(StopAsync));
        var cancel = AnnotationToolbar.IconButton(IconKind.Close, "取消录屏 · Esc", () => controller.Run(CancelAsync));
        _bar = Ui.ToolbarSurface(new StackPanel { Spacing = 6, Children =
            { Ui.Row(_dot, _clock, _fps, _audio, Ui.ToolbarDivider(), _start, _pause, _stop, cancel), _status } }, 8);
        Content = _bar;
        _timer.Tick += (_, _) =>
        {
            _clock.Text = RecordingReviewWindow.FormatTime(_operation.Timeline.Elapsed);
            if (!_busy && !_closing && _operation.Failure is { } failure)
            { _status.Text = failure; controller.Run(StopAsync); }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { controller.Run(CancelAsync); e.Handled = true; } };
        Closing += (_, e) => { if (!_allowClose) { e.Cancel = true; controller.Run(CancelAsync); } };
        Opened += (_, _) =>
        {
            _border.Show();
            var handle = _border.TryGetPlatformHandle()!.Handle; var platform = controller.Platform;
            var inset = 4 * platform.DesktopUnitsPerDip(handle);
            platform.PlaceWindow(handle, new DesktopPlacement(area.Bounds.Left - inset, area.Bounds.Top - inset,
                area.Bounds.Width + inset * 2, area.Bounds.Height + inset * 2), false);
            platform.PrepareCaptureOverlay(handle, true);
            platform.PrepareCaptureOverlay(TryGetPlatformHandle()!.Handle, false);
            Refresh();
        };
        Refresh();
    }

    private static void ConfigureFloating(Window window)
    {
        window.SystemDecorations = SystemDecorations.None; window.CanResize = false;
        window.ShowInTaskbar = false; window.Topmost = true; window.RequestedThemeVariant = ThemeVariant.Dark;
        window.Background = Brushes.Transparent; window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
    }

    private void Refresh()
    {
        if (_closing) return;
        var state = _operation.Timeline.State;
        var ready = state == RecordingSessionState.Ready;
        _fps.IsVisible = _audio.IsVisible = _start.IsVisible = ready;
        _fps.IsEnabled = _audio.IsEnabled = _start.IsEnabled = !_busy;
        _pause.IsVisible = _stop.IsVisible = !ready;
        _pause.IsEnabled = _stop.IsEnabled = !_busy;
        _pause.Content = state == RecordingSessionState.Paused ? "▶" : "Ⅱ";
        _dot.Foreground = Brush.Parse(state == RecordingSessionState.Paused ? "#F8C53A" : "#F55353");
        if (!_busy) _status.Text = state == RecordingSessionState.Paused ? "已暂停" : _operation.Notice ?? "";
        _status.IsVisible = !string.IsNullOrEmpty(_status.Text);
        _bar.InvalidateMeasure(); _bar.Measure(Size.Infinity);
        Width = _bar.DesiredSize.Width; Height = _bar.DesiredSize.Height;
        if (TryGetPlatformHandle() is not { } handle) return;
        var platform = _controller.Platform; var work = platform.GetWorkArea(handle.Handle); var scale = platform.DesktopUnitsPerDip(handle.Handle);
        var place = PinToolbarPlacementPolicy.PlaceAdjacent(new PinWindowBounds((int)_area.Bounds.Left, (int)_area.Bounds.Top,
            (int)_area.Bounds.Width, (int)_area.Bounds.Height), new PinWindowBounds((int)work.Left, (int)work.Top, (int)work.Width, (int)work.Height),
            (int)Math.Ceiling(Width * scale), (int)Math.Ceiling(Height * scale));
        platform.PlaceWindow(handle.Handle, new DesktopPlacement(place.X, place.Y, place.Width, place.Height), false);
    }

    private async Task StartAsync()
    {
        if (_busy || _closing || _operation.Timeline.State != RecordingSessionState.Ready) return;
        _busy = true; _status.Text = "正在开始录制…"; Refresh();
        try
        {
            await _operation.StartAsync(_audio.IsChecked == true, RecordingFrameRatePolicy.AllowedFrameRates[_fps.SelectedIndex]);
            if (!_closing) _timer.Start();
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error) { if (!_closing) { _controller.Main.SetStatus(error.Message); await CancelAsync(); _controller.ShowPanel(); } }
        finally { _busy = false; Refresh(); }
    }

    private void TogglePause()
    {
        if (_busy || _closing) return;
        if (_operation.Timeline.State == RecordingSessionState.Paused) _operation.Resume(); else _operation.Pause();
        Refresh();
    }

    private async Task StopAsync()
    {
        if (_busy || _closing) return;
        _busy = true; _timer.Stop(); _status.Text = "正在完成录屏…"; Refresh();
        try
        {
            var output = await _operation.StopAsync();
            if (_closing) return;
            var workspace = _operation.TransferWorkspace();
            var duration = output.DurationSeconds > 0 ? TimeSpan.FromSeconds(output.DurationSeconds) : _operation.Timeline.Elapsed;
            CloseControls(); await _operation.DisposeAsync(); NotifyReleased();
            try { _controller.ShowRecordingReview(workspace, _area, output, duration); }
            catch { workspace.Dispose(); throw; }
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error) { _controller.Main.SetStatus(error.Message); await CancelAsync(); _controller.ShowPanel(); }
        finally { _busy = false; }
    }

    public Task CancelAsync() => _cancelTask ??= CancelCoreAsync();
    private async Task CancelCoreAsync()
    {
        if (!_closing) CloseControls();
        await _operation.DisposeAsync(); NotifyReleased();
    }
    private void CloseControls()
    {
        if (_closing) return;
        _closing = true; _timer.Stop(); _border.Close(); _allowClose = true; Close();
    }
    private void NotifyReleased() { if (!_released) { _released = true; Released?.Invoke(); } }
}
