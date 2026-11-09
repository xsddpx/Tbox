using Avalonia;
using Avalonia.Controls;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;
using Tbox.Core;

namespace Tbox.Desktop.Views;

public enum CaptureAction { Copy, Save, Pin, Ocr, Long, Record }

internal sealed class CaptureSession
{
    private readonly DesktopController _controller;
    private readonly Action<bool> _closed;
    private readonly List<CaptureWindow> _windows = [];
    private readonly ICaptureTargetSource _targets;
    private (CaptureWindow Window, Point Point, bool Replace)? _pendingDetection;
    private bool _detecting;
    public CaptureDetectionMode DetectionMode { get; private set; }
    public CaptureSessionState State { get; } = new();
    // Record or Long when the session only selects a region for that mode; null for a normal capture.
    public CaptureAction? Preset { get; }

    public CaptureSession(DesktopController controller, IReadOnlyList<CapturedDisplay> displays, Action<bool> closed,
        CaptureAction? preset = null)
    {
        _controller = controller; _closed = closed;
        Preset = preset is CaptureAction.Record or CaptureAction.Long ? preset : null;
        _targets = controller.Platform.CreateCaptureTargets();
        foreach (var display in displays)
            _windows.Add(new CaptureWindow(this, display, controller.Platform.GetDisplayTopSafeInset(display.Id)));
    }

    public void Show()
    {
        var pointer = _controller.Platform.GetPointerPosition();
        foreach (var window in _windows)
        {
            window.Show();
            var d = window.Display;
            _controller.Platform.PlaceWindow(window.TryGetPlatformHandle()!.Handle,
                new DesktopPlacement(d.Left, d.Top, d.Width, d.Height), true);
        }
        var active = _windows.FirstOrDefault(window => CaptureHistory.ContainsPointer(window.Display, pointer)) ?? _windows.FirstOrDefault();
        active?.Activate();
        if (active is not null) QueueDetection(active, new Point((pointer.X - active.Display.Left) / active.Display.Width * active.Surface.Bounds.Width,
            (pointer.Y - active.Display.Top) / active.Display.Height * active.Surface.Bounds.Height));
    }

    public void QueueDetection(CaptureWindow window, Point localPoint, bool replace = false)
    {
        if (State.IsComplete || window.Surface.IsDragging || (State.Selection is not null && !replace)) return;
        _pendingDetection = (window, localPoint, replace);
        if (!_detecting) _controller.Run(DetectAsync);
    }

    private async Task DetectAsync()
    {
        _detecting = true;
        try
        {
            while (_pendingDetection is { } request && !State.IsComplete)
            {
                _pendingDetection = null;
                var window = request.Window; var display = window.Display;
                var point = new DesktopPoint(display.Left + request.Point.X / window.Surface.Bounds.Width * display.Width,
                    display.Top + request.Point.Y / window.Surface.Bounds.Height * display.Height);
                var mode = DetectionMode;
                var revision = State.Revision;
                // Accessibility providers may be slow. Never block the input/render thread or build a request backlog.
                var target = await Task.Run(() => _targets.FindTarget(mode, point));
                if (State.IsComplete || _pendingDetection is not null || mode != DetectionMode || revision != State.Revision || window.Surface.IsDragging) continue;
                CaptureSelectionBounds? local = null;
                if (target is { } bounds && CaptureTargetPolicy.MapClippedToDisplay(
                    new CaptureDisplayBounds((int)display.Left, (int)display.Top, (int)display.Width, (int)display.Height), bounds) is { } clipped)
                    local = new CaptureSelectionBounds(clipped.Left / display.Width * window.Surface.Bounds.Width,
                        clipped.Top / display.Height * window.Surface.Bounds.Height,
                        clipped.Width / display.Width * window.Surface.Bounds.Width, clipped.Height / display.Height * window.Surface.Bounds.Height);
                if (request.Replace && State.SelectionWasDetected && !_windows.Any(peer => peer.Editor.Document.Commands.Count > 0 || peer.Editor.IsDrawing))
                    Select(display.Id, local, true);
                else if (State.Selection is null)
                {
                    foreach (var peer in _windows) peer.Surface.DetectedSelection = peer == window ? local : null;
                    window.Refresh();
                }
            }
        }
        finally { _detecting = false; }
    }

