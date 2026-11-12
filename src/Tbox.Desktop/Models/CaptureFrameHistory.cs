using Tbox.Core;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Models;

// Full-screen frames are kept only in memory and only for this app process.
internal sealed record CaptureFrame(IReadOnlyList<CapturedDisplay> Displays, uint ActiveDisplay,
    CaptureSelectionBounds Selection, AnnotationDocumentSnapshot Annotations)
{
    public long Bytes => Displays.Sum(display => display.HistoryBytes);
}

internal sealed class CaptureFrameHistory
{
    internal const int MaximumCount = 10;
    internal const long MaximumBytes = 256L * 1024 * 1024;
    private readonly List<CaptureFrame> _frames = [];
    private long _bytes;

    public void Add(CaptureFrame frame)
    {
        var bytes = frame.Bytes;
        if (bytes > MaximumBytes) return;
        _frames.Insert(0, frame); _bytes += bytes;
        while (_frames.Count > MaximumCount || _bytes > MaximumBytes)
        {
            _bytes -= _frames[^1].Bytes;
            _frames.RemoveAt(_frames.Count - 1);
        }
    }

    public IReadOnlyList<CaptureFrame> CompatibleWith(IEnumerable<CapturedDisplay> displays)
    {
        var current = displays.ToArray();
        return _frames.Where(frame => frame.Displays.Count == current.Length &&
            frame.Displays.All(saved => current.Any(display => SameDisplay(display, saved)))).ToArray();
    }

    private static bool SameDisplay(CapturedDisplay a, CapturedDisplay b) =>
        a.Id == b.Id && a.Left == b.Left && a.Top == b.Top && a.Width == b.Width && a.Height == b.Height &&
        a.HistoryPixelSize == b.HistoryPixelSize;
}
