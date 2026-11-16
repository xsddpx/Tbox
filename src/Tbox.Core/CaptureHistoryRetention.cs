namespace Tbox.Core;

// Saved full-screen capture history: the newest 50 records from the last 7 days, at most 1 GiB in total.
public static class CaptureHistoryRetention
{
    public const int MaximumCount = 50;
    public const long MaximumTotalBytes = 1024L * 1024 * 1024;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);

    // Records are ordered newest first; the kept records are always the leading ones.
    public static int CountToKeep(IReadOnlyList<(DateTime CapturedUtc, long Bytes)> newestFirst, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        var cutoff = nowUtc - MaximumAge;
        long total = 0;
        var kept = 0;
        foreach (var (captured, bytes) in newestFirst)
        {
            if (kept == MaximumCount || captured < cutoff || total + bytes > MaximumTotalBytes) break;
            total += bytes;
            kept++;
        }
        return kept;
    }
}
