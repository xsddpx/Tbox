using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tbox.Desktop.Views;

internal sealed class SelectionOutline(IBrush color) : Control
{
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Draw(context, new Rect(4, 4, Math.Max(0, Bounds.Width - 8), Math.Max(0, Bounds.Height - 8)), color);
    }

    public static void Draw(DrawingContext context, Rect r, IBrush color, bool handles = true)
    {
        var pen = new Pen(color, 1);
        context.DrawRectangle(null, pen, r);
        if (!handles) return;
        foreach (var p in new[] { r.TopLeft, new Point(r.Center.X, r.Top), r.TopRight, new Point(r.Right, r.Center.Y),
            r.BottomRight, new Point(r.Center.X, r.Bottom), r.BottomLeft, new Point(r.Left, r.Center.Y) })
            context.DrawRectangle(Brushes.White, pen, new Rect(p.X - 3, p.Y - 3, 6, 6));
    }
}
