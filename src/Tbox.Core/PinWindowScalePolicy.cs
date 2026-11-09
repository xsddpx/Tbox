namespace Tbox.Core;

public readonly record struct PinWindowBounds(int X, int Y, int Width, int Height);

public static class WindowDpiScalePolicy
{
    public static int LogicalToPhysicalPixels(double logicalPixels, uint dpi)
    {
        if (!double.IsFinite(logicalPixels) || logicalPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalPixels));
        }
        if (dpi == 0) throw new ArgumentOutOfRangeException(nameof(dpi));

        return Math.Max(1, checked((int)Math.Ceiling(logicalPixels * dpi / 96d)));
    }
}

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

public static class PinToolbarPlacementPolicy
{
    public static PinWindowBounds PlaceAdjacent(
        PinWindowBounds image,
        PinWindowBounds workArea,
        int toolbarWidth,
        int toolbarHeight,
        int gap = 8)
    {
        if (image.Width <= 0 || image.Height <= 0) throw new ArgumentOutOfRangeException(nameof(image));
        if (workArea.Width <= 0 || workArea.Height <= 0) throw new ArgumentOutOfRangeException(nameof(workArea));
        if (toolbarWidth <= 0) throw new ArgumentOutOfRangeException(nameof(toolbarWidth));
        if (toolbarHeight <= 0) throw new ArgumentOutOfRangeException(nameof(toolbarHeight));
        if (gap < 0) throw new ArgumentOutOfRangeException(nameof(gap));

        toolbarWidth = Math.Min(toolbarWidth, workArea.Width);
        toolbarHeight = Math.Min(toolbarHeight, workArea.Height);
        var x = Math.Clamp(
            image.X + image.Width - toolbarWidth,
            workArea.X,
            workArea.X + workArea.Width - toolbarWidth);
        var below = image.Y + image.Height + gap;
        var above = image.Y - toolbarHeight - gap;
        var y = below + toolbarHeight <= workArea.Y + workArea.Height
            ? below
            : above >= workArea.Y
                ? above
                : Math.Clamp(below, workArea.Y, workArea.Y + workArea.Height - toolbarHeight);
        return new PinWindowBounds(x, y, toolbarWidth, toolbarHeight);
    }

    // Task toolbars (spec ⑤): 12 px below the selection, otherwise above it, otherwise inside its bottom edge.
    public static PinWindowBounds PlaceTaskToolbar(PinWindowBounds selection, PinWindowBounds workArea, int toolbarWidth, int toolbarHeight, int gap = 12)
    {
        var placed = PlaceAdjacent(selection, workArea, toolbarWidth, toolbarHeight, gap);
        var below = selection.Y + selection.Height + gap;
        var above = selection.Y - placed.Height - gap;
        if (placed.Y == below || placed.Y == above) return placed;
        var inside = Math.Clamp(selection.Y + selection.Height - placed.Height - gap, workArea.Y, workArea.Y + workArea.Height - placed.Height);
        return placed with { Y = inside };
    }
}
