using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class AnnotationDocumentTests
{
    private static readonly AnnotationStyle Red = new(0xFFFF0000, 4, 18);

    [Fact]
    public void NumberAnnotationDefaultDiameterIsTwentyTwo()
    {
        Assert.Equal(22, AnnotationDefaults.NumberDiameter);
    }

    [Fact]
    public void ExposesAllDrawingToolsAndCommands()
    {
        var commands = new AnnotationCommand[]
        {
            new RectangleAnnotationCommand(Point(0, 0), Point(20, 10), Red),
            new EllipseAnnotationCommand(Point(0, 0), Point(20, 10), Red),
            new LineAnnotationCommand(Point(0, 0), Point(20, 10), Red),
            new PolylineAnnotationCommand([Point(0, 0), Point(10, 10), Point(20, 0)], Red),
            new ArrowAnnotationCommand(Point(0, 0), Point(20, 10), Red),
            new NumberAnnotationCommand(1, Point(10, 10), 24, Red),
            new FreePathAnnotationCommand([Point(0, 0), Point(10, 10)], Red),
            new HighlighterAnnotationCommand([Point(0, 0), Point(10, 10)], new AnnotationStyle(0x66FFFF00, 12)),
            new MosaicAnnotationCommand([Point(0, 0), Point(10, 10)], new AnnotationStyle(0xFF000000, 16), 8),
            new TextAnnotationCommand("Tbox", new AnnotationBounds(0, 0, 50, 24), Red)
        };

        Assert.Equal(
            [
                AnnotationTool.Rectangle,
                AnnotationTool.Ellipse,
                AnnotationTool.Line,
                AnnotationTool.Polyline,
                AnnotationTool.Arrow,
                AnnotationTool.Number,
                AnnotationTool.Pen,
                AnnotationTool.Highlighter,
                AnnotationTool.Mosaic,
                AnnotationTool.Text
            ],
            commands.Select(command => command.Tool));
        Assert.Contains(AnnotationTool.Eraser, Enum.GetValues<AnnotationTool>());
    }

    [Fact]
    public void NumberAnnotationsAutoIncrementAndUndoReusesTheRemovedNumber()
    {
        var document = new AnnotationDocument();
        Assert.Equal(1, document.NextNumber);

        var first = new NumberAnnotationCommand(document.NextNumber, Point(10, 10), 24, Red);
        document.Commit(first);
        var second = new NumberAnnotationCommand(document.NextNumber, Point(40, 10), 24, Red);
        document.Commit(second);

        Assert.Equal(3, document.NextNumber);
        Assert.True(document.Undo());
        Assert.Equal(2, document.NextNumber);
        Assert.True(document.Redo());
        Assert.Equal(3, document.NextNumber);
    }

    [Fact]
    public void NumberAnnotationUsesCircularBoundsAndHitTesting()
    {
        var number = new NumberAnnotationCommand(12, Point(20, 20), 24, Red);

        Assert.Equal(new AnnotationBounds(8, 8, 32, 32), number.Bounds);
        Assert.True(number.HitTest(Point(20, 20)));
        Assert.True(number.HitTest(Point(32, 20)));
        Assert.False(number.HitTest(Point(40, 20)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NumberAnnotationCommand(0, Point(20, 20), 24, Red));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NumberAnnotationCommand(1, Point(20, 20), 0, Red));
    }

    [Fact]
    public void CommitUndoRedoAndClearPreserveCommandOrder()
    {
        var document = new AnnotationDocument();
        var first = new LineAnnotationCommand(Point(0, 0), Point(10, 0), Red);
        var second = new RectangleAnnotationCommand(Point(10, 10), Point(30, 30), Red);

        document.Commit(first);
        document.Commit(second);

        Assert.Equal([first, second], document.Commands);
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);

        Assert.True(document.Undo());
        Assert.Equal([first], document.Commands);
        Assert.True(document.CanRedo);

        Assert.True(document.Redo());
        Assert.Equal([first, second], document.Commands);

        Assert.True(document.Clear());
        Assert.Empty(document.Commands);
        Assert.True(document.Undo());
        Assert.Equal([first, second], document.Commands);
    }

    [Fact]
    public void CommitAfterUndoDiscardsRedoBranch()
    {
        var document = new AnnotationDocument();
        var first = new LineAnnotationCommand(Point(0, 0), Point(10, 0), Red);
        var discarded = new LineAnnotationCommand(Point(0, 10), Point(10, 10), Red);
        var replacement = new LineAnnotationCommand(Point(0, 20), Point(10, 20), Red);
        document.Commit(first);
        document.Commit(discarded);
        document.Undo();

        document.Commit(replacement);

        Assert.Equal([first, replacement], document.Commands);
        Assert.False(document.CanRedo);
        Assert.False(document.Redo());
    }

    [Fact]
    public void BatchEraseIsOneUndoableTransaction()
    {
        var document = new AnnotationDocument();
        var first = new LineAnnotationCommand(Point(0, 0), Point(10, 0), Red);
        var untouched = new LineAnnotationCommand(Point(0, 10), Point(10, 10), Red);
        var third = new LineAnnotationCommand(Point(0, 20), Point(10, 20), Red);
        document.Commit(first);
        document.Commit(untouched);
        document.Commit(third);

        Assert.Equal(2, document.Erase([first.Id, third.Id]));
        Assert.Equal([untouched], document.Commands);

        Assert.True(document.Undo());
        Assert.Equal([first, untouched, third], document.Commands);
        Assert.True(document.Redo());
        Assert.Equal([untouched], document.Commands);
    }

    [Fact]
    public void EraserPathDeletesEveryIntersectedCommandAsOneTransaction()
    {
        var document = new AnnotationDocument();
        var top = new LineAnnotationCommand(Point(0, 5), Point(20, 5), Red);
        var bottom = new LineAnnotationCommand(Point(0, 15), Point(20, 15), Red);
        var untouched = new LineAnnotationCommand(Point(0, 40), Point(20, 40), Red);
        document.Commit(top);
        document.Commit(bottom);
        document.Commit(untouched);

        var removed = document.EraseAt([Point(10, 0), Point(10, 20)], radiusPixels: 1);

        Assert.Equal(2, removed);
        Assert.Equal([untouched], document.Commands);
        Assert.True(document.Undo());
        Assert.Equal([top, bottom, untouched], document.Commands);
    }

    [Fact]
    public void ShiftConstrainsShapesToSquareAndLinesToFortyFiveDegreeSteps()
    {
        var squareEnd = AnnotationGeometry.ApplyShiftConstraint(
            AnnotationTool.Rectangle,
            Point(10, 10),
            Point(30, 22),
            shiftPressed: true);
        Assert.Equal(Point(30, 30), squareEnd);

        var lineEnd = AnnotationGeometry.ApplyShiftConstraint(
            AnnotationTool.Arrow,
            Point(0, 0),
            Point(10, 7),
            shiftPressed: true);
        Assert.Equal(lineEnd.X, lineEnd.Y, precision: 10);
        Assert.Equal(Math.Sqrt(149), Math.Sqrt((lineEnd.X * lineEnd.X) + (lineEnd.Y * lineEnd.Y)), precision: 10);

        Assert.Equal(
            Point(10, 7),
            AnnotationGeometry.ApplyShiftConstraint(
                AnnotationTool.Line,
                Point(0, 0),
                Point(10, 7),
                shiftPressed: false));
    }

    [Fact]
    public void ShapeAndPathHitTestingUsesStrokeAndTolerance()
    {
        var rectangle = new RectangleAnnotationCommand(Point(0, 0), Point(20, 10), Red);
        Assert.True(rectangle.HitTest(Point(10, 1)));
        Assert.False(rectangle.HitTest(Point(10, 5)));

        var ellipse = new EllipseAnnotationCommand(Point(0, 0), Point(20, 10), Red);
        Assert.True(ellipse.HitTest(Point(10, 0)));
        Assert.False(ellipse.HitTest(Point(10, 5)));

        var line = new LineAnnotationCommand(Point(0, 0), Point(20, 0), Red);
        Assert.True(line.HitTest(Point(10, 3), tolerancePixels: 1));
        Assert.False(line.HitTest(Point(10, 4.1), tolerancePixels: 1));

        var arrow = new ArrowAnnotationCommand(Point(0, 0), Point(20, 0), Red, headLengthPixels: 8);
        Assert.True(arrow.HitTest(arrow.HeadPoint1));

        AnnotationCommand[] paths =
        [
            new PolylineAnnotationCommand([Point(0, 0), Point(20, 0)], Red),
            new FreePathAnnotationCommand([Point(0, 0), Point(20, 0)], Red),
            new HighlighterAnnotationCommand([Point(0, 0), Point(20, 0)], new AnnotationStyle(0x66FFFF00, 12)),
            new MosaicAnnotationCommand([Point(0, 0), Point(20, 0)], new AnnotationStyle(0xFF000000, 12), 6)
        ];
        Assert.All(paths, path => Assert.True(path.HitTest(Point(10, 1))));
        Assert.All(paths, path => Assert.False(path.HitTest(Point(10, 30))));

        var text = new TextAnnotationCommand("hello", new AnnotationBounds(10, 10, 40, 30), Red);
        Assert.True(text.HitTest(Point(20, 20)));
        Assert.False(text.HitTest(Point(50, 20)));
    }

    private static AnnotationPoint Point(double x, double y) => new(x, y);
}
