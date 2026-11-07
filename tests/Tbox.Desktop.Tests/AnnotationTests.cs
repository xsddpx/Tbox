using Avalonia.Headless.XUnit;
using Tbox.Core;
using Tbox.Desktop.Models;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class AnnotationTests
{
    [Fact]
    public void CancelledGesturesNeverEnterUndoHistory()
    {
        var editor = new AnnotationEditor(); editor.Select(AnnotationTool.Arrow);
        editor.Begin(new AnnotationPoint(1, 1), false); editor.Move(new AnnotationPoint(30, 30), false);
        editor.CancelGesture();
        Assert.Empty(editor.Document.Commands); Assert.False(editor.Document.CanUndo); Assert.Null(editor.Draft);
    }

    [Fact]
    public void NumberToolsReuseUndoneNumbersAndPreserveSize()
    {
        var editor = new AnnotationEditor(); editor.Select(AnnotationTool.Number);
        editor.Begin(new AnnotationPoint(20, 20), false); editor.Begin(new AnnotationPoint(50, 20), false);
        editor.Undo(); editor.Begin(new AnnotationPoint(60, 20), false);
        Assert.Equal(new[] { 1, 2 }, editor.Document.Commands.Cast<NumberAnnotationCommand>().Select(c => c.Number));
        Assert.All(editor.Document.Commands.Cast<NumberAnnotationCommand>(), c => Assert.Equal(22, c.DiameterPixels));
    }

    [Fact]
    public void ShiftConstrainsShapesAndCompletingPolylineDropsTheHoverPoint()
    {
        var editor = new AnnotationEditor(); editor.Select(AnnotationTool.Rectangle);
        editor.Begin(new AnnotationPoint(10, 10), true); editor.End(new AnnotationPoint(40, 25), true);
        var box = Assert.IsType<RectangleAnnotationCommand>(Assert.Single(editor.Document.Commands));
        Assert.Equal(box.Bounds.Width, box.Bounds.Height);
        editor.Select(AnnotationTool.Polyline);
        editor.Begin(new AnnotationPoint(10, 10), false); editor.Begin(new AnnotationPoint(30, 30), false);
        editor.Move(new AnnotationPoint(100, 100), false); editor.FinishPolyline();
        var path = Assert.IsType<PolylineAnnotationCommand>(editor.Document.Commands[1]);
        Assert.Equal(2, path.Points.Count);
        Assert.Equal(new AnnotationPoint(30, 30), path.Points[^1]);
    }

    [Fact]
    public void PinTranslationKeepsCommandIdentityAndMovesOriginalCoordinates()
    {
        var original = new ArrowAnnotationCommand(new AnnotationPoint(100, 100), new AnnotationPoint(150, 130), AnnotationStyle.Default);
        var translated = Assert.IsType<ArrowAnnotationCommand>(AnnotationTransform.Translate(original, -80, -90));
        Assert.Equal(original.Id, translated.Id);
        Assert.Equal(new AnnotationPoint(20, 10), translated.Start);
        Assert.Equal(new AnnotationPoint(70, 40), translated.End);
        Assert.Equal(original.Style, translated.Style);
    }

    [AvaloniaFact]
    public void CompositingAnnotationsLeavesOriginalPixelsAvailableForOcr()
    {
        using var original = DesktopController.RenderText("OCR 原图");
        var before = original.GetPixels().ToArray();
        var region = new PixelRegion(0, 0, original.Bitmap.PixelSize.Width, original.Bitmap.PixelSize.Height);
        var commands = new AnnotationCommand[] { new RectangleAnnotationCommand(new AnnotationPoint(10, 10),
            new AnnotationPoint(50, 50), new AnnotationStyle(0xFFFF0000, 10)) };
        using var annotated = AnnotationRenderer.Render(original, region, commands);
        Assert.Equal(before, original.GetPixels());
        Assert.NotEqual(before, annotated.GetPixels());
        using var rawCrop = original.Crop(new PixelRegion(0, 0, 64, 64));
        var expected = CaptureRegionPolicy.CropBgra32(before, region.Width, region.Height, new PixelRegion(0, 0, 64, 64));
        Assert.Equal(expected, rawCrop.GetPixels());
    }
}