    public void ToggleDetection(CaptureWindow window)
    {
        DetectionMode = CaptureTargetPolicy.Toggle(DetectionMode);
        var enabled = DetectionMode != CaptureDetectionMode.InterfaceElement || _targets.EnableElementDetection();
        var hint = DetectionMode == CaptureDetectionMode.Window ? "窗口检测 · Tab 切换" :
            enabled ? "界面元素检测 · 不可用时回退到窗口" : "界面元素检测需要辅助功能权限；当前回退到窗口。";
        foreach (var peer in _windows) peer.SetHint(hint);
        var pointer = _controller.Platform.GetPointerPosition();
        var target = _windows.FirstOrDefault(peer => CaptureHistory.ContainsPointer(peer.Display, pointer)) ?? window;
        var local = new Point((pointer.X - target.Display.Left) / target.Display.Width * target.Surface.Bounds.Width,
            (pointer.Y - target.Display.Top) / target.Display.Height * target.Surface.Bounds.Height);
        var selected = _windows.FirstOrDefault(peer => peer.Display.Id == State.ActiveDisplay);
        var replace = State.SelectionWasDetected && selected?.Editor.Document.Commands.Count == 0;
        QueueDetection(target, local, replace);
    }

    public void RestoreLastRegion(CaptureWindow requestedFrom)
    {
        var display = _controller.CaptureHistory.FindDisplay(_windows.Select(window => window.Display));
        if (display is null) { requestedFrom.SetHint("没有可恢复的区域，或显示器布局已改变。"); return; }
        var target = _windows.Single(window => window.Display.Id == display.Id);
        foreach (var window in _windows) window.ClearEditing();
        Select(display.Id, _controller.CaptureHistory.MapToLocal(display, target.Surface.Bounds.Width, target.Surface.Bounds.Height));
        target.Activate(); target.Surface.Focus(); target.SetHint("已恢复上次截图区域");
    }

    public void Select(uint display, CaptureSelectionBounds? selection, bool detected = false)
    {
        if (State.ActiveDisplay != display)
            foreach (var window in _windows) if (window.Display.Id != display) window.ClearEditing();
        State.Select(display, selection, detected);
        foreach (var window in _windows) window.Surface.DetectedSelection = null;
        foreach (var window in _windows) window.Refresh();
    }

    public void Finish(CaptureWindow window, CaptureAction action)
    {
        window.CommitEditing();
        if (State.ActiveDisplay != window.Display.Id || State.Selection is not { } selection) return;
        var region = CaptureRegionPolicy.MapToPixels(window.Surface.Bounds.Width, window.Surface.Bounds.Height,
            selection.Left, selection.Top, selection.Width, selection.Height,
            window.Picture.Bitmap.PixelSize.Width, window.Picture.Bitmap.PixelSize.Height);
        var display = window.Display;
        var scaleX = display.Width / window.Surface.Bounds.Width;
        var scaleY = display.Height / window.Surface.Bounds.Height;
        var placement = new DesktopPlacement(display.Left + selection.Left * scaleX, display.Top + selection.Top * scaleY,
            selection.Width * scaleX, selection.Height * scaleY);
        if (action == CaptureAction.Record)
        {
            if (region.Width < 16 || region.Height < 16) { window.SetHint("录屏区域至少为 16 × 16 像素，请重新调整选区。"); return; }
            region = RecordingRegionPolicy.NormalizeForVideo(region);
            var pixelWidth = window.Picture.Bitmap.PixelSize.Width; var pixelHeight = window.Picture.Bitmap.PixelSize.Height;
            placement = new DesktopPlacement(display.Left + region.Left * display.Width / pixelWidth, display.Top + region.Top * display.Height / pixelHeight,
                region.Width * display.Width / pixelWidth, region.Height * display.Height / pixelHeight);
            if (!State.TryComplete()) return;
            Close(false);
            _controller.Run(() => _controller.StartRecordingAsync(new RecordingArea(placement, region.Width, region.Height)));
            return;
        }
        if (!State.TryComplete()) return;
        if (action == CaptureAction.Long)
        {
            Close(false);
            _controller.Run(() => _controller.StartLongCaptureAsync(placement));
            return;
        }
        var picture = action is CaptureAction.Ocr or CaptureAction.Pin ? window.Picture.Crop(region)
            : AnnotationRenderer.Render(window.Picture, region, window.Editor.Document.Commands);
        var annotations = action == CaptureAction.Pin ? window.Editor.Document.Commands
            .Select(command => AnnotationTransform.Translate(command, -region.Left, -region.Top)).ToArray() : [];
        Close(false);
        _controller.Run(() => _controller.CompleteCaptureAsync(picture, placement, action, annotations));
    }

    public void Cancel() { State.Cancel(); Close(true); }
    private void Close(bool cancelled)
    {
        foreach (var window in _windows) window.Close();
        _windows.Clear();
        _closed(cancelled);
    }
}
