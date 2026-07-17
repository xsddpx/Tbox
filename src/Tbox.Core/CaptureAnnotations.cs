using System.Collections.ObjectModel;

namespace Tbox.Core;

public enum AnnotationTool
{
    Rectangle,
    Ellipse,
    Line,
    Polyline,
    Arrow,
    Pen,
    Highlighter,
    Mosaic,
    Text,
    Eraser
}

public readonly record struct AnnotationPoint
{
    public AnnotationPoint(double x, double y)
    {
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        X = x;
        Y = y;
    }

    public double X { get; }
    public double Y { get; }
}

public readonly record struct AnnotationBounds
{
    public AnnotationBounds(double left, double top, double right, double bottom)
    {
        if (!double.IsFinite(left)) throw new ArgumentOutOfRangeException(nameof(left));
        if (!double.IsFinite(top)) throw new ArgumentOutOfRangeException(nameof(top));
        if (!double.IsFinite(right) || right < left) throw new ArgumentOutOfRangeException(nameof(right));
        if (!double.IsFinite(bottom) || bottom < top) throw new ArgumentOutOfRangeException(nameof(bottom));
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public double Left { get; }
    public double Top { get; }
    public double Right { get; }
    public double Bottom { get; }
    public double Width => Right - Left;
    public double Height => Bottom - Top;

    public bool Contains(AnnotationPoint point) =>
        point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    public AnnotationBounds Inflate(double amount)
    {
        if (!double.IsFinite(amount)) throw new ArgumentOutOfRangeException(nameof(amount));
        var horizontal = amount >= 0 ? amount : Math.Max(amount, -Width / 2);
        var vertical = amount >= 0 ? amount : Math.Max(amount, -Height / 2);
        return new AnnotationBounds(
            Left - horizontal,
            Top - vertical,
            Right + horizontal,
            Bottom + vertical);
    }

    internal static AnnotationBounds FromPoints(IEnumerable<AnnotationPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        using var enumerator = points.GetEnumerator();
        if (!enumerator.MoveNext()) throw new ArgumentException("至少需要一个点。", nameof(points));

        var left = enumerator.Current.X;
        var right = left;
        var top = enumerator.Current.Y;
        var bottom = top;
        while (enumerator.MoveNext())
        {
            left = Math.Min(left, enumerator.Current.X);
            right = Math.Max(right, enumerator.Current.X);
            top = Math.Min(top, enumerator.Current.Y);
            bottom = Math.Max(bottom, enumerator.Current.Y);
        }

        return new AnnotationBounds(left, top, right, bottom);
    }
}

public readonly record struct AnnotationStyle
{
    public AnnotationStyle(uint colorArgb, double strokeWidthPixels, double fontSizePixels = 20)
    {
        if (!double.IsFinite(strokeWidthPixels) || strokeWidthPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(strokeWidthPixels));
        }
        if (!double.IsFinite(fontSizePixels) || fontSizePixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSizePixels));
        }

        ColorArgb = colorArgb;
        StrokeWidthPixels = strokeWidthPixels;
        FontSizePixels = fontSizePixels;
    }

    public uint ColorArgb { get; }
    public double StrokeWidthPixels { get; }
    public double FontSizePixels { get; }
    public byte Alpha => (byte)(ColorArgb >> 24);

    public static AnnotationStyle Default { get; } = new(0xFFFF3B30, 3, 20);
}

public abstract record AnnotationCommand
{
    protected AnnotationCommand(AnnotationStyle style, Guid? id)
    {
        if (style.StrokeWidthPixels <= 0 || style.FontSizePixels <= 0)
        {
            throw new ArgumentException("标注样式未初始化。", nameof(style));
        }

        Style = style;
        Id = id ?? Guid.NewGuid();
    }

    public Guid Id { get; }
    public AnnotationStyle Style { get; }
    public abstract AnnotationTool Tool { get; }
    public abstract AnnotationBounds Bounds { get; }

    public bool HitTest(AnnotationPoint point, double tolerancePixels = 0)
    {
        if (!double.IsFinite(tolerancePixels) || tolerancePixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerancePixels));
        }

        return HitTestCore(point, tolerancePixels + (Style.StrokeWidthPixels / 2));
    }

    protected abstract bool HitTestCore(AnnotationPoint point, double radiusPixels);
}

