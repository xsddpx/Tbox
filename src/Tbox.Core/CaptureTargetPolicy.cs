namespace Tbox.Core;

public enum CaptureDetectionMode
{
    Window,
    InterfaceElement
}

public readonly record struct CaptureTargetBounds(int Left, int Top, int Width, int Height)
{
    public bool Contains(int x, int y) =>
        Width > 0 && Height > 0 &&
        x >= Left && x < Left + Width &&
        y >= Top && y < Top + Height;
}

public static class CaptureTargetPolicy
{
    public static CaptureDetectionMode Toggle(CaptureDetectionMode mode) => mode switch
    {
        CaptureDetectionMode.Window => CaptureDetectionMode.InterfaceElement,
        CaptureDetectionMode.InterfaceElement => CaptureDetectionMode.Window,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static CaptureTargetBounds? FindTopmostContaining(
        IReadOnlyList<CaptureTargetBounds> targets,
        int x,
        int y)
    {
        ArgumentNullException.ThrowIfNull(targets);
        foreach (var target in targets)
        {
            if (target.Contains(x, y)) return target;
        }

        return null;
    }

    public static PixelRegion? MapClippedToDisplay(
        CaptureDisplayBounds display,
        CaptureTargetBounds target)
    {
        if (display.Width <= 0 || display.Height <= 0 || target.Width <= 0 || target.Height <= 0)
        {
            return null;
        }

        var left = Math.Max(display.Left, target.Left);
        var top = Math.Max(display.Top, target.Top);
        var right = Math.Min(display.Left + display.Width, target.Left + target.Width);
        var bottom = Math.Min(display.Top + display.Height, target.Top + target.Height);
        if (right <= left || bottom <= top) return null;

        return new PixelRegion(
            left - display.Left,
            top - display.Top,
            right - left,
            bottom - top);
    }
}
