namespace Tbox.Core;

// Plain data for saving an editable annotation history. A command shared by the current list and
// several undo/redo states is stored once and referenced by index.
public sealed record AnnotationSnapshotData(AnnotationCommandData[] Commands, int[] Current, int[][] Undo, int[][] Redo)
{
    public static AnnotationSnapshotData From(AnnotationDocumentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var commands = new List<AnnotationCommandData>();
        var indexes = new Dictionary<AnnotationCommand, int>(ReferenceEqualityComparer.Instance);
        int[] Encode(AnnotationCommand[] state) => state.Select(command =>
        {
            if (indexes.TryGetValue(command, out var index)) return index;
            commands.Add(AnnotationCommandData.From(command));
            return indexes[command] = commands.Count - 1;
        }).ToArray();
        var current = Encode(snapshot.Commands);
        var undo = snapshot.Undo.Select(Encode).ToArray();
        var redo = snapshot.Redo.Select(Encode).ToArray();
        return new AnnotationSnapshotData(commands.ToArray(), current, undo, redo);
    }

    public AnnotationDocumentSnapshot ToSnapshot()
    {
        var commands = (Commands ?? []).Select(command => command.ToCommand()).ToArray();
        AnnotationCommand[] Decode(int[]? state) => (state ?? []).Select(index =>
            index >= 0 && index < commands.Length ? commands[index] : throw new ArgumentException("标注历史引用了不存在的命令。")).ToArray();
        return new AnnotationDocumentSnapshot(Decode(Current), (Undo ?? []).Select(Decode).ToArray(), (Redo ?? []).Select(Decode).ToArray());
    }
}

// Points are flattened as x0, y0, x1, y1… Text stores its layout box as two corners.
public sealed record AnnotationCommandData(AnnotationTool Tool, Guid Id, uint Color, double StrokeWidth, double FontSize,
    double[] Points, double? Size = null, int? Number = null, string? Text = null)
{
    public static AnnotationCommandData From(AnnotationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        AnnotationCommandData Data(IEnumerable<AnnotationPoint> points, double? size = null, int? number = null, string? text = null) =>
            new(command.Tool, command.Id, command.Style.ColorArgb, command.Style.StrokeWidthPixels, command.Style.FontSizePixels,
                points.SelectMany(point => new[] { point.X, point.Y }).ToArray(), size, number, text);
        return command switch
        {
            RectangleAnnotationCommand c => Data([c.Start, c.End]),
            EllipseAnnotationCommand c => Data([c.Start, c.End]),
            LineAnnotationCommand c => Data([c.Start, c.End]),
            ArrowAnnotationCommand c => Data([c.Start, c.End], c.HeadLengthPixels),
            NumberAnnotationCommand c => Data([c.Center], c.DiameterPixels, c.Number),
            MosaicAnnotationCommand c => Data(c.Points, c.BlockSizePixels),
            PathAnnotationCommand c => Data(c.Points),
            TextAnnotationCommand c => Data([new(c.Bounds.Left, c.Bounds.Top), new(c.Bounds.Right, c.Bounds.Bottom)], text: c.Text),
            _ => throw new NotSupportedException($"不支持保存标注 {command.Tool}。")
        };
    }

    public AnnotationCommand ToCommand()
    {
        var values = Points ?? [];
        if (values.Length == 0 || values.Length % 2 != 0) throw new ArgumentException("标注坐标数据不完整。");
        var points = Enumerable.Range(0, values.Length / 2).Select(i => new AnnotationPoint(values[i * 2], values[i * 2 + 1])).ToArray();
        var style = new AnnotationStyle(Color, StrokeWidth, FontSize);
        AnnotationPoint At(int index) => index < points.Length ? points[index] : throw new ArgumentException("标注坐标数据不完整。");
        double Required(double? value) => value ?? throw new ArgumentException("标注缺少尺寸。");
        return Tool switch
        {
            AnnotationTool.Rectangle => new RectangleAnnotationCommand(At(0), At(1), style, Id),
            AnnotationTool.Ellipse => new EllipseAnnotationCommand(At(0), At(1), style, Id),
            AnnotationTool.Line => new LineAnnotationCommand(At(0), At(1), style, Id),
            AnnotationTool.Arrow => new ArrowAnnotationCommand(At(0), At(1), style, Size, Id),
            AnnotationTool.Number => new NumberAnnotationCommand(Number ?? 0, At(0), Required(Size), style, Id),
            AnnotationTool.Polyline => new PolylineAnnotationCommand(points, style, Id),
            AnnotationTool.Pen => new FreePathAnnotationCommand(points, style, Id),
            AnnotationTool.Highlighter => new HighlighterAnnotationCommand(points, style, Id),
            AnnotationTool.Mosaic => new MosaicAnnotationCommand(points, style, Required(Size), Id),
            AnnotationTool.Text => new TextAnnotationCommand(Text ?? "", new AnnotationBounds(At(0).X, At(0).Y, At(1).X, At(1).Y), style, Id),
            _ => throw new ArgumentException($"不支持读取标注 {Tool}。")
        };
    }
}