public sealed record RectangleAnnotationCommand : AnnotationCommand
{
    public RectangleAnnotationCommand(
        AnnotationPoint start,
        AnnotationPoint end,
        AnnotationStyle style,
        Guid? id = null)
        : base(style, id)
    {
        Start = start;
        End = end;
        Bounds = AnnotationGeometry.GetBounds(start, end);
    }

    public AnnotationPoint Start { get; }
    public AnnotationPoint End { get; }
    public override AnnotationTool Tool => AnnotationTool.Rectangle;
    public override AnnotationBounds Bounds { get; }

    protected override bool HitTestCore(AnnotationPoint point, double radiusPixels)
    {
        if (Bounds.Width == 0 || Bounds.Height == 0)
        {
            return AnnotationGeometry.DistanceToSegment(point, Start, End) <= radiusPixels;
        }

        var outer = new AnnotationBounds(
            Bounds.Left - radiusPixels,
            Bounds.Top - radiusPixels,
            Bounds.Right + radiusPixels,
            Bounds.Bottom + radiusPixels);
        if (!outer.Contains(point)) return false;

        if (Bounds.Width <= radiusPixels * 2 || Bounds.Height <= radiusPixels * 2) return true;
        var inner = new AnnotationBounds(
            Bounds.Left + radiusPixels,
            Bounds.Top + radiusPixels,
            Bounds.Right - radiusPixels,
            Bounds.Bottom - radiusPixels);
        return !inner.Contains(point);
    }
}

public sealed record EllipseAnnotationCommand : AnnotationCommand
{
    public EllipseAnnotationCommand(
        AnnotationPoint start,
        AnnotationPoint end,
        AnnotationStyle style,
        Guid? id = null)
        : base(style, id)
    {
        Start = start;
        End = end;
        Bounds = AnnotationGeometry.GetBounds(start, end);
    }

    public AnnotationPoint Start { get; }
    public AnnotationPoint End { get; }
    public override AnnotationTool Tool => AnnotationTool.Ellipse;
    public override AnnotationBounds Bounds { get; }

    protected override bool HitTestCore(AnnotationPoint point, double radiusPixels)
    {
        if (Bounds.Width == 0 || Bounds.Height == 0)
        {
            return AnnotationGeometry.DistanceToSegment(point, Start, End) <= radiusPixels;
        }

        var centerX = (Bounds.Left + Bounds.Right) / 2;
        var centerY = (Bounds.Top + Bounds.Bottom) / 2;
        var radiusX = Bounds.Width / 2;
        var radiusY = Bounds.Height / 2;
        if (!IsInsideEllipse(point, centerX, centerY, radiusX + radiusPixels, radiusY + radiusPixels))
        {
            return false;
        }

        var innerRadiusX = radiusX - radiusPixels;
        var innerRadiusY = radiusY - radiusPixels;
        return innerRadiusX <= 0 || innerRadiusY <= 0 ||
               !IsInsideEllipse(point, centerX, centerY, innerRadiusX, innerRadiusY);
    }

    private static bool IsInsideEllipse(
        AnnotationPoint point,
        double centerX,
        double centerY,
        double radiusX,
        double radiusY)
    {
        var normalizedX = (point.X - centerX) / radiusX;
        var normalizedY = (point.Y - centerY) / radiusY;
        return (normalizedX * normalizedX) + (normalizedY * normalizedY) <= 1;
    }
}

public sealed record LineAnnotationCommand : AnnotationCommand
{
    public LineAnnotationCommand(
        AnnotationPoint start,
        AnnotationPoint end,
        AnnotationStyle style,
        Guid? id = null)
        : base(style, id)
    {
        Start = start;
        End = end;
    }

    public AnnotationPoint Start { get; }
    public AnnotationPoint End { get; }
    public override AnnotationTool Tool => AnnotationTool.Line;
    public override AnnotationBounds Bounds => AnnotationGeometry.GetBounds(Start, End);

    protected override bool HitTestCore(AnnotationPoint point, double radiusPixels) =>
        AnnotationGeometry.DistanceToSegment(point, Start, End) <= radiusPixels;
}

