using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Tbox.Core;
using Tbox.Desktop.Models;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class MosaicStrokeTests
{
    [AvaloniaFact]
    public void LiveCellsMatchExportAcrossMissedFramesResizeSourceChangeAndCancellation()
    {
        const int width = 79, height = 57;
        Picture Image(int seed)
        {
            var bytes = new byte[width * height * 4];
            new Random(seed).NextBytes(bytes);
            return Picture.FromPixels(bytes, width, height);
        }

        using var first = Image(42);
        using var second = Image(43);
        var editor = new AnnotationEditor();
        editor.Select(AnnotationTool.Mosaic);

        void Compare(Picture source, double scale)
        {
            var command = Assert.IsType<MosaicAnnotationCommand>(editor.Draft);
            byte[] Draw(bool live)
            {
                using var target = new RenderTargetBitmap(new PixelSize(108, 85));
                using (var context = target.CreateDrawingContext())
                using (context.PushTransform(Matrix.CreateTranslation(-2, -1) * Matrix.CreateScale(scale, scale)))
                {
                    context.DrawImage(source.Bitmap, new Rect(0, 0, width, height));
                    if (live) editor.Draw(context, source);
                    else AnnotationRenderer.Draw(context, source, [command]);
                }
                using var copy = Picture.FromBitmap(target);
                return copy.GetPixels();
            }
            Assert.True(Draw(false).AsSpan().SequenceEqual(Draw(true)));
        }

        editor.Begin(new(76.5, 56.1), false);
        Compare(first, 1);
        editor.Move(new(78, 3), false);
        editor.Move(new(7.25, 12.5), false);
        editor.Move(new(13.25, 35.75), false);
        editor.Move(new(78, 3), false);
        Compare(first, 0.85);
        Compare(first, 1.3);
        Compare(second, 1.3);
        editor.SetSize(7.5);
        editor.Move(new(-7, -2), false);
        Compare(second, 0.85);
        editor.CancelGesture();
        Assert.Null(editor.Draft);
        Assert.Empty(editor.Document.Commands);
        editor.Begin(new(2.5, 5), false);
        Compare(second, 1.3);
    }
}
