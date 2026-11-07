using Tbox.Core;

namespace Tbox.Desktop.Models;

public static class AnnotationTransform
{
    public static AnnotationCommand Translate(AnnotationCommand command, double x, double y)
    {
        AnnotationPoint P(AnnotationPoint p) => new(p.X + x, p.Y + y);
        AnnotationBounds B(AnnotationBounds b) => new(b.Left + x, b.Top + y, b.Right + x, b.Bottom + y);
        return command switch
        {
            RectangleAnnotationCommand c => new RectangleAnnotationCommand(P(c.Start), P(c.End), c.Style, c.Id),
            EllipseAnnotationCommand c => new EllipseAnnotationCommand(P(c.Start), P(c.End), c.Style, c.Id),
            LineAnnotationCommand c => new LineAnnotationCommand(P(c.Start), P(c.End), c.Style, c.Id),
            ArrowAnnotationCommand c => new ArrowAnnotationCommand(P(c.Start), P(c.End), c.Style, c.HeadLengthPixels, c.Id),
            NumberAnnotationCommand c => new NumberAnnotationCommand(c.Number, P(c.Center), c.DiameterPixels, c.Style, c.Id),
            PolylineAnnotationCommand c => new PolylineAnnotationCommand(c.Points.Select(P), c.Style, c.Id),
            FreePathAnnotationCommand c => new FreePathAnnotationCommand(c.Points.Select(P), c.Style, c.Id),
            HighlighterAnnotationCommand c => new HighlighterAnnotationCommand(c.Points.Select(P), c.Style, c.Id),
            MosaicAnnotationCommand c => new MosaicAnnotationCommand(c.Points.Select(P), c.Style, c.BlockSizePixels, c.Id),
            TextAnnotationCommand c => new TextAnnotationCommand(c.Text, B(c.Bounds), c.Style, c.Id),
            _ => throw new NotSupportedException($"不支持转换标注 {command.Tool}。")
        };
    }
}
