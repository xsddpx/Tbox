using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class AnnotationSnapshotDataTests
{
    private static readonly AnnotationStyle Red = new(0xFFFF0000, 4, 18);

    [Fact]
    public void EveryAnnotationToolSurvivesARoundTrip()
    {
        AnnotationCommand[] commands =
        [
            new RectangleAnnotationCommand(new(0, 0), new(20, 10), Red),
            new EllipseAnnotationCommand(new(0, 0), new(20, 10), Red),
            new LineAnnotationCommand(new(0, 0), new(20, 10), Red),
            new PolylineAnnotationCommand([new(0, 0), new(10, 10), new(20, 0)], Red),
            new ArrowAnnotationCommand(new(0, 0), new(20, 10), Red, 14),
            new NumberAnnotationCommand(3, new(10, 10), 24, Red),
            new FreePathAnnotationCommand([new(0, 0), new(10, 10)], Red),
            new HighlighterAnnotationCommand([new(0, 0), new(10, 10)], new AnnotationStyle(0x66FFFF00, 12)),
            new MosaicAnnotationCommand([new(0, 0), new(10, 10)], new AnnotationStyle(0xFF000000, 16), 8),
            new TextAnnotationCommand("Tbox", new AnnotationBounds(1, 2, 50, 24), Red)
        ];
        var restored = AnnotationSnapshotData.From(new AnnotationDocumentSnapshot(commands, [], [])).ToSnapshot().Commands;
        Assert.Equal(commands.Length, restored.Length);
        for (var i = 0; i < commands.Length; i++)
        {
            Assert.Equal(commands[i].GetType(), restored[i].GetType());
            Assert.Equal(commands[i].Id, restored[i].Id);
            Assert.Equal(commands[i].Style, restored[i].Style);
            Assert.Equal(commands[i].Bounds, restored[i].Bounds);
        }
        Assert.Equal(14, ((ArrowAnnotationCommand)restored[4]).HeadLengthPixels);
        Assert.Equal(3, ((NumberAnnotationCommand)restored[5]).Number);
        Assert.Equal(8, ((MosaicAnnotationCommand)restored[8]).BlockSizePixels);
        Assert.Equal("Tbox", ((TextAnnotationCommand)restored[9]).Text);
    }

    [Fact]
    public void UndoAndRedoStatesShareCommandsAndStayEditable()
    {
        var document = new AnnotationDocument();
        document.Commit(new RectangleAnnotationCommand(new(1, 1), new(5, 5), Red));
        document.Commit(new LineAnnotationCommand(new(1, 1), new(9, 9), Red));
        document.Commit(new NumberAnnotationCommand(1, new(4, 4), 22, Red));
        document.Undo();
        var data = AnnotationSnapshotData.From(document.Snapshot());
        Assert.Equal(3, data.Commands.Length);

        var restored = new AnnotationDocument();
        restored.RestoreSnapshot(data.ToSnapshot());
        Assert.Equal(2, restored.Commands.Count);
        Assert.True(restored.Redo());
        Assert.Equal(3, restored.Commands.Count);
        Assert.True(restored.Undo()); Assert.True(restored.Undo());
        Assert.Single(restored.Commands);
        Assert.True(restored.Undo());
        Assert.Empty(restored.Commands);
        Assert.False(restored.Undo());
    }

    [Fact]
    public void ReferencesToMissingCommandsAreRejected()
    {
        var data = new AnnotationSnapshotData([], [0], [], []);
        Assert.Throws<ArgumentException>(() => data.ToSnapshot());
    }
}
