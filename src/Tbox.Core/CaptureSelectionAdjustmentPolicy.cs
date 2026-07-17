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
        double tolerance)
    {
        if (bounds.Width < 0 || bounds.Height < 0) throw new ArgumentOutOfRangeException(nameof(bounds));
        if (tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));

        var withinHorizontalRange = x >= bounds.Left - tolerance && x <= bounds.Right + tolerance;
        var withinVerticalRange = y >= bounds.Top - tolerance && y <= bounds.Bottom + tolerance;
        if (!withinHorizontalRange || !withinVerticalRange) return CaptureSelectionHandle.None;

        var nearLeft = Math.Abs(x - bounds.Left) <= tolerance;
        var nearRight = Math.Abs(x - bounds.Right) <= tolerance;
        var nearTop = Math.Abs(y - bounds.Top) <= tolerance;
        var nearBottom = Math.Abs(y - bounds.Bottom) <= tolerance;

        if (nearLeft && nearTop) return CaptureSelectionHandle.TopLeft;
        if (nearRight && nearTop) return CaptureSelectionHandle.TopRight;
        if (nearLeft && nearBottom) return CaptureSelectionHandle.BottomLeft;
        if (nearRight && nearBottom) return CaptureSelectionHandle.BottomRight;
        if (nearLeft) return CaptureSelectionHandle.Left;
        if (nearRight) return CaptureSelectionHandle.Right;
        if (nearTop) return CaptureSelectionHandle.Top;
        if (nearBottom) return CaptureSelectionHandle.Bottom;
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
