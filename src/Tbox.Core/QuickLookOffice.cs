using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Tbox.Core;

public enum QuickLookOfficeKind { Word, Excel, PowerPoint }

// What 速览 shows for an Office file when no system preview handler is installed: the thumbnail Office stores in
// the package, plus its text. Word gives paragraphs, Excel the first sheet as rows, PowerPoint each slide's text.
public sealed record QuickLookOffice(QuickLookOfficeKind Kind, byte[]? Thumbnail, IReadOnlyList<string> Paragraphs,
    IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<IReadOnlyList<string>> Slides, string? SheetName, bool Truncated)
{
    public const int MaximumParagraphs = 5000, MaximumRows = 1000, MaximumColumns = 50, MaximumSlides = 500;
    private const long MaximumPartBytes = 64L * 1024 * 1024;
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static QuickLookOfficeKind? KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" or ".docm" or ".dotx" or ".dotm" => QuickLookOfficeKind.Word,
        ".xlsx" or ".xlsm" or ".xltx" or ".xltm" => QuickLookOfficeKind.Excel,
        ".pptx" or ".pptm" or ".potx" or ".potm" or ".ppsx" or ".ppsm" => QuickLookOfficeKind.PowerPoint,
        _ => null
    };

    public static QuickLookOffice Read(string path, CancellationToken cancellationToken)
    {
        var kind = KindOf(path) ?? throw new NotSupportedException("不是 Office Open XML 文件。");
        using var zip = ZipFile.OpenRead(path);
        var thumbnail = ReadThumbnail(zip);
        var truncated = false;
        List<string> paragraphs = []; List<IReadOnlyList<string>> rows = [], slides = [];
        string? sheetName = null;
        switch (kind)
        {
            case QuickLookOfficeKind.Word:
                if (Load(zip, "word/document.xml") is { } document)
                    foreach (var paragraph in document.Descendants(W + "p"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (paragraphs.Count >= MaximumParagraphs) { truncated = true; break; }
                        paragraphs.Add(WordText(paragraph));
                    }
                TrimBlank(paragraphs);
                break;
            case QuickLookOfficeKind.Excel:
                (sheetName, truncated) = FirstSheet(zip, rows, cancellationToken);
                break;
            case QuickLookOfficeKind.PowerPoint:
                truncated = ReadSlides(zip, slides, cancellationToken);
                break;
        }
        return new QuickLookOffice(kind, thumbnail, paragraphs, rows, slides, sheetName, truncated);
    }

    // docProps/thumbnail.* is written when the user saves with "Save preview picture"; it is optional.
    private static byte[]? ReadThumbnail(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("docProps/thumbnail.", StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(e.FullName).ToLowerInvariant() is ".jpeg" or ".jpg" or ".png");
        if (entry is null || entry.Length is 0 or > 16 * 1024 * 1024) return null;
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static XDocument? Load(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        if (entry.Length > MaximumPartBytes) throw new InvalidDataException("文档过大，无法预览。");
        using var stream = entry.Open();
        // Office parts never need a DTD; refusing it blocks entity expansion.
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private static string WordText(XElement paragraph)
    {
        var text = new System.Text.StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (node.Name == W + "t") text.Append(node.Value);
            else if (node.Name == W + "tab") text.Append('\t');
            else if (node.Name == W + "br" || node.Name == W + "cr") text.Append('\n');
        }
        return text.ToString();
    }

    private static void TrimBlank(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
    }

    // Targets in .rels are relative to the folder of the part that owns them.
    private static string Resolve(string ownerFolder, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = new List<string>(ownerFolder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var part in target.Split('/'))
        {
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (part != ".") parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static Dictionary<string, string> Relationships(ZipArchive zip, string folder, string file)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Load(zip, $"{folder}/_rels/{file}.rels") is not { } rels) return map;
        foreach (var rel in rels.Descendants(Rel + "Relationship"))
            if (rel.Attribute("Id")?.Value is { } id && rel.Attribute("Target")?.Value is { } target && rel.Attribute("TargetMode")?.Value != "External")
                map[id] = Resolve(folder, target);
        return map;
    }

    private static (string? Name, bool Truncated) FirstSheet(ZipArchive zip, List<IReadOnlyList<string>> rows, CancellationToken cancellationToken)
    {
        if (Load(zip, "xl/workbook.xml") is not { } workbook) return (null, false);
        var sheet = workbook.Descendants(S + "sheet").FirstOrDefault(s => s.Attribute("state")?.Value is not ("hidden" or "veryHidden"))
            ?? workbook.Descendants(S + "sheet").FirstOrDefault();
        if (sheet?.Attribute(R + "id")?.Value is not { } id || !Relationships(zip, "xl", "workbook.xml").TryGetValue(id, out var part)) return (sheet?.Attribute("name")?.Value, false);
        var shared = Load(zip, "xl/sharedStrings.xml")?.Root?.Elements(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList() ?? [];
        if (Load(zip, part) is not { } data) return (sheet.Attribute("name")?.Value, false);
        var truncated = false;
        foreach (var row in data.Descendants(S + "row"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count >= MaximumRows) { truncated = true; break; }
            var cells = new List<string>();
            foreach (var cell in row.Elements(S + "c"))
            {
                // Cells may skip columns; the reference ("C7") says where each one goes.
                var column = Column(cell.Attribute("r")?.Value) ?? cells.Count;
                if (column >= MaximumColumns) { truncated = true; break; }
                while (cells.Count < column) cells.Add("");
                cells.Add(CellText(cell, shared));
            }
            while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
            // Blank rows between data are kept so the layout reads like the sheet; trailing ones are dropped below.
            var index = int.TryParse(row.Attribute("r")?.Value, out var r) ? r - 1 : rows.Count;
            while (rows.Count < index && rows.Count < MaximumRows) rows.Add([]);
            if (rows.Count < MaximumRows) rows.Add(cells);
        }
        while (rows.Count > 0 && rows[^1].Count == 0) rows.RemoveAt(rows.Count - 1);
        return (sheet.Attribute("name")?.Value, truncated);
    }

    internal static int? Column(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var column = 0; var letters = 0;
        foreach (var c in reference)
        {
            if (c is >= 'A' and <= 'Z') { column = column * 26 + (c - 'A' + 1); letters++; }
            else if (c is >= 'a' and <= 'z') { column = column * 26 + (c - 'a' + 1); letters++; }
            else break;
            if (letters > 3) return null;
        }
        return letters == 0 ? null : column - 1;
    }

    private static string CellText(XElement cell, List<string> shared)
    {
        var type = cell.Attribute("t")?.Value;
        if (type == "inlineStr") return string.Concat(cell.Descendants(S + "t").Select(t => t.Value));
        var value = cell.Element(S + "v")?.Value ?? "";
        return type switch
        {
            "s" => int.TryParse(value, out var index) && index >= 0 && index < shared.Count ? shared[index] : "",
            "b" => value == "1" ? "TRUE" : "FALSE",
            _ => value
        };
    }

    private static bool ReadSlides(ZipArchive zip, List<IReadOnlyList<string>> slides, CancellationToken cancellationToken)
    {
        if (Load(zip, "ppt/presentation.xml") is not { } presentation) return false;
        var rels = Relationships(zip, "ppt", "presentation.xml");
        foreach (var slide in presentation.Descendants(P + "sldId"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slides.Count >= MaximumSlides) return true;
            if (slide.Attribute(R + "id")?.Value is not { } id || !rels.TryGetValue(id, out var part) || Load(zip, part) is not { } xml) continue;
            var lines = xml.Descendants(A + "p").Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value)))
                .Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
            slides.Add(lines);
        }
        return false;
    }
}
