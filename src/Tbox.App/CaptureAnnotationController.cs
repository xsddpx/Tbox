using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tbox.Core;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Tbox.App;

internal sealed class CaptureAnnotationController : IDisposable
{
    private readonly Canvas _host;
    private readonly CanvasControl _surface;
    private readonly AnnotationDocument _document = new();
    private readonly Dictionary<AnnotationTool, double> _toolWidths = new()
    {
        [AnnotationTool.Rectangle] = 3,
        [AnnotationTool.Ellipse] = 3,
        [AnnotationTool.Line] = 3,
        [AnnotationTool.Polyline] = 3,
        [AnnotationTool.Arrow] = 3,
        [AnnotationTool.Number] = AnnotationDefaults.NumberDiameter,
        [AnnotationTool.Pen] = 3,
        [AnnotationTool.Highlighter] = 12,
        [AnnotationTool.Mosaic] = 18,
        [AnnotationTool.Text] = 20,
        [AnnotationTool.Eraser] = 18
    };
    private CapturedScreen? _screen;
    private PixelRegion _region;
    private AnnotationCommand? _draft;
    private AnnotationPoint? _gestureStart;
    private List<AnnotationPoint>? _gesturePoints;
    private List<AnnotationPoint>? _polylinePoints;
    private List<AnnotationPoint>? _eraserPoints;
    private TextBox? _textEditor;
    private AnnotationPoint _textPosition;
    private bool _committingText;
    private bool _disposed;

    internal CaptureAnnotationController(Canvas host)
    {
        _host = host;
        _surface = new CanvasControl
        {
            ClearColor = Colors.Transparent,
            IsHitTestVisible = false
        };
        _surface.Draw += OnDraw;
        _host.Children.Add(_surface);
    }

    internal event Action? StateChanged;
    internal event Action? FinishRequested;

    internal AnnotationTool? CurrentTool { get; private set; }
    internal uint ColorArgb { get; private set; } = 0xFFFF3B30;
    internal bool CanUndo => _document.CanUndo;
    internal bool CanRedo => _document.CanRedo;
    internal bool IsEditingText => _textEditor is not null;
    internal bool HasAnnotations => _document.Commands.Count != 0;
    internal IReadOnlyList<AnnotationCommand> Commands => _document.Commands;
    internal double CurrentWidth => CurrentTool is { } tool ? _toolWidths[tool] : 3;

    internal void Begin(CapturedScreen screen, PixelRegion region, double width, double height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Reset();
        _screen = screen;
        _region = region;
        _host.Width = width;
        _host.Height = height;
        _surface.Width = width;
        _surface.Height = height;
        _host.Visibility = Visibility.Visible;
        Invalidate();
    }

    internal void ResizeSurface(double width, double height)
    {
        if (_screen is null || width <= 0 || height <= 0) return;
        _host.Width = width;
        _host.Height = height;
        _surface.Width = width;
        _surface.Height = height;
        Invalidate();
    }

    internal void PrepareForRegionAdjustment()
    {
        if (_screen is null) return;
        CommitTextEditor();
        FinishPolyline();
        _eraserPoints = null;
        CancelGesture();
    }

