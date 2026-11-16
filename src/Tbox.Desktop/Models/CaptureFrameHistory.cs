using Tbox.Core;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Models;

internal sealed record CaptureFrame(IReadOnlyList<CapturedDisplay> Displays, uint ActiveDisplay,
    CaptureSelectionBounds Selection, AnnotationDocumentSnapshot Annotations)
{
    public long Bytes => Displays.Sum(display => display.HistoryBytes);
}

// The desktop layout a record was captured on, kept so compatibility is known without reading its images.
internal sealed record CaptureFrameDisplay(uint Id, double Left, double Top, double Width, double Height, int PixelWidth, int PixelHeight)
{
    public static CaptureFrameDisplay From(CapturedDisplay display)
    {
        var (width, height) = display.HistoryPixelSize;
        return new(display.Id, display.Left, display.Top, display.Width, display.Height, width, height);
    }

    public bool Matches(CapturedDisplay display) =>
        Id == display.Id && Left == display.Left && Top == display.Top && Width == display.Width && Height == display.Height &&
        (PixelWidth, PixelHeight) == display.HistoryPixelSize;
}

internal sealed class CaptureFrameEntry(DateTime capturedUtc, IReadOnlyList<CaptureFrameDisplay> displays, CaptureFrame? frame)
{
    public DateTime CapturedUtc { get; } = capturedUtc;
    public IReadOnlyList<CaptureFrameDisplay> Displays { get; } = displays;
    // The newest captures stay in memory so stepping back is instant; older ones are read from Directory.
    public CaptureFrame? Frame { get; set; } = frame;
    public string? Directory { get; set; }
    public long DiskBytes { get; set; }
    public bool Saving { get; set; }
    public uint ActiveDisplay { get; init; } = frame?.ActiveDisplay ?? 0;
    public CaptureSelectionBounds Selection { get; init; } = frame?.Selection ?? default;
    public AnnotationDocumentSnapshot? Annotations { get; init; } = frame?.Annotations;
}

// Completed captures, newest first. Once a screenshot folder is opened every record is also saved there, so
// history survives restarts and updates; disk use follows CaptureHistoryRetention. Use from the UI thread only.
internal sealed class CaptureFrameHistory(Func<DateTime>? utcNow = null)
{
    internal const int MemoryCount = 10;
    internal const long MemoryBytes = 256L * 1024 * 1024;
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private List<CaptureFrameEntry> _entries = [];
    private string? _root;
    // Bumped by ClearAsync so a save that finishes afterwards removes its files instead of reviving the record.
    private int _clears;

    public string? Root => _root;
    public int Count => _entries.Count;

    public async Task AddAsync(CaptureFrame frame)
    {
        var root = _root;
        var entry = new CaptureFrameEntry(_utcNow(), frame.Displays.Select(CaptureFrameDisplay.From).ToArray(), frame) { Saving = root is not null };
        _entries.Insert(0, entry);
        TrimMemory();
        if (root is null) return;
        var clears = _clears;
        try
        {
            var (directory, bytes) = await Task.Run(() => CaptureFrameStore.Save(CaptureFrameStore.Root(root), frame, entry.CapturedUtc));
            if (clears != _clears) { await Task.Run(() => CaptureFrameStore.TryDelete(directory)); return; }
            entry.Directory = directory; entry.DiskBytes = bytes;
            // A history folder read while this record was being written may already list it.
            _entries.RemoveAll(other => other != entry && string.Equals(other.Directory, directory, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // History is best effort: the screenshot itself has already been saved, copied or pinned.
            AppLog.Write("Saving capture history failed: " + error.Message);
        }
        finally { entry.Saving = false; }
        TrimMemory();
        await PruneAsync();
    }

    // Switches to the history saved in a screenshot folder. Records in the previous folder stay where they are;
    // captures made before any folder was opened (early in startup) stay in memory.
    public async Task OpenAsync(string cacheDirectory)
    {
        var root = Path.GetFullPath(cacheDirectory);
        if (string.Equals(root, _root, StringComparison.OrdinalIgnoreCase)) return;
        if (_root is not null) _entries = [];
        _root = root;
        List<CaptureFrameEntry> saved;
        try { saved = await Task.Run(() => CaptureFrameStore.LoadIndex(CaptureFrameStore.Root(root))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Reading capture history failed: " + error.Message);
            return;
        }
        if (!string.Equals(root, _root, StringComparison.OrdinalIgnoreCase)) return;
        // Captures completed while the folder was read are already in the list.
        var known = _entries.Select(entry => entry.Directory).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _entries = _entries.Concat(saved.Where(entry => !known.Contains(entry.Directory!)))
            .OrderByDescending(entry => entry.CapturedUtc).ToList();
        await PruneAsync();
    }

    public async Task ClearAsync()
    {
        _clears++; _entries = [];
        if (_root is { } root) await Task.Run(() => CaptureFrameStore.DeleteAll(CaptureFrameStore.Root(root)));
    }

    public IReadOnlyList<CaptureFrameEntry> CompatibleWith(IEnumerable<CapturedDisplay> displays)
    {
        var current = displays.ToArray();
        return _entries.Where(entry => entry.Displays.Count == current.Length &&
            entry.Displays.All(saved => current.Any(saved.Matches))).ToArray();
    }

    // Safe to call off the UI thread: it only reads the entry's own files.
    public static CaptureFrame Load(CaptureFrameEntry entry) => entry.Frame ?? CaptureFrameStore.Load(entry);

    // Drops a record whose files were removed or damaged outside Tbox.
    public void Forget(CaptureFrameEntry entry)
    {
        if (!_entries.Remove(entry) || entry.Directory is not { } directory) return;
        _ = Task.Run(() => CaptureFrameStore.TryDelete(directory));
    }

    private void TrimMemory()
    {
        long bytes = 0; var count = 0;
        foreach (var entry in _entries.ToArray())
        {
            if (entry.Frame is not { } frame) continue;
            var size = frame.Bytes;
            if (count < MemoryCount && bytes + size <= MemoryBytes) { count++; bytes += size; continue; }
            // A record still being written keeps its pixels until its files exist.
            if (entry.Saving) continue;
            if (entry.Directory is not null) entry.Frame = null;
            else _entries.Remove(entry);
        }
    }

    private async Task PruneAsync()
    {
        var keep = CaptureHistoryRetention.CountToKeep(
            _entries.Select(entry => (entry.CapturedUtc, entry.DiskBytes)).ToArray(), _utcNow());
        if (keep == _entries.Count) return;
        var removed = _entries.Skip(keep).Where(entry => !entry.Saving).ToArray();
        foreach (var entry in removed) _entries.Remove(entry);
        var directories = removed.Select(entry => entry.Directory).OfType<string>().ToArray();
        if (directories.Length != 0) await Task.Run(() => { foreach (var directory in directories) CaptureFrameStore.TryDelete(directory); });
    }
}
