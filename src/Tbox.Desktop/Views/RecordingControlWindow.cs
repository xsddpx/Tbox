using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
    private readonly SelectionOutline _outline = new(Ui.RecordRed, handles: false);
    internal SelectionOutline Outline => _outline;
    private readonly Border _bar;
    // Timer with a red dot (spec ⑤); the dot turns grey while paused.
    private readonly Ellipse _dot = new() { Width = 9, Height = 9, Fill = Ui.RecordRed, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _clock = new() { Text = "00:00", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Ui.ToolbarText, MinWidth = 40,
        VerticalAlignment = VerticalAlignment.Center };
    private readonly Control _timerGroup, _fps;
    private readonly Func<int> _fpsIndex;
    private readonly Action<bool> _enableFps;
    private readonly CheckBox _audio = new() { Content = "系统声音", IsChecked = false, FontSize = 13, Foreground = Ui.ToolbarText, MinHeight = 0,
        Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _start, _pause, _stop;
    private readonly Control[] _prepare, _recording;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private Task? _cancelTask;
    private bool _busy, _closing, _allowClose, _released;
    public event Action? Released;

    public RecordingControlWindow(DesktopController controller, RecordingArea area)
    {
        _controller = controller; _area = area;
        _operation = new RecordingOperation((audio, fps) => controller.Platform.CreateRecorder(area, audio, fps));
        Title = "Tbox 录屏控制"; ConfigureFloating(this);
        _border = new Window { Title = "Tbox 录屏区域", Content = _outline, ShowActivated = false };
        ConfigureFloating(_border);
        foreach (var state in new[] { "", "PointerOver", "Pressed" }) _audio.Resources["CheckBoxForegroundUnchecked" + state] = _audio.Resources["CheckBoxForegroundChecked" + state] = Ui.ToolbarText;
        var rates = RecordingFrameRatePolicy.AllowedFrameRates;
        var fps = Ui.ToolbarSegmented(rates.Select(r => r.ToString()).ToArray(), Math.Max(0, rates.ToList().IndexOf(RecordingFrameRatePolicy.DefaultFrameRate)), out _fpsIndex, out _enableFps);
        _fps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { Ui.ToolbarLabel("帧率", strong: false), fps } };
        _start = Ui.RecordButton("开始录制 · Enter", () => controller.Run(StartAsync));
        _pause = AnnotationToolbar.IconButton(IconKind.Pause, "暂停 · Space", TogglePause);
        _stop = AnnotationToolbar.IconButton(IconKind.Stop, "停止并预览 · Enter", () => controller.Run(StopAsync));
        var cancel = AnnotationToolbar.IconButton(IconKind.Close, "取消 · Esc", () => controller.Run(CancelAsync));
        _timerGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 0), Children = { _dot, _clock } };
        // Prepare: 帧率 | 系统声音 | 开始录制, close. Recording: timer | pause, stop | close (spec ⑤).
        var prepareDivider = Ui.ToolbarDivider(); var recordDivider = Ui.ToolbarDivider(); var closeDivider = Ui.ToolbarDivider();
        _prepare = [_fps, prepareDivider, _audio, recordDivider, _start];
        _recording = [_timerGroup, Ui.ToolbarDivider(), _pause, _stop, closeDivider];
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var item in _prepare.Concat(_recording).Append(cancel)) { item.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(item); }
        _bar = Ui.ToolbarSurface(row);
        Content = _bar;
        _timer.Tick += (_, _) =>
        {
            _clock.Text = RecordingReviewWindow.FormatTime(_operation.Timeline.Elapsed);
            if (!_busy && !_closing && _operation.Failure is { } failure)
            { ToolTip.SetTip(_timerGroup, failure); controller.Run(StopAsync); }
        };
        KeyDown += (_, e) =>
        {
            var ready = _operation.Timeline.State == RecordingSessionState.Ready;
            if (e.Key == Key.Escape) controller.Run(CancelAsync);
            else if (e.Key == Key.Enter) controller.Run(ready ? StartAsync : StopAsync);
            else if (e.Key == Key.Space && !ready) TogglePause();
            else return;
            e.Handled = true;
        };
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
        var paused = state == RecordingSessionState.Paused;
        foreach (var item in _prepare) item.IsVisible = ready;
        foreach (var item in _recording) item.IsVisible = !ready;
        _enableFps(!_busy); _audio.IsEnabled = _start.IsEnabled = !_busy;
        _pause.IsEnabled = _stop.IsEnabled = !_busy;
        // Pause and resume share one button; its icon and tooltip follow the state.
        _pause.Content = new LineIcon(paused ? IconKind.Play : IconKind.Pause, brush: Ui.ToolbarText) { Width = 18, Height = 18 };
        var pauseName = paused ? "继续 · Space" : "暂停 · Space";
        ToolTip.SetTip(_pause, pauseName); AutomationProperties.SetName(_pause, pauseName);
        _dot.Fill = paused ? Brush.Parse(DesignTokens.TaskSecondaryText) : Ui.RecordRed;
        // Once recording starts the area can no longer be adjusted, so the outline turns dashed.
        _outline.Dashed = !ready;
        ToolTip.SetTip(_timerGroup, _busy ? null : paused ? "已暂停" : _operation.Notice);
        _bar.InvalidateMeasure(); _bar.Measure(Size.Infinity);
        Width = _bar.DesiredSize.Width; Height = _bar.DesiredSize.Height;
        if (TryGetPlatformHandle() is not { } handle) return;
        var platform = _controller.Platform; var work = platform.GetWorkArea(handle.Handle); var scale = platform.DesktopUnitsPerDip(handle.Handle);
        var place = PinToolbarPlacementPolicy.PlaceTaskToolbar(new PinWindowBounds((int)_area.Bounds.Left, (int)_area.Bounds.Top,
            (int)_area.Bounds.Width, (int)_area.Bounds.Height), new PinWindowBounds((int)work.Left, (int)work.Top, (int)work.Width, (int)work.Height),
            (int)Math.Ceiling(Width * scale), (int)Math.Ceiling(Height * scale));
        platform.PlaceWindow(handle.Handle, new DesktopPlacement(place.X, place.Y, place.Width, place.Height), false);
    }

    private async Task StartAsync()
    {
        if (_busy || _closing || _operation.Timeline.State != RecordingSessionState.Ready) return;
        _busy = true; Refresh();
        try
        {
            await _operation.StartAsync(_audio.IsChecked == true, RecordingFrameRatePolicy.AllowedFrameRates[_fpsIndex()]);
            if (!_closing) _timer.Start();
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error) { if (!_closing) { _controller.Main.SetStatus(error.Message); await CancelAsync(); _controller.ShowMain(); } }
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
        _busy = true; _timer.Stop(); Refresh();
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
        catch (Exception error) { _controller.Main.SetStatus(error.Message); await CancelAsync(); _controller.ShowMain(); }
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
