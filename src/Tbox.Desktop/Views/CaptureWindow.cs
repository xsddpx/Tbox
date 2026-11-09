using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Views;

internal sealed class CaptureWindow : Window
{
    private readonly CaptureSession _session;
    private readonly Border _toolbar;
    private readonly AnnotationTextEditor _text;
    private bool _toolbarHidden;
    private readonly StackPanel _hintKeys = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _hintText = new() { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _modeConfirmation;
    private readonly TextBlock _modePrompt = new() { Foreground = Brushes.White };
    private CaptureAction _pendingMode;
    public CapturedDisplay Display { get; }
    public Picture Picture { get; }
    public CaptureSurface Surface { get; }
    public AnnotationEditor Editor { get; } = new();

    public CaptureWindow(CaptureSession session, CapturedDisplay display, double topSafeInset)
    {
        _session = session; Display = display; Picture = new Picture(display.Png);
        Title = session.Preset switch { CaptureAction.Record => "Tbox 录屏选区", CaptureAction.Long => "Tbox 长截图选区", _ => "Tbox 截图" };
        SystemDecorations = SystemDecorations.None;
        CanResize = false; ShowInTaskbar = false; Topmost = true;
        RequestedThemeVariant = ThemeVariant.Dark;
        Width = display.Width; Height = display.Height;
        Surface = new CaptureSurface(session, this);
        var overlay = new Canvas();
        _text = new AnnotationTextEditor(overlay, Editor,
            point => new Point(point.X * Scale.X, point.Y * Scale.Y), () => Scale);
        _toolbar = CreateToolbar();
        _toolbar.IsVisible = false;
        overlay.Children.Add(_toolbar);
        _modeConfirmation = new Border
        {
            IsVisible = false, Background = Ui.ToolbarBackground, BorderBrush = Ui.ToolbarBorder, BorderThickness = new Thickness(1),
            Padding = new Thickness(20), CornerRadius = new CornerRadius(DesignTokens.CardRadius),
            Child = new StackPanel { Spacing = 12, Children =
            {
                _modePrompt,
                Ui.Row(Ui.Primary("丢弃标注并继续", () => { DismissConfirmation(); Editor.Reset(); session.Finish(this, _pendingMode); }),
                    Ui.Button("保留标注", DismissConfirmation))
            } }
        };
        overlay.Children.Add(_modeConfirmation);
        Content = new Grid { Children = { Surface, overlay, CreateHint(topSafeInset) } };
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

    private Border CreateToolbar()
    {
        if (_session.Preset is { } preset)
        {
            // Record / long-capture area: confirm and cancel only, as icons like every other toolbar.
            var name = preset == CaptureAction.Record ? "录屏" : "长截图";
            var confirm = AnnotationToolbar.IconButton(preset == CaptureAction.Record ? IconKind.Record : IconKind.Long, $"确认{name}区域 · Enter",
                () => _session.Finish(this, preset), preset == CaptureAction.Record ? Brush.Parse("#FF6B6E") : null);
            return Ui.Toolbar([confirm], [AnnotationToolbar.IconButton(IconKind.Close, $"取消 · Esc", _session.Cancel)]);
        }

        // Row 1: annotation tools, colour and size, undo / redo. Row 2: mode, outputs and cancel (spec ⑤).
        var tools = new AnnotationToolbar(Editor, _text.Commit);
        var outputs = Ui.Toolbar(
            [
                AnnotationToolbar.IconButton(IconKind.Long, "长截图", () => RequestMode(CaptureAction.Long)),
                AnnotationToolbar.IconButton(IconKind.Record, "录屏", () => RequestMode(CaptureAction.Record), Brush.Parse("#FF6B6E"))
            ],
            [
                AnnotationToolbar.IconButton(IconKind.Pin, $"贴图 · {ShortcutLabels.Command}T", () => _session.Finish(this, CaptureAction.Pin)),
                AnnotationToolbar.IconButton(IconKind.Ocr, "识别文字", () => _session.Finish(this, CaptureAction.Ocr)),
                AnnotationToolbar.IconButton(IconKind.Save, $"保存 · {ShortcutLabels.Command}S", () => _session.Finish(this, CaptureAction.Save)),
                AnnotationToolbar.IconButton(IconKind.Copy, $"复制 · {ShortcutLabels.Command}C / Enter", () => _session.Finish(this, CaptureAction.Copy))
            ],
            [AnnotationToolbar.IconButton(IconKind.Close, "取消 · Esc", _session.Cancel)]);
        tools.HorizontalAlignment = outputs.HorizontalAlignment = HorizontalAlignment.Left;
        return new Border { Child = new StackPanel { Spacing = 6, Children = { tools, outputs } } };
    }

    public void Refresh()
    {
        Surface.InvalidateVisual();
        var bounds = _session.State.ActiveDisplay == Display.Id ? _session.State.Selection : null;
        _toolbar.IsVisible = bounds is { Width: > 0, Height: > 0 } && !Surface.IsDragging && !_toolbarHidden;
        if (bounds is not { } selection) return;
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _toolbar.DesiredSize;
        if (size.Width < 1 || size.Height < 1) return; // Hidden while dragging; placed once it shows.
        var placed = PinToolbarPlacementPolicy.PlaceTaskToolbar(
            new PinWindowBounds((int)selection.Left, (int)selection.Top, (int)Math.Max(1, selection.Width), (int)Math.Max(1, selection.Height)),
            new PinWindowBounds(0, 0, (int)Math.Max(1, Surface.Bounds.Width), (int)Math.Max(1, Surface.Bounds.Height)),
            (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
        // Left-aligned with the selection as in the spec, kept on screen.
        Canvas.SetLeft(_toolbar, Math.Clamp(selection.Left, 0, Math.Max(0, Surface.Bounds.Width - size.Width)));
        Canvas.SetTop(_toolbar, placed.Y);
    }

    // The hint chip: a sentence plus key caps, centred at the top. Mode messages replace the default text.
    public void SetHint(string message) { _hintText.Text = message; _hintKeys.IsVisible = message.Length == 0; Refresh(); }

    private Border CreateHint(double topSafeInset)
    {
        var name = _session.Preset switch { CaptureAction.Record => "录屏区域", CaptureAction.Long => "长截图区域", _ => null };
        var lead = new TextBlock { Text = name is null ? "拖动框选，单击选窗口" : $"拖动框选{name}", Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        _hintKeys.Children.Add(lead);
        foreach (var (key, label) in _session.Preset is null
            ? new[] { ("R", "上次区域"), ("Tab", "元素检测"), ("Esc", "取消") }
            : new[] { ("Enter", "确认"), ("Esc", "取消") })
        {
            _hintKeys.Children.Add(HintKey(key));
            _hintKeys.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        }
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, topSafeInset + 18, 0, 0),
            Background = new SolidColorBrush(Color.Parse("#14141C"), .82), CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 7),
            IsHitTestVisible = false, Child = new Panel { Children = { _hintKeys, _hintText } }
        };
    }

    private static Border HintKey(string key) => new()
    {
        Background = new SolidColorBrush(Colors.White, .16), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 0), Height = 20, MinWidth = 20,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = key, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center }
    };

