namespace Tbox.Core;

// Time label on a recent-capture thumbnail: clock time today, "昨天", then the date.
public static class RecentCaptureLabel
{
    public static string Format(DateTime captured, DateTime now) =>
        captured.Date == now.Date ? captured.ToString("HH:mm")
        : captured.Date == now.Date.AddDays(-1) ? "昨天"
        : captured.ToString("M/d");
}
