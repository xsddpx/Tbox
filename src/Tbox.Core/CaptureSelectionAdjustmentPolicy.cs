namespace Tbox.Core;

public enum CaptureSelectionHandle
{
    None,
    Left,
    Top,
    Right,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public readonly record struct CaptureSelectionBounds(
    double Left,
    double Top,
    double Width,
    double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public static class CaptureSelectionAdjustmentPolicy
{
    public static CaptureSelectionHandle HitTest(
        CaptureSelectionBounds bounds,
        double x,
        double y,
        double tolerance,
        double? cornerTolerance = null)
    {
        if (bounds.Width < 0 || bounds.Height < 0) throw new ArgumentOutOfRangeException(nameof(bounds));
        if (tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));

        var corner = Math.Max(tolerance, cornerTolerance ?? tolerance);
        var left = Math.Abs(x - bounds.Left);
        var right = Math.Abs(x - bounds.Right);
        var top = Math.Abs(y - bounds.Top);
        var bottom = Math.Abs(y - bounds.Bottom);
        if (Math.Min(left, right) <= corner && Math.Min(top, bottom) <= corner)
        {
            if (top <= bottom) return left <= right ? CaptureSelectionHandle.TopLeft : CaptureSelectionHandle.TopRight;
            return left <= right ? CaptureSelectionHandle.BottomLeft : CaptureSelectionHandle.BottomRight;
        }
        if (y >= bounds.Top && y <= bounds.Bottom && Math.Min(left, right) <= tolerance)
            return left <= right ? CaptureSelectionHandle.Left : CaptureSelectionHandle.Right;
        if (x >= bounds.Left && x <= bounds.Right && Math.Min(top, bottom) <= tolerance)
            return top <= bottom ? CaptureSelectionHandle.Top : CaptureSelectionHandle.Bottom;
        return CaptureSelectionHandle.None;
    }

    public static CaptureSelectionBounds Resize(
        CaptureSelectionBounds bounds,
        CaptureSelectionHandle handle,
        double pointerX,
        double pointerY,
        double surfaceWidth,
        double surfaceHeight,
        double minimumWidth,
        double minimumHeight)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(surfaceWidth));
        if (minimumWidth <= 0 || minimumHeight <= 0) throw new ArgumentOutOfRangeException(nameof(minimumWidth));
        if (bounds.Left < 0 || bounds.Top < 0 || bounds.Width <= 0 || bounds.Height <= 0 ||
            bounds.Right > surfaceWidth || bounds.Bottom > surfaceHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }
        if (handle == CaptureSelectionHandle.None) return bounds;

        var left = bounds.Left;
        var top = bounds.Top;
        var right = bounds.Right;
        var bottom = bounds.Bottom;
        var effectiveMinimumWidth = Math.Min(minimumWidth, surfaceWidth);
        var effectiveMinimumHeight = Math.Min(minimumHeight, surfaceHeight);

        if (handle is CaptureSelectionHandle.Left or CaptureSelectionHandle.TopLeft or CaptureSelectionHandle.BottomLeft)
        {
            left = Math.Clamp(pointerX, 0, Math.Max(0, right - effectiveMinimumWidth));
        }
        if (handle is CaptureSelectionHandle.Right or CaptureSelectionHandle.TopRight or CaptureSelectionHandle.BottomRight)
        {
            right = Math.Clamp(pointerX, Math.Min(surfaceWidth, left + effectiveMinimumWidth), surfaceWidth);
        }
        if (handle is CaptureSelectionHandle.Top or CaptureSelectionHandle.TopLeft or CaptureSelectionHandle.TopRight)
        {
            top = Math.Clamp(pointerY, 0, Math.Max(0, bottom - effectiveMinimumHeight));
        }
        if (handle is CaptureSelectionHandle.Bottom or CaptureSelectionHandle.BottomLeft or CaptureSelectionHandle.BottomRight)
        {
            bottom = Math.Clamp(pointerY, Math.Min(surfaceHeight, top + effectiveMinimumHeight), surfaceHeight);
        }

        return new CaptureSelectionBounds(left, top, right - left, bottom - top);
    }
}