public sealed record ArrowAnnotationCommand : AnnotationCommand
{
    public ArrowAnnotationCommand(
        AnnotationPoint start,
        AnnotationPoint end,
        AnnotationStyle style,
        double? headLengthPixels = null,
        Guid? id = null)
        : base(style, id)
    {
        var resolvedHeadLength = headLengthPixels ?? Math.Max(8, style.StrokeWidthPixels * 4);
        if (!double.IsFinite(resolvedHeadLength) || resolvedHeadLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(headLengthPixels));
        }

        Start = start;
        End = end;
        HeadLengthPixels = resolvedHeadLength;
        (HeadPoint1, HeadPoint2) = AnnotationGeometry.GetArrowHead(start, end, resolvedHeadLength);
    }

    public AnnotationPoint Start { get; }
    public AnnotationPoint End { get; }
    public AnnotationPoint HeadPoint1 { get; }
    public AnnotationPoint HeadPoint2 { get; }
    public double HeadLengthPixels { get; }
    public override AnnotationTool Tool => AnnotationTool.Arrow;
    public override AnnotationBounds Bounds =>
        AnnotationBounds.FromPoints([Start, End, HeadPoint1, HeadPoint2]);

    protected override bool HitTestCore(AnnotationPoint point, double radiusPixels) =>
        AnnotationGeometry.DistanceToSegment(point, Start, End) <= radiusPixels ||
        AnnotationGeometry.DistanceToSegment(point, End, HeadPoint1) <= radiusPixels ||
        AnnotationGeometry.DistanceToSegment(point, End, HeadPoint2) <= radiusPixels;
}

public abstract record PathAnnotationCommand : AnnotationCommand
{
    private readonly ReadOnlyCollection<AnnotationPoint> _points;

    protected PathAnnotationCommand(
        IEnumerable<AnnotationPoint> points,
        AnnotationStyle style,
        Guid? id)
        : base(style, id)
    {
        ArgumentNullException.ThrowIfNull(points);
        var copy = points.ToArray();
        if (copy.Length == 0) throw new ArgumentException("路径至少需要一个点。", nameof(points));
        _points = Array.AsReadOnly(copy);
        Bounds = AnnotationBounds.FromPoints(copy);
    }

    public IReadOnlyList<AnnotationPoint> Points => _points;
    public override AnnotationBounds Bounds { get; }

    protected override bool HitTestCore(AnnotationPoint point, double radiusPixels) =>
        AnnotationGeometry.HitTestPath(_points, point, radiusPixels);
}

public sealed record PolylineAnnotationCommand : PathAnnotationCommand
{
    public PolylineAnnotationCommand(
        IEnumerable<AnnotationPoint> points,
        AnnotationStyle style,
        Guid? id = null)
        : base(points, style, id)
    {
    }

    public override AnnotationTool Tool => AnnotationTool.Polyline;
}

public sealed record FreePathAnnotationCommand : PathAnnotationCommand
{
    public FreePathAnnotationCommand(
        IEnumerable<AnnotationPoint> points,
        AnnotationStyle style,
        Guid? id = null)
        : base(points, style, id)
    {
    }

    public override AnnotationTool Tool => AnnotationTool.Pen;
}

public sealed record HighlighterAnnotationCommand : PathAnnotationCommand
{
    public HighlighterAnnotationCommand(
        IEnumerable<AnnotationPoint> points,
        AnnotationStyle style,
        Guid? id = null)
        : base(points, style, id)
    {
    }

    public override AnnotationTool Tool => AnnotationTool.Highlighter;
}

public sealed record MosaicAnnotationCommand : PathAnnotationCommand
{
    public MosaicAnnotationCommand(
        IEnumerable<AnnotationPoint> points,
        AnnotationStyle style,
        double blockSizePixels,
        Guid? id = null)
        : base(points, style, id)
    {
        if (!double.IsFinite(blockSizePixels) || blockSizePixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSizePixels));
        }

        BlockSizePixels = blockSizePixels;
    }

    public double BlockSizePixels { get; }
    public override AnnotationTool Tool => AnnotationTool.Mosaic;
}

public sealed record TextAnnotationCommand : AnnotationCommand
{
    public TextAnnotationCommand(
        string text,
        AnnotationBounds layoutBounds,
        AnnotationStyle style,
        Guid? id = null)
        : base(style, id)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("文字不能为空。", nameof(text));
        if (layoutBounds.Width <= 0 || layoutBounds.Height <= 0)
        {
            throw new ArgumentException("文字布局区域必须具有正宽度和高度。", nameof(layoutBounds));
        }

