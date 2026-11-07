using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Interactivity;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Views;

internal sealed class CaptureWindow : Window
{
    private readonly CaptureSession _session;
    private readonly AnnotationToolbar _toolbar;
    private readonly AnnotationTextEditor _text;
    private bool _toolbarHidden;
    private readonly TextBlock _hint = new() { Foreground = Brushes.White, Margin = new Thickness(20, 10), IsHitTestVisible = false };
    public CapturedDisplay Display { get; }
    public Picture Picture { get; }
    public CaptureSurface Surface { get; }
    public AnnotationEditor Editor { get; } = new();

    public CaptureWindow(CaptureSession session, CapturedDisplay display)
    {
        _session = session; Display = display; Picture = new Picture(display.Png);
        Title = "Tbox 截图";
        SystemDecorations = SystemDecorations.None;
        CanResize = false; ShowInTaskbar = false; Topmost = true;
        RequestedThemeVariant = ThemeVariant.Dark;
        Width = display.Width; Height = display.Height;
        Surface = new CaptureSurface(session, this);
        var overlay = new Canvas();
        _text = new AnnotationTextEditor(overlay, Editor,
            point => new Point(point.X * Scale.X, point.Y * Scale.Y), () => Scale);
        _toolbar = new AnnotationToolbar(Editor, _text.Commit) { IsVisible = false };
        _toolbar.Actions.Children.Add(AnnotationToolbar.IconButton("⌖", $"固定为贴图 · {ShortcutLabels.Command}T / 鼠标中键", () => session.Finish(this, CaptureAction.Pin)));
        _toolbar.Actions.Children.Add(AnnotationToolbar.IconButton("Aa", "识别文字", () => session.Finish(this, CaptureAction.Ocr)));
        _toolbar.Actions.Children.Add(AnnotationToolbar.IconButton("↓", $"保存图片 · {ShortcutLabels.Command}S", () => session.Finish(this, CaptureAction.Save)));
        _toolbar.Actions.Children.Add(AnnotationToolbar.IconButton("⧉", $"复制图片 · {ShortcutLabels.Command}C / Enter", () => session.Finish(this, CaptureAction.Copy)));
        _toolbar.Actions.Children.Add(AnnotationToolbar.IconButton("×", "取消截图 · Esc", session.Cancel));
        overlay.Children.Add(_toolbar);
        overlay.Children.Add(_hint);
        Content = new Grid { Children = { Surface, overlay } };
        Editor.Changed += () => Surface.InvalidateVisual();
        KeyDown += OnKeyDown;
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key != Key.Tab || _text.IsEditing) return;
            _session.ToggleDetection(this); args.Handled = true;
        }, RoutingStrategies.Tunnel);
        Closed += (_, _) => Picture.Dispose();
        Opened += (_, _) => Surface.Focus();
    }

    public void Refresh()
    {
        Surface.InvalidateVisual();
        var bounds = _session.State.ActiveDisplay == Display.Id ? _session.State.Selection : null;
        _toolbar.IsVisible = bounds is { Width: > 0, Height: > 0 } && !Surface.IsDragging && !_toolbarHidden;
        if (bounds is not { } selection) return;
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _toolbar.DesiredSize;
        Canvas.SetLeft(_toolbar, Math.Clamp(selection.Right - size.Width, 0, Math.Max(0, Width - size.Width)));
        var y = selection.Bottom + 8;
        if (y + size.Height > Height) y = selection.Top - size.Height - 8;
        Canvas.SetTop(_toolbar, Math.Clamp(y, 0, Math.Max(0, Height - size.Height)));
    }

    public void SetHint(string message) => _hint.Text = message;

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (_text.IsEditing) return;
        if (args.Key == Key.Escape)
        {
            if (Editor.IsDrawing) Editor.CancelGesture();
            else if (Editor.Tool is not null) Editor.Select(null);
            else _session.Cancel();
            args.Handled = true;
        }
        else if (args.Key == Key.Enter) { _session.Finish(this, CaptureAction.Copy); args.Handled = true; }
        else if ((args.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0)
        {
            if (args.Key == Key.C) _session.Finish(this, CaptureAction.Copy);
            else if (args.Key == Key.S) _session.Finish(this, CaptureAction.Save);
            else if (args.Key == Key.T) _session.Finish(this, CaptureAction.Pin);
            else if (args.Key == Key.Z) { if (args.KeyModifiers.HasFlag(KeyModifiers.Shift)) Editor.Redo(); else Editor.Undo(); }
            else if (args.Key == Key.Y) Editor.Redo();
            else return;
            args.Handled = true;
        }
        else if (args.Key == Key.Space) { _toolbarHidden = !_toolbarHidden; Refresh(); args.Handled = true; }
        else if (args.Key == Key.R) { _session.RestoreLastRegion(this); args.Handled = true; }
        else if (args.Key == Key.B) { Editor.Select(AnnotationTool.Pen); args.Handled = true; }
        else if (args.Key == Key.T) { Editor.Select(AnnotationTool.Text); args.Handled = true; }
        else if (args.Key is Key.D1 or Key.D2) { Editor.ChangeSize(args.Key == Key.D1 ? -1 : 1); args.Handled = true; }
    }

    public Vector Scale => new(Surface.Bounds.Width / Picture.Bitmap.PixelSize.Width, Surface.Bounds.Height / Picture.Bitmap.PixelSize.Height);
    public void CommitEditing() { _text.Commit(); Editor.FinishPolyline(); Editor.CancelGesture(); }
    public void ClearEditing() { _text.Cancel(); Editor.Reset(); }
}

