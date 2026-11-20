using System.Globalization;

namespace Tbox.Core;

// 速览 settings. Names and defaults follow PowerToys Peek (PeekProperties).
public sealed record QuickLookSettings
{
    public bool Enabled { get; init; } = true;
    // true: a bare Space in Explorer or on the desktop; false: the 速览 global shortcut (default Ctrl+Space).
    public bool SpaceToActivate { get; init; } = true;
    public bool AlwaysOnTop { get; init; }
    public bool ShowInTaskbar { get; init; } = true;
    public bool CloseAfterLosingFocus { get; init; }
    public bool ConfirmFileDelete { get; init; } = true;
    public bool ShowFileInfoTooltip { get; init; } = true;
}

public enum QuickLookKind { Image, Video, Pdf, Archive, Folder, Unsupported }

public static class QuickLookPolicy
{
    // WindowConstants in Peek.
    public const double MaxWindowToMonitorRatio = 0.80, MinWindowWidth = 500, MinWindowHeight = 500, TitleBarHeight = 48;
    // Preview sizes Peek requests in effective pixels; null previews take 80% of the monitor.
    public const double InfoWidth = 680, InfoHeight = 500;
    public static readonly TimeSpan NavigationThrottle = TimeSpan.FromMilliseconds(100);

    // Images Tbox decodes itself; the platform adds its own registered image codecs.
    private static readonly HashSet<string> SkiaImages = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".gif", ".webp", ".ico" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".3gp", ".3g2", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".ogv" };
    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".jar", ".nupkg", ".tar", ".gz", ".tgz" };

    public static bool IsAnimatable(string path) => Path.GetExtension(path) is var e &&
        (e.Equals(".gif", StringComparison.OrdinalIgnoreCase) || e.Equals(".webp", StringComparison.OrdinalIgnoreCase));
    public static bool IsSkiaImage(string path) => SkiaImages.Contains(Path.GetExtension(path));

    public static QuickLookKind Classify(string path, bool isDirectory, IReadOnlySet<string>? systemImages = null)
    {
        if (isDirectory) return QuickLookKind.Folder;
        var extension = Path.GetExtension(path);
        if (SkiaImages.Contains(extension) || systemImages?.Contains(extension) == true) return QuickLookKind.Image;
        if (Videos.Contains(extension)) return QuickLookKind.Video;
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return QuickLookKind.Pdf;
        if (Archives.Contains(extension)) return QuickLookKind.Archive;
        return QuickLookKind.Unsupported;
    }

    // Peek's SizeExtensions.Fit: scale down (never up) to fit the maximum, then clamp each side to the minimum.
    public static (double Width, double Height) Fit(double width, double height, double maxWidth, double maxHeight, double minWidth, double minHeight)
    {
        if (width > maxWidth || height > maxHeight)
        {
            var ratio = Math.Min(maxWidth / width, maxHeight / height);
            width *= ratio; height *= ratio;
        }
        return (Math.Max(minWidth, width), Math.Max(minHeight, height));
    }

    // Window size in effective pixels for one preview. requested is null when the preview takes the monitor size;
    // physical sizes (image and video pixels) are divided by the display scale. The title bar adds to the height.
    public static (double Width, double Height) WindowSize((double Width, double Height)? requested, bool effectivePixels,
        double monitorWidth, double monitorHeight, double scale)
    {
        var (width, height) = requested ?? (monitorWidth, monitorHeight);
        if (requested is null || !effectivePixels) { width /= scale; height /= scale; }
        var maxWidth = monitorWidth * MaxWindowToMonitorRatio / scale;
        var maxHeight = (monitorHeight - TitleBarHeight * scale) * MaxWindowToMonitorRatio / scale;
        var content = Fit(width, height, maxWidth, maxHeight, MinWindowWidth / scale, (MinWindowHeight - TitleBarHeight) / scale);
        return (content.Width, content.Height + TitleBarHeight);
    }

    public static string WindowTitle(string? name) => name is null ? "速览" : $"{name} - 速览";
    public static string Counter(int index, int count) => count > 1 ? $"({index + 1}/{count})" : "";
    public static string OpenWith(string app) => $"用 {app} 打开";

    // Peek's ReadableStringHelper: 1024-based units, three significant digits truncated, plus the exact byte count.
    public static string ReadableSize(long bytes, bool showTotal = true)
    {
        string[] units = ["字节", "KB", "MB", "GB", "TB", "PB"];
        if (bytes < 1024) return $"{bytes} 字节";
        var index = Math.Min(units.Length - 1, (int)Math.Floor(Math.Log(bytes, 1024)));
        var value = bytes / Math.Pow(1024, index);
        if (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        var digits = value >= 100 ? 0 : value >= 10 ? 1 : 2;
        var factor = Math.Pow(10, digits);
        var truncated = Math.Floor(value * factor) / factor;
        var text = truncated.ToString("0." + new string('#', digits), CultureInfo.InvariantCulture) + " " + units[index];
        return showTotal ? $"{text} ({bytes.ToString("N0", CultureInfo.InvariantCulture)} 字节)" : text;
    }

    public static string FolderContents(long files, long folders)
    {
        var parts = new List<string>();
        if (files > 0 || folders == 0) parts.Add($"{files} 个文件");
        if (folders > 0) parts.Add($"{folders} 个文件夹");
        return string.Join(", ", parts);
    }
}

// The items one preview session walks through. A single selection walks the whole folder, a multiple selection
// only the selected items; both wrap around. After a delete the next item follows the last navigation direction.
public sealed class QuickLookNavigator
{
    private readonly List<string> _items;
    private int _direction = 1;

    public QuickLookNavigator(IEnumerable<string> items, int index = 0)
    {
        _items = [.. items];
        Index = _items.Count == 0 ? -1 : Math.Clamp(index, 0, _items.Count - 1);
    }

    public int Index { get; private set; }
    public int Count => _items.Count;
    public string? Current => Index >= 0 ? _items[Index] : null;
    public IReadOnlyList<string> Items => _items;

    public string? Move(int step)
    {
        if (_items.Count == 0) return null;
        _direction = step < 0 ? -1 : 1;
        Index = Modulo(Index + step, _items.Count);
        return Current;
    }

    // Replaces the walked list (the folder view loads after the first item is already showing) and keeps the current item.
    public void Replace(IEnumerable<string> items, string? current)
    {
        _items.Clear(); _items.AddRange(items);
        var found = current is null ? -1 : _items.FindIndex(item => string.Equals(item, current, StringComparison.OrdinalIgnoreCase));
        if (found < 0 && current is not null) { _items.Insert(0, current); found = 0; }
        Index = _items.Count == 0 ? -1 : Math.Max(0, found);
    }

    // Removes the current item. Moving forward keeps the position (2/3 becomes 2/2); moving back steps back one.
    public string? RemoveCurrent()
    {
        if (Index < 0) return null;
        _items.RemoveAt(Index);
        if (_items.Count == 0) { Index = -1; return null; }
        Index = _direction < 0 ? Modulo(Index - 1, _items.Count) : Index % _items.Count;
        return Current;
    }

    // Puts back an item whose delete was cancelled or failed.
    public void Restore(string item, int index)
    {
        index = Math.Clamp(index, 0, _items.Count);
        _items.Insert(index, item);
        Index = index;
    }

    private static int Modulo(int value, int count) => ((value % count) + count) % count;
}
