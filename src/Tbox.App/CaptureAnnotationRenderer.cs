using System.Numerics;
using Microsoft.UI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Tbox.Core;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace Tbox.App;

internal static class CaptureAnnotationRenderer
{
    private static readonly CanvasStrokeStyle RoundedStroke = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round
    };

    internal static void DrawPreview(
        CanvasDrawingSession drawingSession,
        CapturedScreen screen,
        PixelRegion region,
        IReadOnlyList<AnnotationCommand> commands,
        AnnotationCommand? draft,
        double previewWidth,
        double previewHeight)
    {
        drawingSession.Clear(Colors.Transparent);
        var transform = new RenderTransform(
            region,
            previewWidth / region.Width,
            previewHeight / region.Height);
        foreach (var command in commands) DrawCommand(drawingSession, screen, command, transform);
        if (draft is not null) DrawCommand(drawingSession, screen, draft, transform);
    }

    internal static Task<CapturedScreen> RenderAsync(
        CapturedScreen screen,
        PixelRegion region,
        IReadOnlyList<AnnotationCommand> commands)
    {
        if (commands.Count == 0)
        {
            return Task.FromResult(screen.Crop(region.Left, region.Top, region.Width, region.Height));
        }

        var device = CanvasDevice.GetSharedDevice();
        using var source = CanvasBitmap.CreateFromBytes(
            device,
            screen.Pixels,
            screen.Width,
            screen.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, region.Width, region.Height, 96);
        using (var drawingSession = target.CreateDrawingSession())
        {
            drawingSession.Clear(Colors.Transparent);
            drawingSession.DrawImage(
                source,
                new Rect(0, 0, region.Width, region.Height),
                new Rect(region.Left, region.Top, region.Width, region.Height));
            var transform = new RenderTransform(region, 1, 1);
            foreach (var command in commands) DrawCommand(drawingSession, screen, command, transform);
        }

        var pixels = target.GetPixelBytes();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        return Task.FromResult(new CapturedScreen(
            screen.Left + region.Left,
            screen.Top + region.Top,
            region.Width,
            region.Height,
            pixels));
    }

    private static void DrawCommand(
        CanvasDrawingSession drawingSession,
        CapturedScreen screen,
        AnnotationCommand command,
        RenderTransform transform)
    {
        var color = ToColor(command.Style.ColorArgb);
        var width = (float)Math.Max(1, command.Style.StrokeWidthPixels * transform.AverageScale);
        switch (command)
        {
            case RectangleAnnotationCommand rectangle:
                drawingSession.DrawRectangle(transform.Map(rectangle.Bounds), color, width, RoundedStroke);
                break;
            case EllipseAnnotationCommand ellipse:
                var ellipseBounds = transform.Map(ellipse.Bounds);
                drawingSession.DrawEllipse(
                    new Vector2(
                        (float)(ellipseBounds.X + (ellipseBounds.Width / 2)),
                        (float)(ellipseBounds.Y + (ellipseBounds.Height / 2))),
                    (float)(ellipseBounds.Width / 2),
                    (float)(ellipseBounds.Height / 2),
                    color,
                    width,
                    RoundedStroke);
                break;
            case LineAnnotationCommand line:
                DrawLine(drawingSession, line.Start, line.End, color, width, transform);
                break;
            case ArrowAnnotationCommand arrow:
                DrawLine(drawingSession, arrow.Start, arrow.End, color, width, transform);
                DrawLine(drawingSession, arrow.End, arrow.HeadPoint1, color, width, transform);
                DrawLine(drawingSession, arrow.End, arrow.HeadPoint2, color, width, transform);
                break;
            case NumberAnnotationCommand number:
                DrawNumber(drawingSession, number, color, transform);
                break;
            case PolylineAnnotationCommand polyline:
                DrawPath(drawingSession, polyline.Points, color, width, transform);
                break;
            case FreePathAnnotationCommand pen:
                DrawPath(drawingSession, pen.Points, color, width, transform);
                break;
            case HighlighterAnnotationCommand highlighter:
                DrawPath(drawingSession, highlighter.Points, color, width, transform);
                break;
            case MosaicAnnotationCommand mosaic:
                DrawMosaic(drawingSession, screen, mosaic, transform);
                break;
            case TextAnnotationCommand text:
                using (var format = new CanvasTextFormat
                {
                    FontFamily = "Segoe UI",
                    FontSize = (float)Math.Max(8, text.Style.FontSizePixels * transform.AverageScale),
                    WordWrapping = CanvasWordWrapping.Wrap
                })
                {
                    drawingSession.DrawText(text.Text, transform.Map(text.Bounds), color, format);
                }
                break;
        }
    }

    private static void DrawNumber(
        CanvasDrawingSession drawingSession,
        NumberAnnotationCommand number,
        Color fill,
        RenderTransform transform)
    {
        var diameter = Math.Max(1, number.DiameterPixels * transform.AverageScale);
        drawingSession.FillCircle(transform.Map(number.Center), (float)(diameter / 2), fill);

        var brightness = (fill.R * 0.299) + (fill.G * 0.587) + (fill.B * 0.114);
        var textColor = brightness >= 170 ? Colors.Black : Colors.White;
        var digitScale = number.Number switch
        {
            < 100 => 1,
            < 1000 => 0.82,
            _ => 0.68
        };
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = (float)Math.Max(
                8,
                number.Style.FontSizePixels * transform.AverageScale * digitScale),
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
        drawingSession.DrawText(number.Number.ToString(), transform.Map(number.Bounds), textColor, format);
    }

    private static void DrawLine(
        CanvasDrawingSession drawingSession,
        AnnotationPoint start,
        AnnotationPoint end,
        Color color,
        float width,
        RenderTransform transform)
    {
        drawingSession.DrawLine(transform.Map(start), transform.Map(end), color, width, RoundedStroke);
    }

    private static void DrawPath(
        CanvasDrawingSession drawingSession,
        IReadOnlyList<AnnotationPoint> points,
        Color color,
        float width,
        RenderTransform transform)
    {
        if (points.Count == 1)
        {
            drawingSession.FillCircle(transform.Map(points[0]), width / 2, color);
            return;
        }
        for (var index = 1; index < points.Count; index++)
        {
            DrawLine(drawingSession, points[index - 1], points[index], color, width, transform);
        }
    }

    private static void DrawMosaic(
        CanvasDrawingSession drawingSession,
        CapturedScreen screen,
        MosaicAnnotationCommand mosaic,
        RenderTransform transform)
    {
        var blockSize = Math.Max(2, (int)Math.Round(mosaic.BlockSizePixels));
        var radius = Math.Max(blockSize / 2d, mosaic.Style.StrokeWidthPixels / 2);
        var cells = new HashSet<(int X, int Y)>();
        foreach (var point in SamplePath(mosaic.Points, Math.Max(1, blockSize / 2d)))
        {
            var minimumX = (int)Math.Floor((point.X - radius) / blockSize);
            var maximumX = (int)Math.Floor((point.X + radius) / blockSize);
            var minimumY = (int)Math.Floor((point.Y - radius) / blockSize);
            var maximumY = (int)Math.Floor((point.Y + radius) / blockSize);
            for (var y = minimumY; y <= maximumY; y++)
            {
                for (var x = minimumX; x <= maximumX; x++)
                {
                    cells.Add((x, y));
                }
            }
        }

        foreach (var cell in cells)
        {
            var left = cell.X * blockSize;
            var top = cell.Y * blockSize;
            if (left >= screen.Width || top >= screen.Height || left + blockSize <= 0 || top + blockSize <= 0) continue;
            var color = SampleAverageColor(screen, left, top, blockSize, blockSize);
            drawingSession.FillRectangle(
                transform.Map(new AnnotationBounds(left, top, left + blockSize, top + blockSize)),
                color);
        }
    }

    private static IEnumerable<AnnotationPoint> SamplePath(IReadOnlyList<AnnotationPoint> points, double maximumStep)
    {
        if (points.Count == 0) yield break;
        yield return points[0];
        for (var index = 1; index < points.Count; index++)
        {
            var start = points[index - 1];
            var end = points[index];
            var deltaX = end.X - start.X;
            var deltaY = end.Y - start.Y;
            var length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            var steps = Math.Max(1, (int)Math.Ceiling(length / maximumStep));
            for (var step = 1; step <= steps; step++)
            {
                var progress = step / (double)steps;
                yield return new AnnotationPoint(
                    start.X + (deltaX * progress),
                    start.Y + (deltaY * progress));
            }
        }
    }

    private static Color SampleAverageColor(
        CapturedScreen screen,
        int left,
        int top,
        int width,
        int height)
    {
        var right = Math.Clamp(left + width, 0, screen.Width);
        var bottom = Math.Clamp(top + height, 0, screen.Height);
        left = Math.Clamp(left, 0, screen.Width);
        top = Math.Clamp(top, 0, screen.Height);
        long red = 0;
        long green = 0;
        long blue = 0;
        var count = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var offset = ((y * screen.Width) + x) * 4;
                blue += screen.Pixels[offset];
                green += screen.Pixels[offset + 1];
                red += screen.Pixels[offset + 2];
                count++;
            }
        }
        if (count == 0) return Colors.Transparent;
        return Color.FromArgb(255, (byte)(red / count), (byte)(green / count), (byte)(blue / count));
    }

    private static Color ToColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    private readonly record struct RenderTransform(PixelRegion Region, double ScaleX, double ScaleY)
    {
        internal double AverageScale => (ScaleX + ScaleY) / 2;

        internal Vector2 Map(AnnotationPoint point) => new(
            (float)((point.X - Region.Left) * ScaleX),
            (float)((point.Y - Region.Top) * ScaleY));

        internal Rect Map(AnnotationBounds bounds) => new(
            (bounds.Left - Region.Left) * ScaleX,
            (bounds.Top - Region.Top) * ScaleY,
            bounds.Width * ScaleX,
            bounds.Height * ScaleY);
    }
}
