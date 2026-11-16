using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Tbox.Core;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Models;

// Saved capture history lives in "capture-history" inside the screenshot folder, one folder per capture:
// frame.json (displays, selection, editable annotations) and one full-screen PNG per display.
internal static partial class CaptureFrameStore
{
    public const string FolderName = "capture-history";
    private const int FormatVersion = 1;
    private const string MetadataFile = "frame.json";
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Root(string cacheDirectory) => Path.Combine(cacheDirectory, FolderName);

    // Writes into a temporary folder first so a crash never leaves a half-written record behind.
    public static (string Directory, long Bytes) Save(string root, CaptureFrame frame, DateTime capturedUtc)
    {
        var name = capturedUtc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var target = Path.Combine(root, name);
        var temporary = target + ".tmp";
        Directory.CreateDirectory(temporary);
        try
        {
            long bytes = 0;
            foreach (var display in frame.Displays)
            {
                var png = EncodePng(display);
                File.WriteAllBytes(Path.Combine(temporary, ImageFile(display.Id)), png);
                bytes += png.LongLength;
            }
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new CaptureFrameFile(FormatVersion, capturedUtc, frame.ActiveDisplay,
                SelectionData.From(frame.Selection), frame.Displays.Select(CaptureFrameDisplay.From).ToArray(),
                AnnotationSnapshotData.From(frame.Annotations)), Options);
            File.WriteAllBytes(Path.Combine(temporary, MetadataFile), metadata);
            MoveWhenReleased(temporary, target);
            return (target, bytes + metadata.LongLength);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    // Reads every record's metadata, newest first, without touching the images. Unreadable records and
    // leftovers of an interrupted save are removed; a save still in progress is left alone.
    public static List<CaptureFrameEntry> LoadIndex(string root)
    {
        var entries = new List<CaptureFrameEntry>();
        if (!Directory.Exists(root)) return entries;
        var interruptedBefore = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (TemporaryName().IsMatch(name))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < interruptedBefore) TryDelete(directory);
                continue;
            }
            if (!RecordName().IsMatch(name)) continue;
            try
            {
                var file = JsonSerializer.Deserialize<CaptureFrameFile>(File.ReadAllBytes(Path.Combine(directory, MetadataFile)), Options)
                    ?? throw new JsonException();
                if (file.Version != FormatVersion || file.Displays is not { Length: > 0 } || file.Selection is null || file.Annotations is null ||
                    file.Displays.All(display => display.Id != file.ActiveDisplay))
                    throw new JsonException("截图历史格式无效。");
                var annotations = file.Annotations.ToSnapshot();
                var bytes = new DirectoryInfo(directory).EnumerateFiles().Sum(info => info.Length);
                entries.Add(new CaptureFrameEntry(DateTime.SpecifyKind(file.CapturedUtc, DateTimeKind.Utc), file.Displays, null)
                {
                    Directory = directory, DiskBytes = bytes, ActiveDisplay = file.ActiveDisplay,
                    Selection = file.Selection.ToBounds(), Annotations = annotations
                });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                AppLog.Write($"Removing unreadable capture history {name}: {error.Message}");
                TryDelete(directory);
            }
        }
        return entries.OrderByDescending(entry => entry.CapturedUtc).ToList();
    }

    public static CaptureFrame Load(CaptureFrameEntry entry)
    {
        var directory = entry.Directory ?? throw new InvalidOperationException("这条截图历史没有保存到磁盘。");
        var displays = entry.Displays.Select(saved =>
        {
            var display = new CapturedDisplay(saved.Id, saved.Left, saved.Top, saved.Width, saved.Height,
                File.ReadAllBytes(Path.Combine(directory, ImageFile(saved.Id))));
            if (display.HistoryPixelSize != (saved.PixelWidth, saved.PixelHeight)) throw new InvalidDataException("截图历史图片已损坏。");
            return display;
        }).ToArray();
        return new CaptureFrame(displays, entry.ActiveDisplay, entry.Selection, entry.Annotations!);
    }

    // Only folders Tbox itself names are ever deleted from the history folder.
    public static void DeleteAll(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (RecordName().IsMatch(name) || TemporaryName().IsMatch(name)) TryDelete(directory);
        }
    }

    public static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Capture history cleanup failed for {directory}: {error.Message}");
        }
    }

    private static string ImageFile(uint displayId) => $"display-{displayId.ToString(CultureInfo.InvariantCulture)}.png";

    // Virus scanners and search indexers briefly open newly written files, which blocks renaming their folder.
    private static void MoveWhenReleased(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { Directory.Move(source, target); return; }
            catch (Exception error) when (attempt < 20 && error is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    // Windows captures arrive as raw pixels. Encode a separate copy instead of caching PNG bytes on the display.
    private static byte[] EncodePng(CapturedDisplay display)
    {
        if (display.BgraPixels is null) return display.Png;
        using var picture = display.CreatePicture();
        return picture.Png;
    }

    [GeneratedRegex(@"^\d{8}-\d{6}-\d{3}-[0-9a-f]{8}$")]
    private static partial Regex RecordName();
    [GeneratedRegex(@"^\d{8}-\d{6}-\d{3}-[0-9a-f]{8}\.tmp$")]
    private static partial Regex TemporaryName();

    private sealed record CaptureFrameFile(int Version, DateTime CapturedUtc, uint ActiveDisplay, SelectionData Selection,
        CaptureFrameDisplay[] Displays, AnnotationSnapshotData Annotations);

    private sealed record SelectionData(double Left, double Top, double Width, double Height)
    {
        public static SelectionData From(CaptureSelectionBounds bounds) => new(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        public CaptureSelectionBounds ToBounds() => new(Left, Top, Width, Height);
    }
}
