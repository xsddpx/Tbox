namespace Tbox.Core;

internal readonly record struct PinCropResult(
    PixelRegion Region,
    PinWindowBounds WindowBounds);

internal static class PinCropPolicy
{
    internal static bool TryCreate(
        PixelRegion currentRegion,
        PinWindowBounds currentWindow,
        double surfaceWidth,
        double surfaceHeight,
        double startX,
        double startY,
        double endX,
        double endY,
        double minimumDisplaySize,
        out PinCropResult result)
    {
        ValidateRegion(currentRegion);
        ValidateWindow(currentWindow);
        ValidatePositiveFinite(surfaceWidth, nameof(surfaceWidth));
        ValidatePositiveFinite(surfaceHeight, nameof(surfaceHeight));
        ValidatePositiveFinite(minimumDisplaySize, nameof(minimumDisplaySize));
        ValidateFinite(startX, nameof(startX));
        ValidateFinite(startY, nameof(startY));
        ValidateFinite(endX, nameof(endX));
        ValidateFinite(endY, nameof(endY));

        var left = Math.Clamp(Math.Min(startX, endX), 0, surfaceWidth);
        var top = Math.Clamp(Math.Min(startY, endY), 0, surfaceHeight);
        var right = Math.Clamp(Math.Max(startX, endX), 0, surfaceWidth);
        var bottom = Math.Clamp(Math.Max(startY, endY), 0, surfaceHeight);
        if (right - left < minimumDisplaySize || bottom - top < minimumDisplaySize)
        {
            result = default;
            return false;
        }

        var sourceLeft = currentRegion.Left +
                         (int)Math.Floor(left / surfaceWidth * currentRegion.Width);
        var sourceTop = currentRegion.Top +
                        (int)Math.Floor(top / surfaceHeight * currentRegion.Height);
        var sourceRight = currentRegion.Left +
                          (int)Math.Ceiling(right / surfaceWidth * currentRegion.Width);
        var sourceBottom = currentRegion.Top +
                           (int)Math.Ceiling(bottom / surfaceHeight * currentRegion.Height);
        sourceLeft = Math.Clamp(sourceLeft, currentRegion.Left, currentRegion.Left + currentRegion.Width - 1);
        sourceTop = Math.Clamp(sourceTop, currentRegion.Top, currentRegion.Top + currentRegion.Height - 1);
        sourceRight = Math.Clamp(sourceRight, sourceLeft + 1, currentRegion.Left + currentRegion.Width);
        sourceBottom = Math.Clamp(sourceBottom, sourceTop + 1, currentRegion.Top + currentRegion.Height);

        var region = new PixelRegion(
            sourceLeft,
            sourceTop,
            sourceRight - sourceLeft,
            sourceBottom - sourceTop);
        if (region == currentRegion)
        {
            result = default;
            return false;
        }

        var windowLeft = currentWindow.X +
                         (int)Math.Round(left / surfaceWidth * currentWindow.Width);
        var windowTop = currentWindow.Y +
                        (int)Math.Round(top / surfaceHeight * currentWindow.Height);
        var windowRight = currentWindow.X +
                          (int)Math.Round(right / surfaceWidth * currentWindow.Width);
        var windowBottom = currentWindow.Y +
                           (int)Math.Round(bottom / surfaceHeight * currentWindow.Height);
        var window = new PinWindowBounds(
            windowLeft,
            windowTop,
            Math.Max(1, windowRight - windowLeft),
            Math.Max(1, windowBottom - windowTop));
        result = new PinCropResult(region, window);
        return true;
    }

    internal static PinCropResult Restore(
        PixelRegion currentRegion,
        PinWindowBounds currentWindow,
        int sourceWidth,
        int sourceHeight)
    {
        ValidateRegion(currentRegion);
        ValidateWindow(currentWindow);
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        if (currentRegion.Left + currentRegion.Width > sourceWidth ||
            currentRegion.Top + currentRegion.Height > sourceHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(currentRegion));
        }

        var horizontalScale = currentWindow.Width / (double)currentRegion.Width;
        var verticalScale = currentWindow.Height / (double)currentRegion.Height;
        var window = new PinWindowBounds(
            (int)Math.Round(currentWindow.X - (currentRegion.Left * horizontalScale)),
            (int)Math.Round(currentWindow.Y - (currentRegion.Top * verticalScale)),
            Math.Max(1, (int)Math.Round(sourceWidth * horizontalScale)),
            Math.Max(1, (int)Math.Round(sourceHeight * verticalScale)));
        return new PinCropResult(new PixelRegion(0, 0, sourceWidth, sourceHeight), window);
    }

    internal static bool IsFullImage(PixelRegion region, int sourceWidth, int sourceHeight) =>
        region.Left == 0 &&
        region.Top == 0 &&
        region.Width == sourceWidth &&
        region.Height == sourceHeight;

    private static void ValidateRegion(PixelRegion region)
    {
        if (region.Left < 0 || region.Top < 0 || region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }
    }

    private static void ValidateWindow(PinWindowBounds window)
    {
        if (window.Width <= 0 || window.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }
    }

    private static void ValidatePositiveFinite(double value, string name)
    {
        ValidateFinite(value, name);
        if (value <= 0) throw new ArgumentOutOfRangeException(name);
    }

    private static void ValidateFinite(double value, string name)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(name);
    }
}