    private void RequestMode(CaptureAction action)
    {
        CommitEditing();
        if (Editor.Document.Commands.Count == 0) { _session.Finish(this, action); return; }
        _pendingMode = action;
        _modePrompt.Text = (action == CaptureAction.Record ? "录屏" : "长截图") + "使用原始画面，是否丢弃本次标注？";
        _modeConfirmation.Measure(Size.Infinity);
        Canvas.SetLeft(_modeConfirmation, Math.Max(0, (Surface.Bounds.Width - _modeConfirmation.DesiredSize.Width) / 2));
        Canvas.SetTop(_modeConfirmation, Math.Max(0, (Surface.Bounds.Height - _modeConfirmation.DesiredSize.Height) / 2));
        _modeConfirmation.IsVisible = true;
        _toolbar.IsEnabled = false; Surface.IsEnabled = false;
    }

    private void DismissConfirmation()
    { _modeConfirmation.IsVisible = false; _toolbar.IsEnabled = true; Surface.IsEnabled = true; Surface.Focus(); }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (_text.IsEditing) return;
        if (_modeConfirmation.IsVisible)
        {
            if (args.Key == Key.Escape) DismissConfirmation();
            args.Handled = true; return;
        }
        if (args.Key == Key.Escape)
        {
            if (Editor.IsDrawing) Editor.CancelGesture();
            else if (Editor.Tool is not null) Editor.Select(null);
            else _session.Cancel();
            args.Handled = true;
        }
        else if (args.Key == Key.Enter)
        {
            _session.Finish(this, _session.Preset ?? CaptureAction.Copy);
            args.Handled = true;
        }
        else if (_session.Preset is not null) return;
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
            // The hint chip at the top tells the user what to do.
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
        var mode = Ui.ModeBrush(_session.Preset);
        SelectionOutline.Draw(context, r, mode, Selection is not null);
        var region = CaptureRegionPolicy.MapToPixels(Bounds.Width, Bounds.Height, r.X, r.Y, r.Width, r.Height,
            _window.Picture.Bitmap.PixelSize.Width, _window.Picture.Bitmap.PixelSize.Height);
        // Size tag in the mode colour; yellow takes dark text for contrast.
        var label = new FormattedText($"{region.Width} × {region.Height}", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(Typeface.Default.FontFamily, weight: FontWeight.SemiBold), 12,
            _session.Preset == CaptureAction.Long ? Brush.Parse("#1A1B22") : Brushes.White);
        var labelPoint = new Point(r.Left, r.Top >= 28 ? r.Top - 26 : r.Top + 8);
        context.DrawRectangle(mode, null, new RoundedRect(new Rect(labelPoint.X, labelPoint.Y, label.Width + 14, 22), 5));
        context.DrawText(label, labelPoint + new Vector(7, 3));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsMiddleButtonPressed && _session.Preset is null)
        { _session.Finish(_window, CaptureAction.Pin); return; }
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