    internal void UpdateRegion(CapturedScreen screen, PixelRegion region, double width, double height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0) return;
        _screen = screen;
        _region = region;
        _host.Width = width;
        _host.Height = height;
        _surface.Width = width;
        _surface.Height = height;
        _host.Visibility = Visibility.Visible;
        Invalidate();
    }

    internal void Reset()
    {
        CancelTextEditor();
        _draft = null;
        _gestureStart = null;
        _gesturePoints = null;
        _polylinePoints = null;
        _eraserPoints = null;
        _document.Reset();
        CurrentTool = null;
        _screen = null;
        _host.Visibility = Visibility.Collapsed;
        Invalidate();
        NotifyStateChanged();
    }

    internal void SetTool(AnnotationTool tool)
    {
        CommitTextEditor();
        FinishPolyline();
        CancelGesture();
        CurrentTool = tool;
        NotifyStateChanged();
    }

    internal void SetColor(uint colorArgb)
    {
        ColorArgb = colorArgb;
        NotifyStateChanged();
    }

    internal void AdjustWidth(int steps)
    {
        if (CurrentTool is not { } tool || steps == 0) return;
        _toolWidths[tool] = Math.Clamp(_toolWidths[tool] + steps, 1, 64);
        NotifyStateChanged();
    }

    internal bool PointerPressed(Point localPoint, bool shiftPressed)
    {
        if (_screen is null || CurrentTool is not { } tool) return false;
        CommitTextEditor();
        var point = ToSourcePoint(localPoint);
        switch (tool)
        {
            case AnnotationTool.Polyline:
                _polylinePoints ??= [];
                _polylinePoints.Add(ConstrainFromLast(point, shiftPressed));
                UpdatePolylineDraft(_polylinePoints[^1]);
                return false;
            case AnnotationTool.Text:
                StartTextEditor(point);
                return false;
            case AnnotationTool.Number:
                CommitNumber(point);
                return false;
            case AnnotationTool.Eraser:
                _eraserPoints = [point];
                return true;
            default:
                _gestureStart = point;
                _gesturePoints = [point];
                UpdateGesture(point, shiftPressed);
                return true;
        }
    }

    internal bool IsTextEditorElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, _textEditor)) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    internal void PointerMoved(Point localPoint, bool shiftPressed)
    {
        if (_screen is null || CurrentTool is not { } tool) return;
        var point = ToSourcePoint(localPoint);
        if (tool == AnnotationTool.Polyline && _polylinePoints is { Count: > 0 })
        {
            UpdatePolylineDraft(ConstrainFromLast(point, shiftPressed));
            return;
        }
        if (tool == AnnotationTool.Eraser && _eraserPoints is not null)
        {
            AddPointIfSeparated(_eraserPoints, point, 1);
            return;
        }
        if (_gestureStart is null || _gesturePoints is null) return;
        if (tool is AnnotationTool.Pen or AnnotationTool.Mosaic)
        {
            AddPointIfSeparated(_gesturePoints, point, 1);
        }
        else
        {
            if (_gesturePoints.Count == 1) _gesturePoints.Add(point);
            else _gesturePoints[^1] = point;
        }
        UpdateGesture(point, shiftPressed);
    }

    internal void PointerReleased(Point localPoint, bool shiftPressed)
    {
        if (_screen is null || CurrentTool is not { } tool) return;
        PointerMoved(localPoint, shiftPressed);
        if (tool == AnnotationTool.Eraser)
        {
            if (_eraserPoints is { Count: > 0 })
            {
                _document.EraseAt(_eraserPoints, ToPixelWidth(_toolWidths[AnnotationTool.Eraser]) / 2);
            }
            _eraserPoints = null;
            Invalidate();
            NotifyStateChanged();
            return;
        }
        if (tool == AnnotationTool.Polyline) return;
        if (_draft is not null && IsMeaningful(_draft)) _document.Commit(_draft);
        CancelGesture();
        NotifyStateChanged();
    }

    internal bool FinishPolyline()
    {
        if (_polylinePoints is not { Count: >= 2 })
        {
            if (_polylinePoints is null) return false;
            _polylinePoints = null;
            _draft = null;
            Invalidate();
            return true;
        }

        _document.Commit(new PolylineAnnotationCommand(
            _polylinePoints,
            CreateStyle(AnnotationTool.Polyline)));
        _polylinePoints = null;
        _draft = null;
        Invalidate();
        NotifyStateChanged();
        return true;
    }

    internal bool CancelCurrentOperation()
    {
        if (_textEditor is not null)
        {
            CancelTextEditor();
            return true;
        }
        if (_draft is not null || _gestureStart is not null || _polylinePoints is not null || _eraserPoints is not null)
        {
            _polylinePoints = null;
            _eraserPoints = null;
            CancelGesture();
            return true;
        }
        if (CurrentTool is not null)
        {
            CurrentTool = null;
            NotifyStateChanged();
            return true;
        }
        return false;
    }

    internal void CompleteEditing()
    {
        if (_screen is null) return;
        CommitTextEditor();
        FinishPolyline();
        _polylinePoints = null;
        _eraserPoints = null;
        CancelGesture();
        CurrentTool = null;
        NotifyStateChanged();
    }

    internal void CancelPointerGesture()
    {
        _eraserPoints = null;
        CancelGesture();
    }

    internal void Undo()
    {
        CancelTextEditor();
        _polylinePoints = null;
        _eraserPoints = null;
        CancelGesture();
        if (_document.Undo())
        {
            Invalidate();
            NotifyStateChanged();
        }
    }

    internal void Redo()
    {
        CancelTextEditor();
        _polylinePoints = null;
        _eraserPoints = null;
        CancelGesture();
        if (_document.Redo())
        {
            Invalidate();
            NotifyStateChanged();
        }
    }

    internal void Clear()
    {
        CancelTextEditor();
        _polylinePoints = null;
        _eraserPoints = null;
        CancelGesture();
        if (_document.Clear())
        {
            Invalidate();
            NotifyStateChanged();
        }
    }

    internal async Task<CapturedScreen> RenderAsync()
    {
        if (_screen is null) throw new InvalidOperationException("截图选区尚未准备好。");
        CommitTextEditor();
        FinishPolyline();
        return await CaptureAnnotationRenderer.RenderAsync(_screen, _region, _document.Commands);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_screen is null || _region.Width <= 0 || _region.Height <= 0) return;
        CaptureAnnotationRenderer.DrawPreview(
            args.DrawingSession,
            _screen,
            _region,
            _document.Commands,
            _draft,
            sender.ActualWidth,
            sender.ActualHeight);
    }

    private void UpdateGesture(AnnotationPoint proposedEnd, bool shiftPressed)
    {
        if (_gestureStart is not { } start || CurrentTool is not { } tool || _gesturePoints is null) return;
        var end = AnnotationGeometry.ApplyShiftConstraint(tool, start, proposedEnd, shiftPressed);
        var style = CreateStyle(tool);
        _draft = tool switch
        {
            AnnotationTool.Rectangle => new RectangleAnnotationCommand(start, end, style),
            AnnotationTool.Ellipse => new EllipseAnnotationCommand(start, end, style),
            AnnotationTool.Line => new LineAnnotationCommand(start, end, style),
            AnnotationTool.Arrow => new ArrowAnnotationCommand(start, end, style),
            AnnotationTool.Pen => new FreePathAnnotationCommand(_gesturePoints, style),
            AnnotationTool.Highlighter => new HighlighterAnnotationCommand([start, end], style),
            AnnotationTool.Mosaic => new MosaicAnnotationCommand(
                _gesturePoints,
                style,
                Math.Max(6, ToPixelWidth(10))),
            _ => null
        };
        Invalidate();
    }

    private void UpdatePolylineDraft(AnnotationPoint hoverPoint)
    {
        if (_polylinePoints is not { Count: > 0 }) return;
        var preview = _polylinePoints.Append(hoverPoint).ToArray();
        _draft = new PolylineAnnotationCommand(preview, CreateStyle(AnnotationTool.Polyline));
        Invalidate();
    }

    private AnnotationPoint ConstrainFromLast(AnnotationPoint point, bool shiftPressed)
    {
        if (_polylinePoints is not { Count: > 0 }) return point;
        return AnnotationGeometry.ApplyShiftConstraint(
            AnnotationTool.Polyline,
            _polylinePoints[^1],
            point,
            shiftPressed);
    }

    private void StartTextEditor(AnnotationPoint sourcePoint)
    {
        CancelTextEditor();
        _textPosition = sourcePoint;
        var local = ToLocalPoint(sourcePoint);
        var foreground = ToColor(ColorArgb);
        var perceivedBrightness =
            (foreground.R * 0.299) + (foreground.G * 0.587) + (foreground.B * 0.114);
        var editorBackground = perceivedBrightness >= 170
            ? Color.FromArgb(225, 28, 28, 28)
            : Color.FromArgb(225, 255, 255, 255);
        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 120,
            Width = Math.Min(260, Math.Max(120, _host.ActualWidth - local.X)),
            MinHeight = 42,
            FontSize = Math.Clamp(_toolWidths[AnnotationTool.Text], 10, 64),
            Foreground = new SolidColorBrush(foreground),
            Background = new SolidColorBrush(editorBackground),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212))
        };
        Canvas.SetLeft(editor, local.X);
        Canvas.SetTop(editor, local.Y);
        editor.KeyDown += OnTextEditorKeyDown;
        var commitText = new KeyboardAccelerator
        {
            Key = VirtualKey.Enter,
            Modifiers = VirtualKeyModifiers.Control
        };
        commitText.Invoked += (_, args) =>
        {
            args.Handled = true;
            CommitTextEditor();
        };
        editor.KeyboardAccelerators.Add(commitText);
        editor.LostFocus += (_, _) => CommitTextEditor();
        _textEditor = editor;
        _host.Children.Add(editor);
        editor.Focus(FocusState.Programmatic);
    }

    private void CommitNumber(AnnotationPoint center)
    {
        var diameter = ToPixelWidth(_toolWidths[AnnotationTool.Number]);
        var style = new AnnotationStyle(
            ColorArgb,
            Math.Max(2, diameter / 12),
            Math.Max(8, diameter * 0.52));
        _document.Commit(new NumberAnnotationCommand(
            _document.NextNumber,
            center,
            diameter,
            style));
        Invalidate();
        NotifyStateChanged();
    }

    private void OnTextEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (FinishRequested is not null) FinishRequested.Invoke();
            else CancelTextEditor();
        }
    }

    private void CommitTextEditor()
    {
        if (_textEditor is null || _committingText) return;
        _committingText = true;
        var editor = _textEditor;
        _textEditor = null;
        editor.KeyDown -= OnTextEditorKeyDown;
        _host.Children.Remove(editor);
        var text = editor.Text.TrimEnd();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var localWidth = Math.Max(20, editor.ActualWidth > 0 ? editor.ActualWidth : editor.Width);
            var lineCount = Math.Max(1, text.Count(character => character == '\n') + 1);
            var localHeight = Math.Max(
                editor.ActualHeight,
                lineCount * editor.FontSize * 1.45);
            var end = ToSourcePoint(new Point(
                ToLocalPoint(_textPosition).X + localWidth,
                ToLocalPoint(_textPosition).Y + localHeight));
            var bounds = new AnnotationBounds(
                _textPosition.X,
                _textPosition.Y,
                Math.Max(_textPosition.X + 1, end.X),
                Math.Max(_textPosition.Y + 1, end.Y));
            _document.Commit(new TextAnnotationCommand(
                text,
                bounds,
                CreateStyle(AnnotationTool.Text)));
        }
        _committingText = false;
        Invalidate();
        NotifyStateChanged();
    }

    private void CancelTextEditor()
    {
        if (_textEditor is null) return;
        var editor = _textEditor;
        _textEditor = null;
        editor.KeyDown -= OnTextEditorKeyDown;
        _host.Children.Remove(editor);
        Invalidate();
    }

    private AnnotationStyle CreateStyle(AnnotationTool tool)
    {
        var width = ToPixelWidth(_toolWidths[tool]);
        var color = tool == AnnotationTool.Highlighter
            ? (ColorArgb & 0x00FFFFFF) | 0x66000000
            : ColorArgb;
        return new AnnotationStyle(
            color,
            width,
            ToPixelWidth(_toolWidths[AnnotationTool.Text]));
    }

    private double ToPixelWidth(double dipWidth)
    {
        if (_surface.ActualWidth <= 0 || _surface.ActualHeight <= 0) return dipWidth;
        var horizontal = _region.Width / _surface.ActualWidth;
        var vertical = _region.Height / _surface.ActualHeight;
        return dipWidth * ((horizontal + vertical) / 2);
    }

    private AnnotationPoint ToSourcePoint(Point localPoint) => new(
        _region.Left + (Math.Clamp(localPoint.X, 0, _host.ActualWidth) / Math.Max(1, _host.ActualWidth) * _region.Width),
        _region.Top + (Math.Clamp(localPoint.Y, 0, _host.ActualHeight) / Math.Max(1, _host.ActualHeight) * _region.Height));

    private Point ToLocalPoint(AnnotationPoint sourcePoint) => new(
        (sourcePoint.X - _region.Left) / _region.Width * _host.ActualWidth,
        (sourcePoint.Y - _region.Top) / _region.Height * _host.ActualHeight);

    private static bool IsMeaningful(AnnotationCommand command) =>
        command is PathAnnotationCommand path ? path.Points.Count > 1 :
        command.Bounds.Width >= 1 || command.Bounds.Height >= 1;

    private static void AddPointIfSeparated(List<AnnotationPoint> points, AnnotationPoint point, double minimumDistance)
    {
        var previous = points[^1];
        var deltaX = point.X - previous.X;
        var deltaY = point.Y - previous.Y;
        if ((deltaX * deltaX) + (deltaY * deltaY) >= minimumDistance * minimumDistance) points.Add(point);
    }

    private void CancelGesture()
    {
        _draft = null;
        _gestureStart = null;
        _gesturePoints = null;
        Invalidate();
    }

    private void Invalidate()
    {
        if (!_disposed) _surface.Invalidate();
    }

    private void NotifyStateChanged()
    {
        Invalidate();
        StateChanged?.Invoke();
    }

    private static Color ToColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    public void Dispose()
    {
        if (_disposed) return;
        _surface.Draw -= OnDraw;
        _surface.RemoveFromVisualTree();
        FinishRequested = null;
        _disposed = true;
    }
}
