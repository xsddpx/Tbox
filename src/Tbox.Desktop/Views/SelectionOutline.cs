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
        foreach (var p in new[] { r.TopLeft, new Point(r.Center.X, r.Top), r.TopRight, new Point(r.Right, r.Center.Y),
            r.BottomRight, new Point(r.Center.X, r.Bottom), r.BottomLeft, new Point(r.Left, r.Center.Y) })
            context.DrawRectangle(Brushes.White, pen, new RoundedRect(new Rect(p.X - 4, p.Y - 4, 8, 8), 2));
    }
}
