using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SkiaSharp;
using Tbox.Core;
using TextMateSharp.Grammars;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace Tbox.Desktop.Views;

// Peek's Monaco preview: read-only, highlighted by file type, word wrap and font size from settings, cut at the
// configured size. A status line shows the encoding and a note when the file was cut.
internal sealed class QuickLookText(string path, QuickLookContext context) : QuickLookPreview(path, context)
{
    private static readonly RegistryOptions Grammars = new(ThemeName.LightPlus);
    private TextMate.Installation? _textMate;

    public override async Task LoadAsync(CancellationToken cancellationToken)
    {
        var settings = Context.Settings;
        var maxBytes = (long)QuickLookPolicy.ClampMaxTextKb(settings.MaxTextFileSizeKb) * 1024;
        var (text, encoding, truncated) = await Task.Run(() => QuickLookPolicy.ReadText(Path, maxBytes, Context.Source.AnsiEncoding), cancellationToken);
        var editor = new TextEditor
        {
            IsReadOnly = true, ShowLineNumbers = true, Background = Brushes.White, Foreground = Brush.Parse("#1A1B22"),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI, monospace"),
            FontSize = QuickLookPolicy.ClampFontSize(settings.TextFontSize), WordWrap = settings.WrapText,
            HorizontalScrollBarVisibility = settings.WrapText ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(4, 8, 8, 8), Focusable = true
        };
        editor.TextArea.TextView.Options.EnableHyperlinks = false;
        editor.TextArea.Caret.CaretBrush = Brushes.Transparent;
        editor.Document = new AvaloniaEdit.Document.TextDocument(text);
        var scope = Scope(Path);
        if (scope is not null)
        {
            try
            {
                _textMate = editor.InstallTextMate(Grammars);
                _textMate.SetGrammar(scope);
            }
            catch (Exception error) when (error is not OperationCanceledException) { Platform.AppLog.Write("QuickLook grammar: " + error.GetType().Name); }
        }
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(12, 6), Children =
        {
            Label(encoding, 12, Brush.Parse("#5F6270")),
            Label($"{editor.Document.LineCount:N0} 行", 12, Brush.Parse("#5F6270"))
        } };
        if (truncated) status.Children.Add(Label($"文件超过 {QuickLookPolicy.ReadableSize(maxBytes, false)}，只显示开头部分", 12, Brush.Parse("#9A6700")));
        var bar = new Border { BorderBrush = Brush.Parse("#E6E5EC"), BorderThickness = new Thickness(0, 1, 0, 0), Child = status };
        DockPanel.SetDock(bar, Dock.Bottom);
        View = new DockPanel { Background = Brushes.White, Children = { bar, editor } };
    }

    // TextMate grammars are chosen by extension; files named Dockerfile or Makefile have none.
    private static string? Scope(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var extension = name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ? ".dockerfile"
            : name.Equals("Makefile", StringComparison.OrdinalIgnoreCase) ? ".makefile" : System.IO.Path.GetExtension(path);
        if (extension is ".axaml" or ".xaml" or ".csproj" or ".props" or ".targets" or ".resx" or ".manifest" or ".plist") extension = ".xml";
        if (extension is ".jsonc" or ".json5") extension = ".json";
        if (string.IsNullOrEmpty(extension)) return null;
        try { return Grammars.GetScopeByExtension(extension); } catch (Exception) { return null; }
    }

    public override void Dispose() { _textMate?.Dispose(); _textMate = null; }
}

