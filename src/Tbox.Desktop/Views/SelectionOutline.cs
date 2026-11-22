using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tbox.Desktop.Views;

// Selection outline in the mode colour (spec ⑤). Dashed means the area can no longer be adjusted (recording).
internal sealed class SelectionOutline(IBrush color, bool handles = true) : Control
{
    private bool _dashed;
    public bool Dashed { get => _dashed; set { _dashed = value; InvalidateVisual(); } }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Draw(context, new Rect(4, 4, Math.Max(0, Bounds.Width - 8), Math.Max(0, Bounds.Height - 8)), color, handles && !_dashed, _dashed);
    }

    public static void Draw(DrawingContext context, Rect r, IBrush color, bool handles = true, bool dashed = false)
    {
        var pen = new Pen(color, 1.5, dashed ? new DashStyle([4, 3], 0) : null);
        context.DrawRectangle(null, pen, r);
        if (!handles) return;
        foreach (var p in new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft })
            DrawHandle(p, 5);
        foreach (var p in new[] { new Point(r.Center.X, r.Top), new Point(r.Right, r.Center.Y),
            new Point(r.Center.X, r.Bottom), new Point(r.Left, r.Center.Y) })
            DrawHandle(p, 4);

        void DrawHandle(Point point, double radius) => context.DrawRectangle(Brushes.White, pen,
            new RoundedRect(new Rect(point.X - radius, point.Y - radius, radius * 2, radius * 2), 2));
    }
}