        Text = text;
        Bounds = layoutBounds;
    }

    public string Text { get; }
    public override AnnotationTool Tool => AnnotationTool.Text;
    public override AnnotationBounds Bounds { get; }

    protected override bool HitTestCore(AnnotationPoint point, double radiusPixels)
    {
        var expanded = new AnnotationBounds(
            Bounds.Left - radiusPixels,
            Bounds.Top - radiusPixels,
            Bounds.Right + radiusPixels,
            Bounds.Bottom + radiusPixels);
        return expanded.Contains(point);
    }
}

public static class AnnotationGeometry
{
    private const double FortyFiveDegrees = Math.PI / 4;

    public static AnnotationPoint ApplyShiftConstraint(
        AnnotationTool tool,
        AnnotationPoint start,
        AnnotationPoint proposedEnd,
        bool shiftPressed)
    {
        if (!shiftPressed) return proposedEnd;
        return tool switch
        {
            AnnotationTool.Rectangle or AnnotationTool.Ellipse =>
                ConstrainToSquare(start, proposedEnd),
            AnnotationTool.Line or AnnotationTool.Polyline or AnnotationTool.Arrow =>
                ConstrainToFortyFiveDegrees(start, proposedEnd),
            _ => proposedEnd
        };
    }

    public static AnnotationPoint ConstrainToSquare(AnnotationPoint start, AnnotationPoint proposedEnd)
    {
        var deltaX = proposedEnd.X - start.X;
        var deltaY = proposedEnd.Y - start.Y;
        var size = Math.Max(Math.Abs(deltaX), Math.Abs(deltaY));
        if (size == 0) return start;
        return new AnnotationPoint(
            start.X + (deltaX < 0 ? -size : size),
            start.Y + (deltaY < 0 ? -size : size));
    }

    public static AnnotationPoint ConstrainToFortyFiveDegrees(
        AnnotationPoint start,
        AnnotationPoint proposedEnd)
    {
        var deltaX = proposedEnd.X - start.X;
        var deltaY = proposedEnd.Y - start.Y;
        var length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (length == 0) return start;

        var angle = Math.Atan2(deltaY, deltaX);
        var constrainedAngle = Math.Round(angle / FortyFiveDegrees) * FortyFiveDegrees;
        return new AnnotationPoint(
            start.X + (Math.Cos(constrainedAngle) * length),
            start.Y + (Math.Sin(constrainedAngle) * length));
    }

    public static AnnotationBounds GetBounds(AnnotationPoint first, AnnotationPoint second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Max(first.X, second.X),
        Math.Max(first.Y, second.Y));

    public static double DistanceToSegment(
        AnnotationPoint point,
        AnnotationPoint segmentStart,
        AnnotationPoint segmentEnd)
    {
        var deltaX = segmentEnd.X - segmentStart.X;
        var deltaY = segmentEnd.Y - segmentStart.Y;
        var squaredLength = (deltaX * deltaX) + (deltaY * deltaY);
        if (squaredLength == 0) return Distance(point, segmentStart);

        var projection = (((point.X - segmentStart.X) * deltaX) +
                          ((point.Y - segmentStart.Y) * deltaY)) / squaredLength;
        projection = Math.Clamp(projection, 0, 1);
        return Distance(point, new AnnotationPoint(
            segmentStart.X + (projection * deltaX),
            segmentStart.Y + (projection * deltaY)));
    }

