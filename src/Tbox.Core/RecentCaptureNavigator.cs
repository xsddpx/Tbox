namespace Tbox.Core;

// Ordering for stepping through recent captures in a viewer. Index 0 is the newest capture, so "older" moves forward.
public static class RecentCaptureNavigator
{
    public static int Next(int current, int count, bool older)
    {
        if (count <= 0) return -1;
        if (current < 0 || current >= count) return older ? 0 : count - 1;
        return older ? Math.Min(current + 1, count - 1) : Math.Max(current - 1, 0);
    }

    // Wraps around so repeated presses always keep moving. A single capture stays put.
    public static int Wrap(int current, int count, bool older)
    {
        if (count <= 0) return -1;
        if (count == 1) return 0;
        if (current < 0 || current >= count) return older ? 0 : count - 1;
        return older ? (current + 1) % count : (current - 1 + count) % count;
    }

    public static bool HasOlder(int current, int count) => count > 0 && (current < 0 || current + 1 < count);
    public static bool HasNewer(int current, int count) => count > 0 && (current < 0 || current > 0);

    // "3 / 8": one-based position for display, empty when there is nothing to show.
    public static string Position(int current, int count) =>
        count <= 0 || current < 0 || current >= count ? string.Empty : $"{current + 1} / {count}";
}
