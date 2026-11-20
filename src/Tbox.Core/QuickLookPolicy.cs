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
    // Peek's source-code preview options (Monaco in Peek, the text editor here).
    public bool WrapText { get; init; }
    public int TextFontSize { get; init; } = 14;
    public int MaxTextFileSizeKb { get; init; } = 10240;
}

public enum QuickLookKind { Image, Video, Pdf, Archive, Folder, Text, Markdown, Svg, PreviewHandler, Office, Unsupported }

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
        { ".zip", ".jar", ".nupkg", ".tar", ".gz", ".tgz", ".7z", ".rar" };
    private static readonly HashSet<string> Markdown = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd", ".mdwn", ".mdtxt", ".mdtext" };
    // Peek's Monaco list, trimmed to formats people read as text.
    private static readonly HashSet<string> Texts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".log", ".csv", ".tsv", ".ini", ".cfg", ".conf", ".config", ".env", ".properties", ".toml", ".yaml", ".yml",
        ".json", ".jsonc", ".json5", ".xml", ".xaml", ".axaml", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".sln", ".resx", ".manifest", ".plist",
        ".html", ".htm", ".xhtml", ".css", ".scss", ".sass", ".less", ".js", ".mjs", ".cjs", ".jsx", ".ts", ".mts", ".cts", ".tsx", ".vue", ".svelte",
        ".cs", ".csx", ".vb", ".fs", ".fsx", ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh", ".hxx", ".m", ".mm", ".java", ".kt", ".kts", ".scala", ".groovy", ".gradle",
        ".go", ".rs", ".swift", ".dart", ".py", ".pyw", ".pyi", ".rb", ".php", ".pl", ".pm", ".lua", ".r", ".jl", ".sql", ".graphql", ".gql", ".proto",
        ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".reg", ".dockerfile", ".makefile", ".mk", ".cmake",
        ".tex", ".bib", ".rst", ".adoc", ".org", ".diff", ".patch", ".gitignore", ".gitattributes", ".editorconfig", ".srt", ".vtt", ".ass", ".lrc"
    };
    // Extensionless files people expect to read as text.
    private static readonly HashSet<string> TextNames = new(StringComparer.OrdinalIgnoreCase)
        { "Dockerfile", "Makefile", "LICENSE", "README", "CHANGELOG", "AUTHORS", "CONTRIBUTING", "NOTICE", "Gemfile", "Rakefile", "Procfile", ".gitignore", ".editorconfig" };

    public static bool IsAnimatable(string path) => Path.GetExtension(path) is var e &&
        (e.Equals(".gif", StringComparison.OrdinalIgnoreCase) || e.Equals(".webp", StringComparison.OrdinalIgnoreCase));
    public static bool IsSkiaImage(string path) => SkiaImages.Contains(Path.GetExtension(path));
    // Peek's Monaco limits: font size 2–100, maximum file size 1–102400 KB.
    public static int ClampFontSize(int size) => Math.Clamp(size, 2, 100);
    public static int ClampMaxTextKb(int kb) => Math.Clamp(kb, 1, 102400);

    // A file with no recognised extension is shown as text when its first bytes hold no NUL and decode as UTF-8,
    // as editors guess; UTF-16 is recognised by its byte-order mark.
    public static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && (head[0] == 0xFF && head[1] == 0xFE || head[0] == 0xFE && head[1] == 0xFF)) return true;
        if (head.IndexOf((byte)0) >= 0) return false;
        // A multi-byte sequence may be cut at the end of the sample.
        var trimmed = head;
        for (var i = 0; i < 3 && trimmed.Length > 0 && (trimmed[^1] & 0xC0) == 0x80; i++) trimmed = trimmed[..^1];
        if (trimmed.Length > 0 && trimmed[^1] >= 0xC0) trimmed = trimmed[..^1];
        return System.Text.Unicode.Utf8.IsValid(trimmed);
    }

    // Reads text the way editors do: a byte-order mark wins, then UTF-8, then the system ANSI code page (GBK on
    // Chinese Windows). Returns the text, the encoding name for the status line and whether the file was cut.
    public static (string Text, string Encoding, bool Truncated) ReadText(string path, long maxBytes, System.Text.Encoding? fallback)
    {
        using var stream = File.OpenRead(path);
        var truncated = stream.Length > maxBytes;
        var buffer = new byte[(int)Math.Min(stream.Length, maxBytes)];
        stream.ReadExactly(buffer);
        System.Text.Encoding encoding; var skip = 0;
        if (buffer.Length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) { encoding = new System.Text.UTF8Encoding(false); skip = 3; }
        else if (buffer.Length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) { encoding = System.Text.Encoding.Unicode; skip = 2; }
        else if (buffer.Length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF) { encoding = System.Text.Encoding.BigEndianUnicode; skip = 2; }
        else if (LooksLikeText(buffer) || fallback is null) encoding = new System.Text.UTF8Encoding(false);
        else encoding = fallback;
        var name = encoding switch
        {
            System.Text.UnicodeEncoding { CodePage: 1201 } => "UTF-16 BE", System.Text.UnicodeEncoding => "UTF-16 LE",
            System.Text.UTF8Encoding => skip == 3 ? "UTF-8 BOM" : "UTF-8", _ => encoding.WebName.ToUpperInvariant()
        };
        return (encoding.GetString(buffer, skip, buffer.Length - skip), name, truncated);
    }

    // Peek's order: its own previewers first, then a registered shell preview handler, then the information page.
    // Office files without a handler fall back to Tbox's own reader of the package.
    public static QuickLookKind Classify(string path, bool isDirectory, IReadOnlySet<string>? systemImages = null, Func<string, bool>? hasPreviewHandler = null)
    {
        var kind = ClassifyBuiltIn(path, isDirectory, systemImages);
        if (kind != QuickLookKind.Unsupported) return kind;
        if (hasPreviewHandler?.Invoke(Path.GetExtension(path)) == true) return QuickLookKind.PreviewHandler;
        return QuickLookOffice.KindOf(path) is not null ? QuickLookKind.Office : QuickLookKind.Unsupported;
    }

    private static QuickLookKind ClassifyBuiltIn(string path, bool isDirectory, IReadOnlySet<string>? systemImages)
    {
        if (isDirectory) return QuickLookKind.Folder;
        var extension = Path.GetExtension(path);
        if (SkiaImages.Contains(extension) || systemImages?.Contains(extension) == true) return QuickLookKind.Image;
        if (Videos.Contains(extension)) return QuickLookKind.Video;
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return QuickLookKind.Pdf;
        if (Archives.Contains(extension)) return QuickLookKind.Archive;
        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)) return QuickLookKind.Svg;
        if (Markdown.Contains(extension)) return QuickLookKind.Markdown;
        if (Texts.Contains(extension) || TextNames.Contains(Path.GetFileName(path))) return QuickLookKind.Text;
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
