using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
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
    private Window? _toolbarWindow;
    private bool _toolbarWanted;
    private bool _closing;
    public Picture Picture { get; }
    public PixelRegion Region { get; private set; }
    public AnnotationEditor Editor { get; } = new();
    public bool CanAnnotate => _toolbarWindow?.IsVisible == true;
    internal bool IsSelected => IsActive || _toolbarWindow is { IsActive: true, IsVisible: true };

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
        Content = new Grid { Children = { _surface, overlay } };
        Add("恢复完整贴图", Restore);
        Add("识别文字", Recognize); Add("复制图片", () => controller.Run(CopyAsync));
        Add("图片另存为…", () => controller.Run(SaveAsync));
        _menu.Items.Add(new Separator()); Add("关闭贴图", Close);
        PointerWheelChanged += OnWheel;
        PositionChanged += (_, _) => PlaceToolbar(); SizeChanged += (_, _) => PlaceToolbar();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && !IsVisible) _toolbarWindow?.Hide();
            else if (args.Property == IsVisibleProperty && IsVisible && _toolbarWanted && !_closing) ShowToolbar();
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Editor.Changed += () => _surface.InvalidateVisual();
        Activated += (_, _) => _surface.InvalidateVisual();
        Deactivated += (_, _) => _surface.InvalidateVisual();
        Closed += (_, _) => { _closing = true; _toolbarWindow?.Close(); Picture.Dispose(); };
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
            var outputs = Ui.Toolbar(
                [
                    AnnotationToolbar.IconButton(IconKind.Ocr, "识别文字", Recognize),
                    AnnotationToolbar.IconButton(IconKind.Save, $"保存 · {ShortcutLabels.Command}S", () => _controller.Run(SaveAsync)),
                    AnnotationToolbar.IconButton(IconKind.Copy, $"复制图片 · {ShortcutLabels.Command}C", () => _controller.Run(CopyAsync))
                ],
                [AnnotationToolbar.IconButton(IconKind.Check, "完成 · Enter", FinishEditing)]);
            tools.HorizontalAlignment = outputs.HorizontalAlignment = HorizontalAlignment.Left;
            var toolbar = new StackPanel { Spacing = 6, Children = { tools, outputs } };
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
        _toolbarWindow.Show(); PlaceToolbar();
    }

    private void PlaceToolbar()
    {
        if (!CanAnnotate || _closing || this.TryGetPlatformHandle() is not { } handle) return;
        var bounds = _controller.Platform.GetWindowPlacement(handle.Handle);
        var work = _controller.Platform.GetWorkArea(handle.Handle);
        var toolbar = _toolbarWindow!;
        var scale = _controller.Platform.DesktopUnitsPerDip(handle.Handle);
        var placed = PinToolbarPlacementPolicy.PlaceTaskToolbar(ToBounds(bounds), ToBounds(work),
            (int)Math.Ceiling(toolbar.Width * scale), (int)Math.Ceiling(toolbar.Height * scale));
        _controller.Platform.PlaceWindow(toolbar.TryGetPlatformHandle()!.Handle, ToPlacement(placed), false);
    }

    public void FinishEditing()
    {
        _text.Commit(); Editor.FinishPolyline(); Editor.CancelGesture(); Editor.Select(null);
        _toolbarWanted = false; _toolbarWindow?.Hide(); _surface.InvalidateVisual();
    }

    public async Task CopyAsync()
    {
        _text.Commit(); Editor.FinishPolyline();
        using var output = AnnotationRenderer.Render(Picture, Region, Editor.Document.Commands);
        await _controller.CopyAsync(output);
    }

    public async Task SaveAsync()
    {
        _text.Commit(); Editor.FinishPolyline();
        using var output = AnnotationRenderer.Render(Picture, Region, Editor.Document.Commands);
        await _controller.SaveAsync(output, this);
    }

    public void Recognize()
    {
        using var original = Picture.Crop(Region); _controller.ShowOcr(original, true);
    }

    public void Crop(Point start, Point end)
    {
        var handle = this.TryGetPlatformHandle()!.Handle;
        var p = _controller.Platform.GetWindowPlacement(handle);
        if (!PinCropPolicy.TryCreate(Region, ToBounds(p), _surface.Bounds.Width, _surface.Bounds.Height,
            start.X, start.Y, end.X, end.Y, 8, out var crop)) return;
        _text.Commit(); Region = crop.Region;
        _controller.Platform.PlaceWindow(handle, ToPlacement(crop.WindowBounds), false);
        _surface.InvalidateVisual(); PlaceToolbar();
    }

    private void Restore()
    {
        var handle = this.TryGetPlatformHandle()!.Handle;
        var p = _controller.Platform.GetWindowPlacement(handle);
        var crop = PinCropPolicy.Restore(Region, ToBounds(p), Picture.Bitmap.PixelSize.Width, Picture.Bitmap.PixelSize.Height);
        Region = crop.Region; _controller.Platform.PlaceWindow(handle, ToPlacement(crop.WindowBounds), false);
        _surface.InvalidateVisual(); PlaceToolbar();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
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
        if (_text.IsEditing) return;
        var pointer = e.GetPosition(_surface);
        var handle = this.TryGetPlatformHandle()!.Handle;
        var bounds = _controller.Platform.GetWindowPlacement(handle);
        var scale = PinWindowScalePolicy.ApplyWheelDelta(bounds.Width / Region.Width, (int)Math.Round(e.Delta.Y * 120), 0.05, 8);
        var width = Math.Max(24, Region.Width * scale); var height = Math.Max(24, Region.Height * scale);
        var fractionX = pointer.X / _surface.Bounds.Width; var fractionY = pointer.Y / _surface.Bounds.Height;
        _controller.Platform.PlaceWindow(handle, new DesktopPlacement(bounds.Left + fractionX * (bounds.Width - width),
            bounds.Top + fractionY * (bounds.Height - height), width, height), false);
        e.Handled = true;
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
            AnnotationRenderer.Draw(context, _window.Picture, _window.Editor.Document.Commands);
            if (_window.Editor.Draft is { } draft) AnnotationRenderer.Draw(context, _window.Picture, [draft]);
        }
        if (_window.IsSelected) context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse(DesignTokens.Accent), .6), 1), new Rect(Bounds.Size).Deflate(.5));
        if (_cropStart is { } start) context.DrawRectangle(null, new Pen(Ui.Blue, 1), new Rect(start, _cropEnd).Normalize());
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus();
        var point = e.GetPosition(this); var buttons = e.GetCurrentPoint(this).Properties;
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
