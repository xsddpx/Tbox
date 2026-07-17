namespace Tbox.Core;

public readonly record struct CaptureDisplayBounds(int Left, int Top, int Width, int Height)
{
    public bool Contains(int x, int y) =>
        x >= Left && x < Left + Width &&
        y >= Top && y < Top + Height;
}

public static class CaptureDisplayPolicy
{
    public static int FindContainingDisplayIndex(
        IReadOnlyList<CaptureDisplayBounds> displays,
        int x,
        int y)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count == 0) throw new ArgumentException("至少需要一个显示器。", nameof(displays));

        for (var index = 0; index < displays.Count; index++)
        {
            if (displays[index].Contains(x, y)) return index;
        }

        return 0;
    }
}

public sealed class CaptureSelectionSession
{
    public int? ActiveDisplayIndex { get; private set; }

    public int? BeginSelection(int displayIndex, int displayCount)
    {
        if (displayCount <= 0) throw new ArgumentOutOfRangeException(nameof(displayCount));
        if (displayIndex < 0 || displayIndex >= displayCount)
        {
            throw new ArgumentOutOfRangeException(nameof(displayIndex));
        }

        var previous = ActiveDisplayIndex;
        ActiveDisplayIndex = displayIndex;
        return previous;
    }
}