// Markdown laid out with native controls in a GitHub-like style: headings, paragraphs, lists, task lists, quotes,
// code blocks, tables, rules, links and local images. Raw HTML is shown as text.
internal sealed class QuickLookMarkdown(string path, QuickLookContext context) : QuickLookPreview(path, context)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseTaskLists().UseAutoLinks().UseEmphasisExtras().Build();
    private static readonly IBrush Text = Brush.Parse("#1F2328"), Muted = Brush.Parse("#59636E"), Line = Brush.Parse("#D1D9E0"), CodeBack = Brush.Parse("#F6F8FA"), Link = Brush.Parse("#0969DA");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI, monospace");
    private readonly List<Bitmap> _images = [];

    public override async Task LoadAsync(CancellationToken cancellationToken)
    {
        var settings = Context.Settings;
        var maxBytes = (long)QuickLookPolicy.ClampMaxTextKb(settings.MaxTextFileSizeKb) * 1024;
        var (text, _, truncated) = await Task.Run(() => QuickLookPolicy.ReadText(Path, maxBytes, Context.Source.AnsiEncoding), cancellationToken);
        var document = await Task.Run(() => Markdown.Parse(text, Pipeline), cancellationToken);
        var body = new StackPanel { Spacing = 16, Margin = new Thickness(32, 24), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (truncated) body.Children.Add(Note($"文件超过 {QuickLookPolicy.ReadableSize(maxBytes, false)}，只显示开头部分"));
        foreach (var block in document) if (Render(block) is { } control) body.Children.Add(control);
        View = new ScrollViewer { Background = Brushes.White, Focusable = true, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = body };
    }

    private Control? Render(MdBlock block) => block switch
    {
        HeadingBlock heading => Heading(heading),
        ParagraphBlock paragraph => Paragraph(paragraph.Inline, 16),
        ListBlock list => List(list),
        QuoteBlock quote => Quote(quote),
        FencedCodeBlock or CodeBlock => Code((LeafBlock)block),
        ThematicBreakBlock => new Border { Height = 4, Background = Line, CornerRadius = new CornerRadius(2) },
        Table table => TableView(table),
        HtmlBlock html => Code(html),
        LinkReferenceDefinitionGroup => null,
        ContainerBlock container => Stack(container),
        _ => null
    };

    private StackPanel Stack(ContainerBlock container)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var child in container) if (Render(child) is { } control) panel.Children.Add(control);
        return panel;
    }

    private Control Heading(HeadingBlock heading)
    {
        var size = heading.Level switch { 1 => 32, 2 => 24, 3 => 20, 4 => 16, 5 => 14, _ => 13.6 };
        var text = Paragraph(heading.Inline, size, FontWeight.SemiBold, heading.Level == 6 ? Muted : null);
        if (heading.Level > 2) return text;
        return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 6), Child = text };
    }

    private Control List(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 6 };
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var task = item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList check } ? check : null;
            var marker = task is not null ? (Control)new CheckBox { IsChecked = task.Checked, IsEnabled = false, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(0, 2, 0, 0) }
                : new TextBlock { Text = list.IsOrdered ? $"{number}." : "•", FontSize = 16, Foreground = Text, Margin = new Thickness(0, 0, 0, 0) };
            number++;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*") };
            marker.VerticalAlignment = VerticalAlignment.Top; marker.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(marker);
            grid.Children.Add(Ui.Column(Stack(item), 1));
            panel.Children.Add(grid);
        }
        return panel;
    }

    private Control Quote(QuoteBlock quote) => new Border
    {
        BorderBrush = Line, BorderThickness = new Thickness(4, 0, 0, 0), Padding = new Thickness(16, 0, 0, 0),
        Child = Tint(Stack(quote), Muted)
    };

    private static Control Tint(Control control, IBrush brush)
    {
        foreach (var text in control.GetLogicalDescendants().OfType<SelectableTextBlock>()) text.Foreground = brush;
        return control;
    }

    private static Control Code(LeafBlock block)
    {
        var lines = block.Lines.ToString();
        return new Border
        {
            Background = CodeBack, CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 12),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new SelectableTextBlock { Text = lines, FontFamily = Mono, FontSize = 13.6, Foreground = Text, TextWrapping = TextWrapping.NoWrap }
            }
        };
    }

    private Control TableView(Table table)
    {
        var columns = Math.Max(1, table.ColumnDefinitions.Count);
        var grid = new Grid();
        for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var rowIndex = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var cellIndex = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                if (cellIndex >= columns) break;
                var content = cell.FirstOrDefault() is ParagraphBlock paragraph ? Paragraph(paragraph.Inline, 14, row.IsHeader ? FontWeight.SemiBold : FontWeight.Normal) : new TextBlock();
                var alignment = table.ColumnDefinitions.Count > cellIndex ? table.ColumnDefinitions[cellIndex].Alignment : null;
                content.HorizontalAlignment = alignment switch { TableColumnAlign.Center => HorizontalAlignment.Center, TableColumnAlign.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Left };
                var border = new Border
                {
                    BorderBrush = Line, BorderThickness = new Thickness(cellIndex == 0 ? 1 : 0, rowIndex == 0 ? 1 : 0, 1, 1), Padding = new Thickness(13, 6),
                    Background = rowIndex % 2 == 0 && !row.IsHeader ? Brushes.White : row.IsHeader ? Brushes.White : CodeBack, Child = content
                };
                Grid.SetRow(border, rowIndex); Grid.SetColumn(border, cellIndex);
                grid.Children.Add(border);
                cellIndex++;
            }
            rowIndex++;
        }
        return new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = grid };
    }

    private Control Paragraph(ContainerInline? inline, double size, FontWeight? weight = null, IBrush? brush = null)
    {
        // A paragraph that is only an image shows the image itself.
        if (inline?.FirstChild is LinkInline { IsImage: true } image && image.NextSibling is null && Picture(image.Url) is { } picture) return picture;
        var text = new SelectableTextBlock { FontSize = size, Foreground = brush ?? Text, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.5, FontWeight = weight ?? FontWeight.Normal };
        if (inline is not null) Append(text.Inlines!, inline, size, weight ?? FontWeight.Normal, FontStyle.Normal, false);
        return text;
    }

    private void Append(InlineCollection target, ContainerInline container, double size, FontWeight weight, FontStyle style, bool strike)
    {
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling) Append(target, inline, size, weight, style, strike);
    }

    private void Append(InlineCollection target, MdInline inline, double size, FontWeight weight, FontStyle style, bool strike)
    {
        Run Run(string text) => new(text) { FontWeight = weight, FontStyle = style, TextDecorations = strike ? TextDecorations.Strikethrough : null };
        switch (inline)
        {
            case LiteralInline literal: target.Add(Run(literal.Content.ToString())); break;
            case TaskList: break;
            case LineBreakInline lineBreak: target.Add(lineBreak.IsHard ? new LineBreak() : Run(" ")); break;
            case CodeInline code: target.Add(new Run(code.Content) { FontFamily = Mono, Background = CodeBack, FontSize = size * 0.85 }); break;
            case EmphasisInline emphasis:
                var bold = emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount >= 2;
                var italic = emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount % 2 == 1;
                var struck = emphasis.DelimiterChar == '~';
                Append(target, emphasis, size, bold ? FontWeight.SemiBold : weight, italic ? FontStyle.Italic : style, strike || struck);
                break;
            case LinkInline { IsImage: true } image:
                target.Add(Run(image.FirstChild is LiteralInline alt ? $"[{alt.Content}]" : "[图片]"));
                break;
            case LinkInline link:
                var span = new Span { Foreground = Link };
                Append(span.Inlines, link, size, weight, style, strike);
                if (span.Inlines.Count == 0 && link.Url is { } url) span.Inlines.Add(new Run(url));
                target.Add(span);
                break;
            case AutolinkInline auto: target.Add(new Run(auto.Url) { Foreground = Link }); break;
            case HtmlInline html: target.Add(Run(html.Tag)); break;
            case HtmlEntityInline entity: target.Add(Run(entity.Transcoded.ToString())); break;
            case ContainerInline nested: Append(target, nested, size, weight, style, strike); break;
        }
    }

    // Only images next to the file are shown; nothing is fetched from the network.
    private Control? Picture(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Contains("://", StringComparison.Ordinal) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var file = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, Uri.UnescapeDataString(url.Split('#', '?')[0])));
            if (!File.Exists(file) || !QuickLookPolicy.IsSkiaImage(file)) return null;
            using var stream = File.OpenRead(file);
            var bitmap = Bitmap.DecodeToWidth(stream, 1800);
            _images.Add(bitmap);
            return new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left,
                MaxHeight = 900 };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static Control Note(string text) => new Border
    {
        Background = Brush.Parse("#FFF8C5"), BorderBrush = Brush.Parse("#D4A72C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 8), Child = Label(text, 13, Brush.Parse("#6F5000"))
    };

    public override void Dispose()
    {
        foreach (var image in _images) image.Dispose();
        _images.Clear();
    }
}

