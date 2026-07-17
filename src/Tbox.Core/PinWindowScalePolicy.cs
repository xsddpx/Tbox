namespace Tbox.Core;

public readonly record struct PinWindowBounds(int X, int Y, int Width, int Height);

public static class PinWindowScalePolicy
{
    private const double ScalePerWheelNotch = 1.1;

    public static double ApplyWheelDelta(
        double currentScale,
        int wheelDelta,
        double minimumScale,
        double maximumScale)
    {
        if (!double.IsFinite(currentScale) || currentScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentScale));
        }
        if (!double.IsFinite(minimumScale) || minimumScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumScale));
        }
        if (!double.IsFinite(maximumScale) || maximumScale < minimumScale)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumScale));
        }
        if (wheelDelta == 0) return currentScale;

        var wheelNotches = wheelDelta / 120d;
        var nextScale = currentScale * Math.Pow(ScalePerWheelNotch, wheelNotches);
        return Math.Clamp(nextScale, minimumScale, maximumScale);
    }

    public static PinWindowBounds ResizeAroundPoint(
        PinWindowBounds current,
        int targetWidth,
        int targetHeight,
        int anchorX,
        int anchorY)
    {
        if (current.Width <= 0) throw new ArgumentOutOfRangeException(nameof(current));
        if (current.Height <= 0) throw new ArgumentOutOfRangeException(nameof(current));
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));
        if (targetHeight <= 0) throw new ArgumentOutOfRangeException(nameof(targetHeight));

        var horizontalRatio = Math.Clamp((anchorX - current.X) / (double)current.Width, 0, 1);
        var verticalRatio = Math.Clamp((anchorY - current.Y) / (double)current.Height, 0, 1);
        var x = (int)Math.Round(anchorX - horizontalRatio * targetWidth);
        var y = (int)Math.Round(anchorY - verticalRatio * targetHeight);
        return new PinWindowBounds(x, y, targetWidth, targetHeight);
    }
}
