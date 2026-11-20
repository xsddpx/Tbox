using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Views;

public sealed class PinnedWindow : Window
{
    private readonly DesktopController _controller;
    private readonly PinSurface _surface;
    private readonly AnnotationTextEditor _text;
    private readonly ContextMenu _menu = new();
    private readonly TextBlock _zoomLabel = new() { Foreground = Brushes.White, FontSize = 12 };
    private readonly Border _zoomBadge;
    private readonly DispatcherTimer _zoomBadgeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Window? _toolbarWindow;
    private AnnotationToolbar? _annotationToolbar;
    private PinWindowBounds? _toolbarPlacement;
    private bool _toolbarWanted;
    private bool _toolbarPlacementPending;
    private bool _closing;
    private DesktopPlacement? _zoomBounds;
    private DesktopPlacement? _preciseZoomBounds;
    private double _zoomScale;
    private double _minimumZoom;
    private double _pixelsPerUnit;
    private Point _zoomAnchor;
    private double _wheelRemainder;
    private double _pinchRemainder;
    private int _pendingWheelSteps;
    private bool _zoomPending;
    private int _zoomRevision;
    public Picture Picture { get; }
    public PixelRegion Region { get; private set; }
    public AnnotationEditor Editor { get; } = new();
    public bool CanAnnotate => _toolbarWindow?.IsVisible == true;
    internal bool IsSelected => IsActive || _toolbarWindow is { IsActive: true, IsVisible: true }
        || CanAnnotate && (_annotationToolbar?.PropertiesPopup.IsOpen == true || _annotationToolbar?.MorePopup.IsOpen == true);