internal sealed class CaptureSurface : Control
{
    private readonly CaptureSession _session;
    private readonly CaptureWindow _window;
    private Point _start;
    private CaptureSelectionHandle _handle;
    private CaptureSelectionBounds _original;
    private CaptureSelectionBounds? _clickTarget;
    private CaptureSelectionBounds? _detectedSelection;
    public CaptureSelectionBounds? DetectedSelection
    {
        get => _detectedSelection;
        set { _detectedSelection = value; InvalidateVisual(); }
    }
    public bool IsDragging { get; private set; }

    public CaptureSurface(CaptureSession session, CaptureWindow window)
    {
        _session = session; _window = window;
        Focusable = true; Cursor = new Cursor(StandardCursorType.Cross);
    }

    private CaptureSelectionBounds? Selection => _session.State.ActiveDisplay == _window.Display.Id ? _session.State.Selection : null;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = new Rect(Bounds.Size);
        context.DrawImage(_window.Picture.Bitmap, size);
        var shade = new SolidColorBrush(Color.FromArgb(115, 0, 0, 0));
        if ((Selection ?? DetectedSelection) is not { Width: > 0, Height: > 0 } selection)
        {
            context.FillRectangle(shade, size);
            var text = new FormattedText("拖动选择截图区域  ·  Esc 取消", CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, Typeface.Default, 16, Brushes.White);
            context.DrawText(text, new Point(Math.Max(24, (Bounds.Width - text.Width) / 2), 40));
            return;
        }
        var r = new Rect(selection.Left, selection.Top, selection.Width, selection.Height);
        using (context.PushClip(r))
        using (context.PushTransform(Matrix.CreateScale(_window.Scale.X, _window.Scale.Y)))
        {
            AnnotationRenderer.Draw(context, _window.Picture, _window.Editor.Document.Commands);
            if (_window.Editor.Draft is { } draft) AnnotationRenderer.Draw(context, _window.Picture, [draft]);
        }
        context.FillRectangle(shade, new Rect(0, 0, Bounds.Width, r.Top));
        context.FillRectangle(shade, new Rect(0, r.Bottom, Bounds.Width, Math.Max(0, Bounds.Height - r.Bottom)));
        context.FillRectangle(shade, new Rect(0, r.Top, r.Left, r.Height));
        context.FillRectangle(shade, new Rect(r.Right, r.Top, Math.Max(0, Bounds.Width - r.Right), r.Height));
        var pen = new Pen(Ui.Blue, 1);
        context.DrawRectangle(null, pen, r);
        if (Selection is not null) foreach (var p in new[] { r.TopLeft, new Point(r.Center.X, r.Top), r.TopRight, new Point(r.Right, r.Center.Y),
                     r.BottomRight, new Point(r.Center.X, r.Bottom), r.BottomLeft, new Point(r.Left, r.Center.Y) })
            context.DrawRectangle(Brushes.White, pen, new Rect(p.X - 3, p.Y - 3, 6, 6));
        var region = CaptureRegionPolicy.MapToPixels(Bounds.Width, Bounds.Height, r.X, r.Y, r.Width, r.Height,
            _window.Picture.Bitmap.PixelSize.Width, _window.Picture.Bitmap.PixelSize.Height);
        var label = new FormattedText($"{region.Width} × {region.Height}", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
        var labelPoint = new Point(r.Left, r.Top >= 28 ? r.Top - 26 : r.Top + 8);
        context.FillRectangle(Ui.ToolbarBackground, new Rect(labelPoint.X, labelPoint.Y, label.Width + 12, 22));
        context.DrawText(label, labelPoint + new Vector(6, 3));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsMiddleButtonPressed) { _session.Finish(_window, CaptureAction.Pin); return; }
        if (!properties.IsLeftButtonPressed) return;
        Focus(); _window.Activate();
        _start = Clamp(e.GetPosition(this));
        _clickTarget = Selection is null ? DetectedSelection : null;
        _handle = Selection is { } old ? CaptureSelectionAdjustmentPolicy.HitTest(old, _start.X, _start.Y, 7) : CaptureSelectionHandle.None;
        _original = Selection ?? default;
        if (_handle == CaptureSelectionHandle.None && Selection is { } selected &&
            new Rect(selected.Left, selected.Top, selected.Width, selected.Height).Contains(_start) &&
            _window.Editor.Begin(PixelPoint(_start), e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.ClickCount > 1))
        {
            if (_window.Editor.Tool != AnnotationTool.Text) e.Pointer.Capture(this);
            e.Handled = true; return;
        }
        if (_handle == CaptureSelectionHandle.None) _window.ClearEditing();
        IsDragging = true;
        if (_handle == CaptureSelectionHandle.None) _session.Select(_window.Display.Id, null);
        e.Pointer.Capture(this); e.Handled = true;
        _window.Refresh();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = Clamp(e.GetPosition(this));
        if (_window.Editor.IsDrawing)
        {
            _window.Editor.Move(PixelPoint(point), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            return;
        }
        if (!IsDragging)
        {
            _session.QueueDetection(_window, point);
            var handle = Selection is { } bounds ? CaptureSelectionAdjustmentPolicy.HitTest(bounds, point.X, point.Y, 7) : CaptureSelectionHandle.None;
            Cursor = new Cursor(handle switch
            {
                CaptureSelectionHandle.Left or CaptureSelectionHandle.Right => StandardCursorType.SizeWestEast,
                CaptureSelectionHandle.Top or CaptureSelectionHandle.Bottom => StandardCursorType.SizeNorthSouth,
                CaptureSelectionHandle.TopLeft or CaptureSelectionHandle.BottomRight => StandardCursorType.TopLeftCorner,
                CaptureSelectionHandle.TopRight or CaptureSelectionHandle.BottomLeft => StandardCursorType.TopRightCorner,
                _ => StandardCursorType.Cross
            });
            return;
        }
        var selection = _handle != CaptureSelectionHandle.None
            ? CaptureSelectionAdjustmentPolicy.Resize(_original, _handle, point.X, point.Y, Bounds.Width, Bounds.Height, 1, 1)
            : new CaptureSelectionBounds(Math.Min(_start.X, point.X), Math.Min(_start.Y, point.Y), Math.Abs(point.X - _start.X), Math.Abs(point.Y - _start.Y));
        _session.Select(_window.Display.Id, selection);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_window.Editor.IsDrawing)
        {
            _window.Editor.End(PixelPoint(Clamp(e.GetPosition(this))), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Pointer.Capture(null); e.Handled = true; return;
        }
        if (!IsDragging) { e.Pointer.Capture(null); return; }
        var end = Clamp(e.GetPosition(this));
        if (_clickTarget is { } detected && Math.Abs(end.X - _start.X) < 3 && Math.Abs(end.Y - _start.Y) < 3)
            _session.Select(_window.Display.Id, detected, true);
        _clickTarget = null;
        IsDragging = false; e.Pointer.Capture(null); _window.Refresh(); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        IsDragging = false; _window.Refresh();
    }
    private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, Bounds.Width), Math.Clamp(point.Y, 0, Bounds.Height));
    private AnnotationPoint PixelPoint(Point point) => new(point.X / _window.Scale.X, point.Y / _window.Scale.Y);
}
