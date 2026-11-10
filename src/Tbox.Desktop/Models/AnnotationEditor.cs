using Tbox.Core;

namespace Tbox.Desktop.Models;

public sealed class AnnotationEditor
{
    private AnnotationPoint _start;
    private readonly List<AnnotationPoint> _points = [];
    private bool _drawing;
    public AnnotationDocument Document { get; } = new();
    public AnnotationTool? Tool { get; private set; }
    public uint Color { get; set; } = 0xFFE5484D;
    public double StrokeWidth { get; set; } = 3;
    public double FontSize { get; set; } = 20;
    public double NumberSize { get; set; } = AnnotationDefaults.NumberDiameter;
    public AnnotationCommand? Draft { get; private set; }
    public bool IsDrawing => _drawing;
    public event Action? Changed;
    public event Action<AnnotationPoint>? TextRequested;

    public void Select(AnnotationTool? tool)
    {
        FinishPolyline(); CancelGesture(); Tool = tool; Changed?.Invoke();
    }

    public void ChangeSize(int direction)
    {
        SetSize(CurrentSize + direction * (Tool is AnnotationTool.Number or AnnotationTool.Text ? 2 : 1));
    }

    public double CurrentSize => Tool == AnnotationTool.Number ? NumberSize : Tool == AnnotationTool.Text ? FontSize : StrokeWidth;

    public void SetSize(double value)
    {
        if (!double.IsFinite(value)) return;
        if (Tool == AnnotationTool.Number) NumberSize = Math.Clamp(value, 10, 120);
        else if (Tool == AnnotationTool.Text) FontSize = Math.Clamp(value, 10, 120);
        else StrokeWidth = Math.Clamp(value, 1, 32);
        Changed?.Invoke();
    }

    public bool Begin(AnnotationPoint point, bool shift, bool doubleClick = false)
    {
        if (Tool is null) return false;
        if (Tool == AnnotationTool.Number)
        {
            Document.Commit(new NumberAnnotationCommand(Document.NextNumber, point, NumberSize, Style));
            Changed?.Invoke(); return true;
        }
        if (Tool == AnnotationTool.Text) { TextRequested?.Invoke(point); return true; }
        if (Tool == AnnotationTool.Polyline)
        {
            if (doubleClick) { FinishPolyline(); return true; }
            if (_points.Count > 0) point = AnnotationGeometry.ApplyShiftConstraint(Tool.Value, _points[^1], point, shift);
            _points.Add(point); _drawing = true;
            Draft = new PolylineAnnotationCommand(_points, Style); Changed?.Invoke(); return true;
        }
        _start = point; _points.Clear(); _points.Add(point); _drawing = true;
        Draft = Create(point, shift); Changed?.Invoke(); return true;
    }

    public void Move(AnnotationPoint point, bool shift)
    {
        if (!_drawing || Tool is null) return;
        if (Tool == AnnotationTool.Polyline)
        {
            point = AnnotationGeometry.ApplyShiftConstraint(Tool.Value, _points[^1], point, shift);
            Draft = new PolylineAnnotationCommand(_points.Append(point), Style);
        }
        else
        {
            _points.Add(point); Draft = Create(point, shift);
        }
        Changed?.Invoke();
    }

    public void End(AnnotationPoint point, bool shift)
    {
        if (!_drawing || Tool is null || Tool == AnnotationTool.Polyline) return;
        Move(point, shift);
        if (Tool == AnnotationTool.Eraser) Document.EraseAt(_points, StrokeWidth * 3);
        else if (Draft is { } draft) Document.Commit(draft);
        CancelGesture();
    }

    public void CommitText(string text, AnnotationPoint origin, double width, double height)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Document.Commit(new TextAnnotationCommand(text, new AnnotationBounds(origin.X, origin.Y,
            origin.X + Math.Max(1, width), origin.Y + Math.Max(1, height)), Style));
        Changed?.Invoke();
    }

    public void FinishPolyline()
    {
        if (Tool != AnnotationTool.Polyline || !_drawing) return;
        if (_points.Count >= 2) Document.Commit(new PolylineAnnotationCommand(_points, Style));
        CancelGesture();
    }

    public void CancelGesture() { _drawing = false; _points.Clear(); Draft = null; Changed?.Invoke(); }
    public void Reset() { CancelGesture(); Document.Reset(); Tool = null; Changed?.Invoke(); }
    public void Undo() { CancelGesture(); Document.Undo(); Changed?.Invoke(); }
    public void Redo() { CancelGesture(); Document.Redo(); Changed?.Invoke(); }

    private AnnotationStyle Style => new(Tool == AnnotationTool.Highlighter ? (Color & 0x00FFFFFF) | 0x60000000 : Color,
        Tool is AnnotationTool.Highlighter or AnnotationTool.Mosaic ? StrokeWidth * 6 : StrokeWidth, FontSize);

    private AnnotationCommand? Create(AnnotationPoint proposed, bool shift)
    {
        var end = AnnotationGeometry.ApplyShiftConstraint(Tool!.Value, _start, proposed, shift);
        return Tool switch
        {
            AnnotationTool.Rectangle => new RectangleAnnotationCommand(_start, end, Style),
            AnnotationTool.Ellipse => new EllipseAnnotationCommand(_start, end, Style),
            AnnotationTool.Line => new LineAnnotationCommand(_start, end, Style),
            AnnotationTool.Arrow => new ArrowAnnotationCommand(_start, end, Style),
            AnnotationTool.Pen => new FreePathAnnotationCommand(_points, Style),
            AnnotationTool.Highlighter => new HighlighterAnnotationCommand(_points, Style),
            AnnotationTool.Mosaic => new MosaicAnnotationCommand(_points, Style, Math.Max(6, StrokeWidth * 2)),
            _ => null
        };
    }
}
