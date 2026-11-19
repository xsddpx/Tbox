using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Tbox.Core;

namespace Tbox.Desktop.Models;

// Source-pixel cells live only for the current gesture. The same cell is sampled and retained
// at most once, even when the pointer crosses its path repeatedly or the window changes scale.
internal sealed class MosaicStrokePreview(IReadOnlyList<AnnotationPoint> points, AnnotationStyle style, double blockSize)
{
    private readonly int _step = Math.Max(1, (int)Math.Round(blockSize));
    private readonly SortedList<int, SortedList<int, ImmutableSolidColorBrush>> _rows = [];
    private Picture? _source;
    private int _processedPoints;

    public AnnotationStyle Style => style;
    public double BlockSize => blockSize;
    public AnnotationCommand Freeze() => new MosaicAnnotationCommand(points, style, blockSize);

    public void Draw(DrawingContext context, Picture picture)
    {
        if (!ReferenceEquals(_source, picture))
        {
            _source = picture;
            _rows.Clear();
            _processedPoints = 0;
        }
        var size = picture.Bitmap.PixelSize;
        if (_processedPoints < points.Count)
        {
            var pixels = picture.GetPixels();
            while (_processedPoints < points.Count)
            {
                var from = points[Math.Max(0, _processedPoints - 1)];
                AddSegment(from, points[_processedPoints], pixels, size);
                _processedPoints++;
            }
        }

        // Match export's top-to-bottom, left-to-right order, including antialiased edges at fractional zoom.
        for (var row = 0; row < _rows.Count; row++)
        {
            var y = _rows.GetKeyAtIndex(row);
            var cells = _rows.GetValueAtIndex(row);
            for (var cell = 0; cell < cells.Count; cell++)
            {
                var x = cells.GetKeyAtIndex(cell);
                context.FillRectangle(cells.GetValueAtIndex(cell),
                    new Rect(x, y, Math.Min(_step, size.Width - x), Math.Min(_step, size.Height - y)));
            }
        }
    }

    private void AddSegment(AnnotationPoint from, AnnotationPoint to, byte[] pixels, PixelSize size)
    {
        var radius = style.StrokeWidthPixels / 2;
        var margin = radius + _step;
        var startX = (int)Math.Clamp(Math.Min(from.X, to.X) - margin, 0, size.Width) / _step * _step;
        var startY = (int)Math.Clamp(Math.Min(from.Y, to.Y) - margin, 0, size.Height) / _step * _step;
        var endX = Math.Min(size.Width, Math.Max(from.X, to.X) + margin);
        var endY = Math.Min(size.Height, Math.Max(from.Y, to.Y) + margin);
        var tolerance = radius + _step / 2d;
        for (var y = startY; y < endY; y += _step)
        {
            _rows.TryGetValue(y, out var cells);
            for (var x = startX; x < endX; x += _step)
            {
                if (cells?.ContainsKey(x) == true) continue;
                var center = new AnnotationPoint(x + _step / 2d, y + _step / 2d);
                if (AnnotationGeometry.DistanceToSegment(center, from, to) > tolerance) continue;
                if (cells is null) _rows.Add(y, cells = []);
                var color = AnnotationRenderer.SampleMosaicColor(pixels, size.Width, size.Height, x, y, _step);
                cells.Add(x, new ImmutableSolidColorBrush(color));
            }
        }
    }
}
