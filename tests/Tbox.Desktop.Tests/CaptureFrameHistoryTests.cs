using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class CaptureFrameHistoryTests
{
    private static CaptureFrame Frame(CapturedDisplay display, AnnotationDocumentSnapshot? annotations = null) =>
        new([display], display.Id, new CaptureSelectionBounds(1, 2, 3, 4), annotations ?? new AnnotationDocument().Snapshot());

    [AvaloniaFact]
    public async Task UnsavedHistoryKeepsOnlyTheNewestFramesInMemoryAndSkipsChangedDisplays()
    {
        var history = new CaptureFrameHistory();
        for (var i = 0; i < CaptureFrameHistory.MemoryCount + 2; i++)
            await history.AddAsync(Frame(new CapturedDisplay(1, 0, 0, 100, 80, [(byte)i])));
        var compatible = history.CompatibleWith([new CapturedDisplay(1, 0, 0, 100, 80, [])]);
        Assert.Equal(CaptureFrameHistory.MemoryCount, compatible.Count);
        Assert.Equal(11, compatible[0].Frame!.Displays[0].Png[0]);
        Assert.Equal(2, compatible[^1].Frame!.Displays[0].Png[0]);
        Assert.Empty(history.CompatibleWith([new CapturedDisplay(1, 0, 0, 200, 80, [])]));
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
    public async Task DifferentPngPixelDimensionsMakeAnOtherwiseMatchingDisplayIncompatible()
    {
        var history = new CaptureFrameHistory();
        await history.AddAsync(Frame(new CapturedDisplay(1, 0, 0, 100, 80, PngHeader(200, 160))));
        Assert.Empty(history.CompatibleWith([new CapturedDisplay(1, 0, 0, 100, 80, PngHeader(100, 80))]));
        Assert.Single(history.CompatibleWith([new CapturedDisplay(1, 0, 0, 100, 80, PngHeader(200, 160))]));
    }

    [AvaloniaFact]
    public async Task HistoryRequiresEveryDisplayToMatchTheCapturedDesktop()
    {
        var first = new CapturedDisplay(1, 0, 0, 100, 80, [1]);
        var second = new CapturedDisplay(2, 100, 0, 100, 80, [2]);
        var history = new CaptureFrameHistory();
        await history.AddAsync(new CaptureFrame([first, second], 1, new CaptureSelectionBounds(1, 2, 3, 4), new AnnotationDocument().Snapshot()));
        Assert.Single(history.CompatibleWith([second, first]));
        Assert.Empty(history.CompatibleWith([first]));
        Assert.Empty(history.CompatibleWith([first, second with { Left = 101 }]));
    }

    [AvaloniaFact]
    public async Task SavedHistoryIsReadBackAfterARestartWithFramesSelectionAndEditableAnnotations()
    {
        using var folder = new TemporaryFolder();
        var png = TestPng(4, 3);
        var display = new CapturedDisplay(1, 0, 0, 4, 3, png);
        var document = new AnnotationDocument();
        document.Commit(new RectangleAnnotationCommand(new(1, 1), new(3, 2), AnnotationStyle.Default));
        document.Commit(new TextAnnotationCommand("Tbox", new AnnotationBounds(0, 0, 4, 2), AnnotationStyle.Default));
        document.Undo();
        var first = new CaptureFrameHistory();
        await first.OpenAsync(folder.Path);
        await first.AddAsync(new CaptureFrame([display], 1, new CaptureSelectionBounds(1, 0.5, 2, 2), document.Snapshot()));
        Assert.Single(Directory.GetDirectories(CaptureFrameStore.Root(folder.Path)));

        var restarted = new CaptureFrameHistory();
        await restarted.OpenAsync(folder.Path);
        var entry = Assert.Single(restarted.CompatibleWith([new CapturedDisplay(1, 0, 0, 4, 3, png)]));
        Assert.Null(entry.Frame);
        var loaded = CaptureFrameHistory.Load(entry);
        Assert.Equal(new CaptureSelectionBounds(1, 0.5, 2, 2), loaded.Selection);
        Assert.Equal(png, loaded.Displays[0].Png);
        var restored = new AnnotationDocument();
        restored.RestoreSnapshot(loaded.Annotations);
        Assert.Single(restored.Commands);
        Assert.True(restored.Redo());
        Assert.Equal("Tbox", Assert.IsType<TextAnnotationCommand>(restored.Commands[1]).Text);
    }

    [AvaloniaFact]
    public async Task SavedHistoryDropsRecordsOlderThanSevenDaysAndBeyondFifty()
    {
        using var folder = new TemporaryFolder();
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var clock = now.AddDays(-8);
        var history = new CaptureFrameHistory(() => clock);
        await history.OpenAsync(folder.Path);
        var display = new CapturedDisplay(1, 0, 0, 4, 3, TestPng(4, 3));
        await history.AddAsync(Frame(display));
        for (var i = 0; i < CaptureHistoryRetention.MaximumCount + 1; i++)
        {
            clock = now.AddMinutes(i);
            await history.AddAsync(Frame(display));
        }
        Assert.Equal(CaptureHistoryRetention.MaximumCount, history.Count);
        Assert.Equal(CaptureHistoryRetention.MaximumCount, Directory.GetDirectories(CaptureFrameStore.Root(folder.Path)).Length);
    }

    [AvaloniaFact]
    public async Task ClearingHistoryRemovesOnlyTboxRecordsFromTheHistoryFolder()
    {
        using var folder = new TemporaryFolder();
        var history = new CaptureFrameHistory();
        await history.OpenAsync(folder.Path);
        await history.AddAsync(Frame(new CapturedDisplay(1, 0, 0, 4, 3, TestPng(4, 3))));
        var foreign = Directory.CreateDirectory(Path.Combine(CaptureFrameStore.Root(folder.Path), "my-notes")).FullName;
        await history.ClearAsync();
        Assert.Equal(0, history.Count);
        Assert.Equal([foreign], Directory.GetDirectories(CaptureFrameStore.Root(folder.Path)));
    }

    [AvaloniaFact]
    public async Task DamagedAndInterruptedRecordsAreRemovedWhenHistoryIsRead()
    {
        using var folder = new TemporaryFolder();
        var root = CaptureFrameStore.Root(folder.Path);
        var damaged = Directory.CreateDirectory(Path.Combine(root, "20261116-120000-000-0123abcd")).FullName;
        File.WriteAllText(Path.Combine(damaged, "frame.json"), "{");
        var interrupted = Directory.CreateDirectory(Path.Combine(root, "20261116-120001-000-0123abcd.tmp")).FullName;
        Directory.SetLastWriteTimeUtc(interrupted, DateTime.UtcNow.AddHours(-1));
        var saving = Directory.CreateDirectory(Path.Combine(root, "20261116-120002-000-0123abcd.tmp")).FullName;
        var history = new CaptureFrameHistory();
        await history.OpenAsync(folder.Path);
        Assert.Equal(0, history.Count);
        Assert.False(Directory.Exists(damaged));
        Assert.False(Directory.Exists(interrupted));
        Assert.True(Directory.Exists(saving));
    }

    [AvaloniaFact]
    public async Task CapturesMadeBeforeTheHistoryFolderIsOpenedAreKept()
    {
        using var folder = new TemporaryFolder();
        var history = new CaptureFrameHistory();
        await history.AddAsync(Frame(new CapturedDisplay(1, 0, 0, 4, 3, TestPng(4, 3))));
        await history.OpenAsync(folder.Path);
        Assert.Equal(1, history.Count);
    }

    [AvaloniaFact]
    public async Task ARecordWhoseImageNoLongerMatchesIsRejectedWhenLoaded()
    {
        using var folder = new TemporaryFolder();
        var history = new CaptureFrameHistory();
        await history.OpenAsync(folder.Path);
        await history.AddAsync(Frame(new CapturedDisplay(1, 0, 0, 4, 3, TestPng(4, 3))));
        var record = Assert.Single(Directory.GetDirectories(CaptureFrameStore.Root(folder.Path)));
        File.WriteAllBytes(Path.Combine(record, "display-1.png"), TestPng(2, 2));
        var restarted = new CaptureFrameHistory();
        await restarted.OpenAsync(folder.Path);
        var entry = Assert.Single(restarted.CompatibleWith([new CapturedDisplay(1, 0, 0, 4, 3, TestPng(4, 3))]));
        Assert.Throws<InvalidDataException>(() => CaptureFrameHistory.Load(entry));
    }

    internal static byte[] TestPng(int width, int height)
    {
        using var picture = Picture.FromPixels(Enumerable.Repeat((byte)200, width * height * 4).ToArray(), width, height);
        return picture.Png;
    }

    private static byte[] PngHeader(int width, int height)
    {
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 0, 0, 0, 0, 0];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), height);
        return png;
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "tbox-frame-history-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