    public static bool HitTestPath(
        IReadOnlyList<AnnotationPoint> points,
        AnnotationPoint point,
        double radiusPixels)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (!double.IsFinite(radiusPixels) || radiusPixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusPixels));
        }
        if (points.Count == 0) return false;
        if (points.Count == 1) return Distance(points[0], point) <= radiusPixels;

        for (var index = 1; index < points.Count; index++)
        {
            if (DistanceToSegment(point, points[index - 1], points[index]) <= radiusPixels)
            {
                return true;
            }
        }

        return false;
    }

    public static (AnnotationPoint First, AnnotationPoint Second) GetArrowHead(
        AnnotationPoint start,
        AnnotationPoint end,
        double headLengthPixels)
    {
        if (!double.IsFinite(headLengthPixels) || headLengthPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(headLengthPixels));
        }

        var deltaX = end.X - start.X;
        var deltaY = end.Y - start.Y;
        var length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (length == 0) return (end, end);

        var unitX = deltaX / length;
        var unitY = deltaY / length;
        var effectiveLength = Math.Min(headLengthPixels, length);
        var baseX = end.X - (unitX * effectiveLength);
        var baseY = end.Y - (unitY * effectiveLength);
        var halfWidth = effectiveLength * 0.45;
        var perpendicularX = -unitY;
        var perpendicularY = unitX;
        return (
            new AnnotationPoint(
                baseX + (perpendicularX * halfWidth),
                baseY + (perpendicularY * halfWidth)),
            new AnnotationPoint(
                baseX - (perpendicularX * halfWidth),
                baseY - (perpendicularY * halfWidth)));
    }

    private static double Distance(AnnotationPoint first, AnnotationPoint second)
    {
        var deltaX = first.X - second.X;
        var deltaY = first.Y - second.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }
}

public sealed class AnnotationDocument
{
    private readonly List<AnnotationCommand> _commands = [];
    private readonly ReadOnlyCollection<AnnotationCommand> _readOnlyCommands;
    private readonly Stack<AnnotationCommand[]> _undo = new();
    private readonly Stack<AnnotationCommand[]> _redo = new();

    public AnnotationDocument()
    {
        _readOnlyCommands = _commands.AsReadOnly();
    }

    public IReadOnlyList<AnnotationCommand> Commands => _readOnlyCommands;
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;

    public void Commit(AnnotationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_commands.Any(candidate => candidate.Id == command.Id))
        {
            throw new ArgumentException("标注命令 ID 不能重复。", nameof(command));
        }

        SaveUndoSnapshot();
        _commands.Add(command);
    }

    public bool Clear()
    {
        if (_commands.Count == 0) return false;
        SaveUndoSnapshot();
        _commands.Clear();
        return true;
    }

    public void Reset()
    {
        _commands.Clear();
        _undo.Clear();
        _redo.Clear();
    }

    public int Erase(IEnumerable<Guid> commandIds)
    {
        ArgumentNullException.ThrowIfNull(commandIds);
        var ids = commandIds.ToHashSet();
        if (ids.Count == 0) return 0;
        return EraseWhere(command => ids.Contains(command.Id));
    }

    public int EraseAt(IEnumerable<AnnotationPoint> eraserPath, double radiusPixels)
    {
        ArgumentNullException.ThrowIfNull(eraserPath);
        if (!double.IsFinite(radiusPixels) || radiusPixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusPixels));
        }

        var path = eraserPath.ToArray();
        if (path.Length == 0) return 0;
        var samples = SamplePath(path, Math.Max(1, radiusPixels));
        return EraseWhere(command => samples.Any(point => command.HitTest(point, radiusPixels)));
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(_commands.ToArray());
        Restore(_undo.Pop());
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(_commands.ToArray());
        Restore(_redo.Pop());
        return true;
    }

    private int EraseWhere(Predicate<AnnotationCommand> predicate)
    {
        var removedCount = _commands.Count(predicate.Invoke);
        if (removedCount == 0) return 0;
        SaveUndoSnapshot();
        _commands.RemoveAll(predicate);
        return removedCount;
    }

    private void SaveUndoSnapshot()
    {
        _undo.Push(_commands.ToArray());
        _redo.Clear();
    }

    private void Restore(IEnumerable<AnnotationCommand> snapshot)
    {
        _commands.Clear();
        _commands.AddRange(snapshot);
    }

    private static IReadOnlyList<AnnotationPoint> SamplePath(
        IReadOnlyList<AnnotationPoint> path,
        double maximumStep)
    {
        if (path.Count == 1) return path;
        var samples = new List<AnnotationPoint> { path[0] };
        for (var index = 1; index < path.Count; index++)
        {
            var start = path[index - 1];
            var end = path[index];
            var deltaX = end.X - start.X;
            var deltaY = end.Y - start.Y;
            var length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            var steps = Math.Max(1, (int)Math.Ceiling(length / maximumStep));
            for (var step = 1; step <= steps; step++)
            {
                var progress = step / (double)steps;
                samples.Add(new AnnotationPoint(
                    start.X + (deltaX * progress),
                    start.Y + (deltaY * progress)));
            }
        }

        return samples;
    }
}