// SVG drawn by Svg.Skia at the display's pixel size; the window asks for the drawing's own size, like an image.
internal sealed class QuickLookSvg(string path, QuickLookContext context) : QuickLookPreview(path, context)
{
    private const double Fallback = 800, MaximumSide = 8192;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private Svg.Skia.SKSvg? _svg;
    private SKRect _bounds;
    private Bitmap? _bitmap;

    public override bool EffectivePixels => false;

    public override async Task<(double Width, double Height)?> MeasureAsync(CancellationToken cancellationToken)
    {
        _svg = await Task.Run(() =>
        {
            var svg = new Svg.Skia.SKSvg();
            if (svg.Load(Path) is null) { svg.Dispose(); throw new InvalidDataException("无法读取 SVG。"); }
            return svg;
        }, cancellationToken);
        _bounds = _svg.Picture!.CullRect;
        // Icons drawn at 16 or 24 units would open in a window larger than themselves; show them at 100% within it.
        var (width, height) = _bounds.Width > 0 && _bounds.Height > 0 ? (_bounds.Width, _bounds.Height) : ((float)Fallback, (float)Fallback);
        var physical = (width * Context.Scale, height * Context.Scale);
        _image.MaxWidth = width; _image.MaxHeight = height;
        View = new Panel { Background = Checker(), Children = { _image } };
        return physical;
    }

