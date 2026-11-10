using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Tbox.Core;

namespace Tbox.Desktop.Models;

public static class AnnotationRenderer
{
    public static Picture Render(Picture original, PixelRegion region, IReadOnlyList<AnnotationCommand> commands)
    {
        if (commands.Count == 0) return original.Crop(region);
        using var target = new RenderTargetBitmap(new PixelSize(region.Width, region.Height), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(original.Bitmap, new Rect(region.Left, region.Top, region.Width, region.Height),
                new Rect(0, 0, region.Width, region.Height));
            using (context.PushTransform(Matrix.CreateTranslation(-region.Left, -region.Top)))
                Draw(context, original, commands);
        }
        return Picture.FromBitmap(target);
    }

    public static void Draw(DrawingContext context, Picture original, IEnumerable<AnnotationCommand> commands)
    {
        foreach (var command in commands)
        {
            var brush = new SolidColorBrush(Color.FromUInt32(command.Style.ColorArgb));
            var pen = new Pen(brush, command.Style.StrokeWidthPixels) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var b = command.Bounds;
            var rect = new Rect(b.Left, b.Top, b.Width, b.Height);
            switch (command)
            {
                case RectangleAnnotationCommand:
                    context.DrawRectangle(null, pen, rect); break;
                case EllipseAnnotationCommand:
                    context.DrawEllipse(null, pen, rect); break;
                case LineAnnotationCommand line:
                    context.DrawLine(pen, P(line.Start), P(line.End)); break;
                case ArrowAnnotationCommand arrow:
                    context.DrawLine(pen, P(arrow.Start), P(arrow.End));
                    context.DrawLine(pen, P(arrow.End), P(arrow.HeadPoint1));
                    context.DrawLine(pen, P(arrow.End), P(arrow.HeadPoint2)); break;
                case NumberAnnotationCommand number:
                    context.DrawEllipse(brush, null, P(number.Center), number.DiameterPixels / 2, number.DiameterPixels / 2);
                    var color = brush.Color;
                    var ink = (color.R * 299 + color.G * 587 + color.B * 114) / 1000 > 160 ? Brushes.Black : Brushes.White;
                    var label = Text(number.Number.ToString(CultureInfo.InvariantCulture), number.DiameterPixels * 0.65, ink);
                    if (label.Width > number.DiameterPixels * 0.8)
                        label = Text(number.Number.ToString(CultureInfo.InvariantCulture), number.DiameterPixels * 0.65 * number.DiameterPixels * 0.8 / label.Width, ink);
                    context.DrawText(label, new Point(number.Center.X - label.Width / 2, number.Center.Y - label.Height / 2));
                    break;
                case MosaicAnnotationCommand mosaic:
                    DrawMosaic(context, original, mosaic); break;
                case PathAnnotationCommand path:
                    if (path.Points.Count == 1) context.DrawEllipse(brush, null, P(path.Points[0]), pen.Thickness / 2, pen.Thickness / 2);
                    else
                    {
                        var geometry = new StreamGeometry();
                        using (var builder = geometry.Open())
                        {
                            builder.BeginFigure(P(path.Points[0]), false);
                            foreach (var point in path.Points.Skip(1)) builder.LineTo(P(point));
                            builder.EndFigure(false);
                        }
                        context.DrawGeometry(null, pen, geometry);
                    }
                    break;
                case TextAnnotationCommand text:
                    var formatted = Text(text.Text, text.Style.FontSizePixels, brush);
                    formatted.MaxTextWidth = text.Bounds.Width;
                    formatted.MaxTextHeight = text.Bounds.Height;
                    context.DrawText(formatted, rect.TopLeft); break;
            }
        }
    }

    private static void DrawMosaic(DrawingContext context, Picture original, MosaicAnnotationCommand mosaic)
    {
        var bytes = original.GetPixels();
        var width = original.Bitmap.PixelSize.Width;
        var height = original.Bitmap.PixelSize.Height;
        var step = Math.Max(1, (int)Math.Round(mosaic.BlockSizePixels));
        var radius = mosaic.Style.StrokeWidthPixels / 2;
        var bounds = mosaic.Bounds.Inflate(radius + step);
        var startX = Math.Max(0, (int)Math.Floor(bounds.Left / step) * step);
        var startY = Math.Max(0, (int)Math.Floor(bounds.Top / step) * step);
        for (var y = startY; y < Math.Min(height, bounds.Bottom); y += step)
        for (var x = startX; x < Math.Min(width, bounds.Right); x += step)
        {
            if (!AnnotationGeometry.HitTestPath(mosaic.Points, new AnnotationPoint(x + step / 2d, y + step / 2d), radius + step / 2d)) continue;
            long blue = 0, green = 0, red = 0, alpha = 0, count = 0;
            for (var yy = y; yy < Math.Min(y + step, height); yy++)
            for (var xx = x; xx < Math.Min(x + step, width); xx++)
            {
                var i = (yy * width + xx) * 4;
                blue += bytes[i]; green += bytes[i + 1]; red += bytes[i + 2]; alpha += bytes[i + 3]; count++;
            }
            if (count == 0) continue;
            context.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(alpha / count), (byte)(red / count),
                (byte)(green / count), (byte)(blue / count))), new Rect(x, y, Math.Min(step, width - x), Math.Min(step, height - y)));
        }
    }

    private static Point P(AnnotationPoint p) => new(p.X, p.Y);
    private static FormattedText Text(string text, double size, IBrush brush) => new(text, CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight, Typeface.Default, size, brush);
}
