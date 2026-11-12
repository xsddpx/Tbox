using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class CaptureFrameHistoryTests
{
    [Fact]
    public void HistoryEvictsOldFramesAndSkipsChangedDisplays()
    {
        var history = new CaptureFrameHistory();
        var document = new AnnotationDocument();
        var selection = new CaptureSelectionBounds(1, 2, 3, 4);
        for (var i = 0; i < CaptureFrameHistory.MaximumCount + 2; i++)
            history.Add(new CaptureFrame([new CapturedDisplay(1, 0, 0, 100, 80, [(byte)i])], 1, selection, document.Snapshot()));
        var compatible = history.CompatibleWith([new CapturedDisplay(1, 0, 0, 100, 80, [])]);
        Assert.Equal(CaptureFrameHistory.MaximumCount, compatible.Count);
        Assert.Equal(11, compatible[0].Displays[0].Png[0]);
        Assert.Equal(2, compatible[^1].Displays[0].Png[0]);
        Assert.Empty(history.CompatibleWith([new CapturedDisplay(1, 0, 0, 200, 80, [])]));
    }

    [Fact]
    public void RestoredAnnotationsKeepIndependentUndoAndRedoStacks()
    {
        var original = new AnnotationDocument();
        original.Commit(new RectangleAnnotationCommand(new(1, 1), new(5, 5), AnnotationStyle.Default));
        original.Commit(new RectangleAnnotationCommand(new(10, 10), new(15, 15), AnnotationStyle.Default));
        Assert.True(original.Undo());
        var copy = new AnnotationDocument();
        copy.RestoreSnapshot(original.Snapshot());
        Assert.True(copy.Redo());
        Assert.Equal(2, copy.Commands.Count);
        Assert.Single(original.Commands);
        Assert.True(copy.Undo());
        Assert.True(copy.Undo());
        Assert.Empty(copy.Commands);
        Assert.Single(original.Commands);
    }

    [Fact]
    public void DifferentPngPixelDimensionsMakeAnOtherwiseMatchingDisplayIncompatible()
    {
        static byte[] PngHeader(int width, int height)
        {
            byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
                0, 0, 0, 0, 0, 0, 0, 0];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), width);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), height);
            return png;
        }
        var history = new CaptureFrameHistory();
        history.Add(new CaptureFrame([new CapturedDisplay(1, 0, 0, 100, 80, PngHeader(200, 160))],
            1, new CaptureSelectionBounds(1, 1, 20, 20), new AnnotationDocument().Snapshot()));
        Assert.Empty(history.CompatibleWith([new CapturedDisplay(1, 0, 0, 100, 80, PngHeader(100, 80))]));
        Assert.Single(history.CompatibleWith([new CapturedDisplay(1, 0, 0, 100, 80, PngHeader(200, 160))]));
    }

    [Fact]
    public void HistoryRequiresEveryDisplayToMatchTheCapturedDesktop()
    {
        var first = new CapturedDisplay(1, 0, 0, 100, 80, [1]);
        var second = new CapturedDisplay(2, 100, 0, 100, 80, [2]);
        var history = new CaptureFrameHistory();
        history.Add(new CaptureFrame([first, second], 1, new CaptureSelectionBounds(1, 2, 3, 4), new AnnotationDocument().Snapshot()));
        Assert.Single(history.CompatibleWith([second, first]));
        Assert.Empty(history.CompatibleWith([first]));
        Assert.Empty(history.CompatibleWith([first, second with { Left = 101 }]));
    }
}