    public PinnedWindow(DesktopController controller, Picture picture)
    {
        _controller = controller; Picture = picture;
        Region = new PixelRegion(0, 0, picture.Bitmap.PixelSize.Width, picture.Bitmap.PixelSize.Height);
        Title = "Tbox 贴图";
        SystemDecorations = SystemDecorations.None; CanResize = false; Topmost = true;
        ShowInTaskbar = false; Background = Brushes.Transparent;
        var ratio = Math.Min(0.5, 900d / picture.Bitmap.PixelSize.Width);
        Width = Math.Max(32, picture.Bitmap.PixelSize.Width * ratio);
        Height = Math.Max(24, picture.Bitmap.PixelSize.Height * ratio);
        _surface = new PinSurface(this);
        var overlay = new Canvas();
        _text = new AnnotationTextEditor(overlay, Editor,
            point => new Point((point.X - Region.Left) * Scale.X, (point.Y - Region.Top) * Scale.Y), () => Scale, true);
        _text.EscapeCommitted += FinishEditing;
        _zoomBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 24, 24, 28)),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2),
            Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, IsVisible = false,
            Child = _zoomLabel
        };
        _zoomBadgeTimer.Tick += (_, _) => HideZoomBadge();
        Content = new Grid { Children = { _surface, overlay, _zoomBadge } };
        Add("恢复原尺寸", ResetZoom);
        Add("恢复完整贴图", Restore);
        Add("识别文字", Recognize); Add("复制图片", () => controller.Run(CopyAsync));
        Add("图片另存为…", () => controller.Run(SaveAsync));
        _menu.Items.Add(new Separator()); Add("关闭贴图", Close);
        PointerWheelChanged += OnWheel;
        Gestures.AddPointerTouchPadGestureMagnifyHandler(this, OnMagnify);
        PositionChanged += (_, _) => ScheduleToolbarPlacement();
        SizeChanged += (_, _) => ScheduleToolbarPlacement();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && !IsVisible)
            {
                InvalidateZoom(forgetBounds: false);
                HideZoomBadge();
                _annotationToolbar?.ClosePopovers();
                _toolbarWindow?.Hide();
            }
            else if (args.Property == IsVisibleProperty && IsVisible && _toolbarWanted && !_closing) ShowToolbar();
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Editor.Changed += () => _surface.InvalidateVisual();
        Activated += (_, _) => _surface.InvalidateVisual();
        Deactivated += (_, _) => _surface.InvalidateVisual();
        Closed += (_, _) =>
        {
            _closing = true;
            InvalidateZoom();
            HideZoomBadge();
            _toolbarWindow?.Close();
            Picture.Dispose();
        };
    }

    internal PinSessionImage SnapshotForUpgrade()
    {
        if (!_text.Commit()) throw new InvalidOperationException(AnnotationTextEditor.OverflowMessage);
        Editor.FinishPolyline();
        var handle = this.TryGetPlatformHandle()!.Handle;
        var bounds = ReadZoomGeometry(handle, _controller.Platform.DesktopUnitsPerDip(handle));
        var state = new SavedPin(bounds, _pixelsPerUnit, _zoomScale, Region,
            AnnotationSnapshotData.From(Editor.Document.Snapshot()), IsVisible, Picture.CachePath);
        return new PinSessionImage(state, Picture.Png);
    }

    internal void RestoreForUpgrade(SavedPin state)
    {
        var region = state.Region;
        var size = Picture.Bitmap.PixelSize;
        if (region.Left < 0 || region.Top < 0 || region.Width <= 0 || region.Height <= 0 ||
            (long)region.Left + region.Width > size.Width || (long)region.Top + region.Height > size.Height ||
            state.Bounds is not { } bounds || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0 ||
            !double.IsFinite(state.Zoom) || state.Zoom <= 0 || !double.IsFinite(state.PixelsPerUnit) || state.PixelsPerUnit <= 0)
            throw new InvalidDataException("贴图会话的尺寸无效。");

        Region = region;
        Editor.Restore(state.Annotations.ToSnapshot());
        var platform = _controller.Platform;
        var handle = this.TryGetPlatformHandle()!.Handle;
        platform.PlaceWindow(handle, bounds, false);
        var work = platform.GetWorkArea(handle);
        _pixelsPerUnit = RenderScaling / platform.DesktopUnitsPerDip(handle);
        var ratio = state.PixelsPerUnit / _pixelsPerUnit;
        var target = bounds with { Width = bounds.Width * ratio, Height = bounds.Height * ratio };
        if (target.Left >= work.Left + work.Width || target.Left + target.Width <= work.Left ||
            target.Top >= work.Top + work.Height || target.Top + target.Height <= work.Top)
            target = target with { Left = work.Left, Top = work.Top };
        var actual = platform.GetWindowPlacement(handle);
        _zoomBounds = actual;
        _zoomScale = state.Zoom;
        _minimumZoom = Math.Min(0.05, _zoomScale);
        ApplyZoom(handle, actual, target);
        if (!state.Visible) Hide();
    }

    public Vector Scale => new(_surface.Bounds.Width / Region.Width, _surface.Bounds.Height / Region.Height);
    public AnnotationPoint ToPixel(Point point) => new(Region.Left + point.X / Scale.X, Region.Top + point.Y / Scale.Y);
    public void ShowMenu() => _menu.Open(this);

    private void ShowToolbar()
    {
        _toolbarWanted = true;
        if (_toolbarWindow is null)
        {
            var tools = new AnnotationToolbar(Editor, _text.Commit);
            _annotationToolbar = tools;
            tools.Complete(
                [
                    AnnotationToolbar.IconButton(IconKind.Ocr, "识别文字", Recognize),
                    AnnotationToolbar.IconButton(IconKind.Save, $"保存 · {ShortcutLabels.Command}S", () => _controller.Run(SaveAsync)),
                    AnnotationToolbar.IconButton(IconKind.Copy, $"复制图片 · {ShortcutLabels.Command}C", () => _controller.Run(CopyAsync))
                ],
                AnnotationToolbar.IconButton(IconKind.Check, "完成 · Enter", FinishEditing));
            var toolbar = tools;
            toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _toolbarWindow = new Window
            {
                Title = "Tbox 贴图工具条", Content = toolbar, Width = toolbar.DesiredSize.Width, Height = toolbar.DesiredSize.Height,
                SystemDecorations = SystemDecorations.None, CanResize = false, Topmost = true, ShowInTaskbar = false, ShowActivated = false,
                RequestedThemeVariant = ThemeVariant.Dark, Background = Brushes.Transparent, TransparencyLevelHint = [WindowTransparencyLevel.Transparent]
            };
            _toolbarWindow.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            _toolbarWindow.Activated += (_, _) => _surface.InvalidateVisual();
            _toolbarWindow.Deactivated += (_, _) => _surface.InvalidateVisual();
        }
        _toolbarWindow.Show();
        _toolbarPlacement = null;
        PlaceToolbar();
    }

    private void ScheduleToolbarPlacement()
    {
        if (!CanAnnotate || _closing || _toolbarPlacementPending) return;
        _toolbarPlacementPending = true;
        RequestAnimationFrame(_ =>
        {
            _toolbarPlacementPending = false;
            PlaceToolbar();
        });
    }

    private void PlaceToolbar()
    {
        if (!IsVisible || !CanAnnotate || _closing || this.TryGetPlatformHandle() is not { } handle) return;
        var bounds = _controller.Platform.GetWindowPlacement(handle.Handle);
        var work = _controller.Platform.GetWorkArea(handle.Handle);
        var toolbar = _toolbarWindow!;
        var scale = _controller.Platform.DesktopUnitsPerDip(handle.Handle);
        var placed = PinToolbarPlacementPolicy.PlaceTaskToolbar(ToBounds(bounds), ToBounds(work),
            (int)Math.Ceiling(toolbar.Width * scale), (int)Math.Ceiling(toolbar.Height * scale));
        if (placed == _toolbarPlacement) return;
        _annotationToolbar?.ClosePopovers();
        var toolbarHandle = toolbar.TryGetPlatformHandle()!.Handle;
        if (_toolbarPlacement is { } previous && placed.Width == previous.Width && placed.Height == previous.Height)
            _controller.Platform.MoveWindow(toolbarHandle, ToPlacement(placed));
        else
            _controller.Platform.PlaceWindow(toolbarHandle, ToPlacement(placed), false);
        _toolbarPlacement = placed;
    }

    public void FinishEditing()
    {
        if (!_text.Commit()) return;
        _annotationToolbar?.ClosePopovers();
        Editor.FinishPolyline(); Editor.CancelGesture(); Editor.Select(null);
        _toolbarWanted = false; _toolbarWindow?.Hide(); _surface.InvalidateVisual();
    }

    public async Task CopyAsync()
    {
        if (!_text.Commit()) return;
        Editor.FinishPolyline();
        using var output = AnnotationRenderer.Render(Picture, Region, Editor.Document.Commands);
        await _controller.CopyAsync(output);
    }

    public async Task SaveAsync()
    {
        if (!_text.Commit()) return;
        Editor.FinishPolyline();
        using var output = AnnotationRenderer.Render(Picture, Region, Editor.Document.Commands);
        await _controller.SaveAsync(output, this);
    }

    public void Recognize()
    {
        _controller.ShowOcr(Picture.Crop(Region), true, takeOwnership: true);
    }

    public void Crop(Point start, Point end)
    {
        var handle = this.TryGetPlatformHandle()!.Handle;
        var p = _controller.Platform.GetWindowPlacement(handle);
        if (!PinCropPolicy.TryCreate(Region, ToBounds(p), _surface.Bounds.Width, _surface.Bounds.Height,
            start.X, start.Y, end.X, end.Y, 8, out var crop)) return;
        if (!_text.Commit()) return;
        InvalidateZoom(); Region = crop.Region;
        _controller.Platform.PlaceWindow(handle, ToPlacement(crop.WindowBounds), false);
        _surface.InvalidateVisual(); ScheduleToolbarPlacement();
    }

    private void Restore()
    {
        var handle = this.TryGetPlatformHandle()!.Handle;
        var p = _controller.Platform.GetWindowPlacement(handle);
        var crop = PinCropPolicy.Restore(Region, ToBounds(p), Picture.Bitmap.PixelSize.Width, Picture.Bitmap.PixelSize.Height);
        InvalidateZoom(); Region = crop.Region;
        _controller.Platform.PlaceWindow(handle, ToPlacement(crop.WindowBounds), false);
        _surface.InvalidateVisual(); ScheduleToolbarPlacement();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _annotationToolbar?.DismissPopover() == true) { e.Handled = true; return; }
        // Popup events route through this window's tunnel handler before their own controls.
        // Let sliders, swatches and menu buttons handle Enter instead of finishing pin editing.
        if (e.Source is Visual source && (_annotationToolbar?.PropertiesPopup.IsInsidePopup(source) == true
            || _annotationToolbar?.MorePopup.IsInsidePopup(source) == true)) return;
        if (_text.IsEditing) return;
        if (e.Key == Key.Escape) { if (CanAnnotate) FinishEditing(); else Close(); e.Handled = true; }
        else if (e.Key == Key.Enter && CanAnnotate) { FinishEditing(); e.Handled = true; }
        else if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0)
        {
            if (e.Key == Key.C) _controller.Run(CopyAsync);
            else if (e.Key == Key.S) _controller.Run(SaveAsync);
            else if (e.Key == Key.Z) { if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Editor.Redo(); else Editor.Undo(); }
            else if (e.Key == Key.Y) Editor.Redo();
            else return;
            e.Handled = true;
        }
        else if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && IsSelected)
        {
            if (CanAnnotate) FinishEditing(); else ShowToolbar();
            e.Handled = true;
        }
        else if (CanAnnotate && e.Key == Key.B) { Editor.Select(AnnotationTool.Pen); e.Handled = true; }
        else if (CanAnnotate && e.Key == Key.T) { Editor.Select(AnnotationTool.Text); e.Handled = true; }
        else if (CanAnnotate && e.Key is Key.D1 or Key.D2) { Editor.ChangeSize(e.Key == Key.D1 ? -1 : 1); e.Handled = true; }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_text.IsEditing || !double.IsFinite(e.Delta.Y) || e.Delta.Y == 0) return;
        e.Handled = true;
        QueueZoom(PinWindowScalePolicy.ConsumeWheelSteps(ref _wheelRemainder, e.Delta.Y), e);
    }

    private void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        if (_text.IsEditing) return;
        e.Handled = true;
        QueueZoom(PinWindowScalePolicy.ConsumePinchSteps(ref _pinchRemainder, e.Delta.Y), e);
    }

    private void QueueZoom(int steps, PointerEventArgs e)
    {
        if (steps == 0) return;
        _pendingWheelSteps += steps;
        _zoomAnchor = e.GetPosition(_surface);
        if (_zoomPending) return;

        _zoomPending = true;
        var revision = _zoomRevision;
        RequestAnimationFrame(_ =>
        {
            if (revision != _zoomRevision) return;
            _zoomPending = false;
            var pending = _pendingWheelSteps;
            _pendingWheelSteps = 0;
            if (pending == 0 || !IsVisible || _closing || this.TryGetPlatformHandle() is not { } handle) return;
            var unitsPerDip = _controller.Platform.DesktopUnitsPerDip(handle.Handle);
            var current = ReadZoomGeometry(handle.Handle, unitsPerDip);
            var next = PinWindowScalePolicy.ApplyWheelSteps(_zoomScale, pending, _minimumZoom);
            if (next == _zoomScale) { ShowZoomBadge(); return; }
            _zoomScale = next;
            var anchor = new Point(current.Left + _zoomAnchor.X * unitsPerDip, current.Top + _zoomAnchor.Y * unitsPerDip);
            var width = Region.Width * _zoomScale / _pixelsPerUnit;
            var height = Region.Height * _zoomScale / _pixelsPerUnit;
            ApplyZoom(handle.Handle, current, ZoomAround(_preciseZoomBounds!, anchor, width, height));
            ShowZoomBadge();
        });
    }

    internal void ResetZoom()
    {
        if (_text.IsEditing || _closing || !IsVisible || this.TryGetPlatformHandle() is not { } handle) return;
        InvalidateZoom();
        var current = ReadZoomGeometry(handle.Handle, _controller.Platform.DesktopUnitsPerDip(handle.Handle));
        var work = _controller.Platform.GetWorkArea(handle.Handle);
        var desired = OriginalSize(current, work, Region, _pixelsPerUnit);
        _zoomScale = desired.Width * _pixelsPerUnit / Region.Width;
        _minimumZoom = Math.Min(0.05, _zoomScale);
        ApplyZoom(handle.Handle, current, desired);
        ShowZoomBadge();
    }

    private DesktopPlacement ReadZoomGeometry(nint handle, double unitsPerDip)
    {
        var platform = _controller.Platform;
        var bounds = platform.GetWindowPlacement(handle);
        var pixelsPerUnit = RenderScaling / unitsPerDip;
        if (pixelsPerUnit != _pixelsPerUnit || _zoomBounds is not { } previous ||
            bounds.Width != previous.Width || bounds.Height != previous.Height)
        {
            _pixelsPerUnit = pixelsPerUnit;
            _zoomScale = Math.Min(bounds.Width * pixelsPerUnit / Region.Width, bounds.Height * pixelsPerUnit / Region.Height);
            _minimumZoom = Math.Min(0.05, _zoomScale);
            _preciseZoomBounds = bounds;
        }
        else if (bounds != previous && _preciseZoomBounds is { } precise)
            _preciseZoomBounds = precise with
            {
                Left = precise.Left + bounds.Left - previous.Left,
                Top = precise.Top + bounds.Top - previous.Top
            };
        _zoomBounds = bounds;
        return bounds;
    }

    private void ApplyZoom(nint handle, DesktopPlacement current, DesktopPlacement desired)
    {
        // Window frames use whole desktop units: points on macOS and pixels on Windows.
        // Rounding to backing pixels first lets AppKit round the same dimension twice.
        var snapped = new DesktopPlacement(Math.Round(desired.Left), Math.Round(desired.Top),
            Math.Max(1, Math.Round(desired.Width)), Math.Max(1, Math.Round(desired.Height)));
        // Keep subpixel bounds for the next zoom instead of feeding the compositor's rounding back into the anchor.
        _preciseZoomBounds = desired;
        if (snapped == current) return;
        _controller.Platform.ResizeWindow(handle, snapped);
        _zoomBounds = snapped;
        ScheduleToolbarPlacement();
    }

    internal static DesktopPlacement ZoomAround(DesktopPlacement bounds, Point anchor, double width, double height)
    {
        var x = Math.Clamp((anchor.X - bounds.Left) / bounds.Width, 0, 1);
        var y = Math.Clamp((anchor.Y - bounds.Top) / bounds.Height, 0, 1);
        return new(bounds.Left + x * (bounds.Width - width), bounds.Top + y * (bounds.Height - height), width, height);
    }

    internal static DesktopPlacement OriginalSize(DesktopPlacement bounds, DesktopPlacement work, PixelRegion region,
        double pixelsPerUnit)
    {
        var scale = Math.Min(1 / pixelsPerUnit, Math.Min(work.Width / region.Width, work.Height / region.Height));
        var width = region.Width * scale;
        var height = region.Height * scale;
        var centerX = (Math.Max(bounds.Left, work.Left) + Math.Min(bounds.Left + bounds.Width, work.Left + work.Width)) / 2;
        var centerY = (Math.Max(bounds.Top, work.Top) + Math.Min(bounds.Top + bounds.Height, work.Top + work.Height)) / 2;
        return new(
            Math.Clamp(centerX - width / 2, work.Left, Math.Max(work.Left, work.Left + work.Width - width)),
            Math.Clamp(centerY - height / 2, work.Top, Math.Max(work.Top, work.Top + work.Height - height)),
            width, height);
    }

    private void InvalidateZoom(bool forgetBounds = true)
    {
        _zoomRevision++;
        _zoomPending = false;
        if (forgetBounds) _zoomBounds = null;
        _wheelRemainder = 0;
        _pinchRemainder = 0;
        _pendingWheelSteps = 0;
    }

    private void ShowZoomBadge()
    {
        _zoomLabel.Text = _zoomScale.ToString(_zoomScale < 0.1 ? "0.#%" : "0%");
        _zoomBadge.IsVisible = true;
        _zoomBadgeTimer.Stop();
        _zoomBadgeTimer.Start();
    }

    private void HideZoomBadge()
    {
        _zoomBadgeTimer.Stop();
        _zoomBadge.IsVisible = false;
    }

    private void Add(string label, Action action)
    { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); _menu.Items.Add(item); }
    private static PinWindowBounds ToBounds(DesktopPlacement p) => new((int)p.Left, (int)p.Top, (int)p.Width, (int)p.Height);
    private static DesktopPlacement ToPlacement(PinWindowBounds p) => new(p.X, p.Y, p.Width, p.Height);
}