    public override async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_svg?.Picture is not { } picture || _bounds.Width <= 0 || _bounds.Height <= 0) throw new InvalidDataException("SVG 没有可绘制的内容。");
        // Rasterize at the size the window can show: within the monitor, at most 8192 px a side.
        var scale = Math.Min(Math.Min(Context.MonitorWidth / _bounds.Width, Context.MonitorHeight / _bounds.Height), Context.Scale);
        scale = Math.Min(scale, MaximumSide / Math.Max(_bounds.Width, _bounds.Height));
        if (scale <= 0) scale = 1;
        var width = Math.Max(1, (int)Math.Ceiling(_bounds.Width * scale)); var height = Math.Max(1, (int)Math.Ceiling(_bounds.Height * scale));
        var pixels = await Task.Run(() =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale((float)scale);
            surface.Canvas.Translate(-_bounds.Left, -_bounds.Top);
            surface.Canvas.DrawPicture(picture);
            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            return bitmap.Bytes;
        }, cancellationToken);
        var writeable = new WriteableBitmap(new PixelSize(width, height), new Vector(96 * scale, 96 * scale), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var target = writeable.Lock())
            for (var row = 0; row < height; row++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, row * width * 4, target.Address + row * target.RowBytes, width * 4);
        _bitmap = writeable;
        _image.Source = writeable;
    }

    // Transparent drawings sit on a light checkerboard, as image viewers show them.
    private static IBrush Checker()
    {
        var tile = new DrawingGroup { Children =
        {
            new GeometryDrawing { Brush = Brushes.White, Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)) },
            new GeometryDrawing { Brush = Brush.Parse("#EEEEF2"), Geometry = new RectangleGeometry(new Rect(0, 0, 8, 8)) },
            new GeometryDrawing { Brush = Brush.Parse("#EEEEF2"), Geometry = new RectangleGeometry(new Rect(8, 8, 8, 8)) }
        } };
        return new DrawingBrush(tile) { TileMode = TileMode.Tile, DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute) };
    }

    public override void Dispose() { _bitmap?.Dispose(); _bitmap = null; _svg?.Dispose(); _svg = null; }
}

// 7z and rar listings through SharpCompress, into the same tree and totals as zip and tar.
internal static class QuickLookSharpArchive
{
    public static QuickLookArchive Read(string path, CancellationToken cancellationToken)
    {
        var builder = new QuickLookArchiveBuilder();
        using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(path, new SharpCompress.Readers.ReaderOptions());
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Key is not { Length: > 0 } key) continue;
            builder.Add(key, entry.IsDirectory, entry.IsDirectory ? 0 : entry.Size);
        }
        return builder.Build(new FileInfo(path).Length);
    }
}