internal sealed class PinSurface : Control
{
    private readonly PinnedWindow _window;
    private Point? _cropStart;
    private Point _cropEnd;
    public PinSurface(PinnedWindow window) { _window = window; Focusable = true; ClipToBounds = true; }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var r = _window.Region;
        context.DrawImage(_window.Picture.Bitmap, new Rect(r.Left, r.Top, r.Width, r.Height), new Rect(Bounds.Size));
        using (context.PushTransform(Matrix.CreateTranslation(-r.Left, -r.Top) * Matrix.CreateScale(_window.Scale.X, _window.Scale.Y)))
        {
            _window.Editor.Draw(context, _window.Picture);
        }
        if (_window.IsSelected) context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse(DesignTokens.Accent), .6), 1), new Rect(Bounds.Size).Deflate(.5));
        if (_cropStart is { } start) context.DrawRectangle(null, new Pen(Ui.Blue, 1), new Rect(start, _cropEnd).Normalize());
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this); var buttons = e.GetCurrentPoint(this).Properties;
        if (buttons.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
        {
            if (!buttons.IsLeftButtonPressed && !buttons.IsRightButtonPressed) _window.ResetZoom();
            e.Handled = true;
            return;
        }
        Focus();
        if (buttons.IsRightButtonPressed)
        { _cropStart = point; _cropEnd = point; e.Pointer.Capture(this); e.Handled = true; }
        else if (buttons.IsLeftButtonPressed)
        {
            if (_window.CanAnnotate && _window.Editor.Begin(_window.ToPixel(point), e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.ClickCount > 1))
            { if (_window.Editor.Tool != AnnotationTool.Text) e.Pointer.Capture(this); e.Handled = true; }
            else _window.BeginMoveDrag(e);
        }
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_cropStart is not null) { _cropEnd = e.GetPosition(this); InvalidateVisual(); }
        else _window.Editor.Move(_window.ToPixel(e.GetPosition(this)), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased)
        {
            e.Handled = true;
            return;
        }
        if (_cropStart is { } start)
        {
            var end = e.GetPosition(this); _cropStart = null;
            if (Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2)) >= 8) _window.Crop(start, end); else _window.ShowMenu();
            InvalidateVisual();
        }
        else _window.Editor.End(_window.ToPixel(e.GetPosition(this)), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Pointer.Capture(null); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { base.OnPointerCaptureLost(e); _cropStart = null; InvalidateVisual(); }
}
