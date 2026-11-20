using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using SkiaSharp;
using Tbox.Core;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Views;

// What a preview needs from its window: system services, the display it opens on and the shared video engine.
internal sealed record QuickLookContext(IQuickLookSource Source, double Scale, int MonitorWidth, int MonitorHeight,
    Func<Task<LibVLC>> Engine, QuickLookSettings Settings)
{
    public bool Tooltips => Settings.ShowFileInfoTooltip;
}

// One previewer, as in Peek's FilePreviewer: it reports the size it wants, then loads. Measure must be quick.
internal abstract class QuickLookPreview(string path, QuickLookContext context) : IDisposable
{
    public string Path { get; } = path;
    protected QuickLookContext Context { get; } = context;
    public Control View { get; protected set; } = new Panel();
    // Content size the window should fit; null takes 80% of the monitor. Physical pixels unless EffectivePixels.
    public virtual Task<(double Width, double Height)?> MeasureAsync(CancellationToken cancellationToken) =>
        Task.FromResult<(double, double)?>(null);
    public virtual bool EffectivePixels => true;
    public abstract Task LoadAsync(CancellationToken cancellationToken);
    // Image, video and audio previews carry Peek's file-information tooltip.
    public virtual bool HasTooltip => false;
    public virtual void TogglePlayback() { }
    // Raised when a preview fails after it is shown (a system preview handler that cannot draw the file).
    public event Action<QuickLookPreview>? Failed;
    protected void Fail() => Failed?.Invoke(this);
    public virtual void Dispose() { }

    public static QuickLookPreview Create(string path, QuickLookContext context)
    {
        var directory = Directory.Exists(path);
        return QuickLookPolicy.Classify(path, directory, context.Source.ImageExtensions, context.Source.HasPreviewHandler) switch
        {
            QuickLookKind.PreviewHandler => new QuickLookShellHandler(path, context),
            QuickLookKind.Office => new QuickLookOfficeView(path, context),
            QuickLookKind.Image => new QuickLookImage(path, context),
            QuickLookKind.Video => new QuickLookVideo(path, context),
            QuickLookKind.Pdf => new QuickLookPdf(path, context),
            QuickLookKind.Archive => new QuickLookArchiveView(path, context),
            QuickLookKind.Text => new QuickLookText(path, context),
            QuickLookKind.Markdown => new QuickLookMarkdown(path, context),
            QuickLookKind.Svg => new QuickLookSvg(path, context),
            _ when !directory && SniffText(path) => new QuickLookText(path, context),
            _ => new QuickLookInfo(path, context)
        };
    }

    // An unknown file reads as text when its first 4 KB look like text and it is within the text size limit.
    private static bool SniffText(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length == 0 || stream.Length > 102400L * 1024) return false;
            Span<byte> head = stackalloc byte[(int)Math.Min(4096, stream.Length)];
            stream.ReadExactly(head);
            return QuickLookPolicy.LooksLikeText(head);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static string Date(DateTime time) => time.ToString("yyyy/M/d H:mm", CultureInfo.InvariantCulture);
    internal static TextBlock Label(string text, double size = 14, IBrush? brush = null) => new()
    {
        Text = text, FontSize = size, Foreground = brush ?? Brush.Parse("#1A1B22"), TextWrapping = TextWrapping.Wrap
    };
}

internal sealed class QuickLookImage(string path, QuickLookContext context) : QuickLookPreview(path, context)
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Bitmap> _bitmaps = [];
    private DispatcherTimer? _animation;
    private (int Width, int Height) _pixels;
    private bool _systemCodec;

    public override bool EffectivePixels => false;
    public override bool HasTooltip => true;

    public override async Task<(double Width, double Height)?> MeasureAsync(CancellationToken cancellationToken)
    {
        if (await Context.Source.ReadImageSizeAsync(Path, cancellationToken) is { } size) { _pixels = size; _systemCodec = true; }
        else
        {
            using var codec = SKCodec.Create(Path) ?? throw new InvalidDataException("无法读取图片。");
            _pixels = (codec.Info.Width, codec.Info.Height);
        }
        // Peek shows images at most at 100%: their pixels divided by the display scale.
        _image.MaxWidth = _pixels.Width / Context.Scale; _image.MaxHeight = _pixels.Height / Context.Scale;
        View = new Panel { Children = { _image } };
        return (_pixels.Width, _pixels.Height);
    }

    public override async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (QuickLookPolicy.IsAnimatable(Path) && await Task.Run(() => DecodeFrames(cancellationToken), cancellationToken) is { } frames)
        {
            _bitmaps.AddRange(frames.Select(frame => frame.Bitmap));
            _image.Source = frames[0].Bitmap;
            var index = 0;
            _animation = new DispatcherTimer { Interval = frames[0].Duration };
            _animation.Tick += (_, _) =>
            {
                index = (index + 1) % frames.Count;
                _image.Source = frames[index].Bitmap; _animation.Interval = frames[index].Duration;
            };
            _animation.Start();
            return;
        }
        // The window is at most 80% of the monitor, so decoding beyond the monitor's pixels adds nothing.
        var width = Math.Min(_pixels.Width, Context.MonitorWidth); var height = Math.Min(_pixels.Height, Context.MonitorHeight);
        Bitmap? bitmap;
        try
        {
            bitmap = _systemCodec ? await Context.Source.DecodeImageAsync(Path, width, height, cancellationToken)
                : await Task.Run(() => { using var stream = File.OpenRead(Path); return Bitmap.DecodeToWidth(stream, (int)Math.Min(width, height * (double)_pixels.Width / _pixels.Height)); }, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Peek falls back to the shell thumbnail when the full image cannot be decoded.
            bitmap = await Context.Source.ThumbnailAsync(Path, Math.Max(width, height), thumbnailOnly: true, cancellationToken);
            if (bitmap is null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        }
        _bitmaps.Add(bitmap);
        _image.Source = bitmap;
    }

    private sealed record Frame(Bitmap Bitmap, TimeSpan Duration);

    // Animated GIF and WebP, all frames decoded up front within a memory budget; larger animations show their first frame.
    private List<Frame>? DecodeFrames(CancellationToken cancellationToken)
    {
        using var codec = SKCodec.Create(Path);
        if (codec is null || codec.FrameCount < 2) return null;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if ((long)info.BytesSize * codec.FrameCount > 384L * 1024 * 1024) return null;
        var frames = codec.FrameInfo;
        var decoded = new List<SKBitmap>();
        var result = new List<Frame>();
        try
        {
            for (var index = 0; index < frames.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bitmap = new SKBitmap(info);
                var required = frames[index].RequiredFrame;
                if (required >= 0 && required < decoded.Count) decoded[required].CopyTo(bitmap);
                else bitmap.Erase(SKColors.Transparent);
                var options = new SKCodecOptions(index, required);
                var status = codec.GetPixels(info, bitmap.GetPixels(), options);
                if (status is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) { bitmap.Dispose(); break; }
                decoded.Add(bitmap);
                var pixels = bitmap.Bytes;
                var avalonia = new WriteableBitmap(new PixelSize(info.Width, info.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var target = avalonia.Lock())
                    for (var row = 0; row < info.Height; row++)
                        System.Runtime.InteropServices.Marshal.Copy(pixels, row * info.RowBytes, target.Address + row * target.RowBytes, info.Width * 4);
                // Browsers treat very short frame delays as 100 ms.
                var duration = frames[index].Duration <= 10 ? 100 : frames[index].Duration;
                result.Add(new Frame(avalonia, TimeSpan.FromMilliseconds(duration)));
            }
            if (result.Count >= 2) return result;
            foreach (var frame in result) frame.Bitmap.Dispose();
            return null;
        }
        catch
        {
            foreach (var frame in result) frame.Bitmap.Dispose();
            throw;
        }
        finally { foreach (var bitmap in decoded) bitmap.Dispose(); }
    }

    public override void Dispose()
    {
        _animation?.Stop();
        _image.Source = null;
        foreach (var bitmap in _bitmaps) bitmap.Dispose();
        _bitmaps.Clear();
    }
}

internal sealed class QuickLookVideo(string path, QuickLookContext context) : QuickLookPreview(path, context)
{
    private const double ControlsHeight = 48;
    private readonly VideoView _video = new();
    private readonly Slider _position = new() { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new() { FontSize = 12, Foreground = Brush.Parse("#5F6270"), VerticalAlignment = VerticalAlignment.Center, Text = "00:00 / 00:00" };
    private readonly TextBlock _error = new() { FontSize = 14, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Button? _play;
    private LibVLCSharp.Shared.Media? _media;
    private MediaPlayer? _player;
    private bool _updating, _disposed;

    public override bool EffectivePixels => false;
    public override bool HasTooltip => true;

    public override async Task<(double Width, double Height)?> MeasureAsync(CancellationToken cancellationToken)
    {
        var engine = await Context.Engine();
        cancellationToken.ThrowIfCancellationRequested();
        _media = new LibVLCSharp.Shared.Media(engine, Path, FromType.FromPath);
        await _media.Parse(MediaParseOptions.ParseLocal, 3000, cancellationToken);
        BuildView();
        foreach (var track in _media.Tracks)
        {
            if (track.TrackType != TrackType.Video || track.Data.Video.Width == 0 || track.Data.Video.Height == 0) continue;
            double width = track.Data.Video.Width, height = track.Data.Video.Height;
            if (track.Data.Video.SarNum > 0 && track.Data.Video.SarDen > 0) width = width * track.Data.Video.SarNum / track.Data.Video.SarDen;
            if (track.Data.Video.Orientation is VideoOrientation.LeftBottom or VideoOrientation.RightTop or VideoOrientation.LeftTop or VideoOrientation.RightBottom)
                (width, height) = (height, width);
            return (width, height + ControlsHeight * Context.Scale);
        }
        return null;
    }

    private void BuildView()
    {
        _play = Ui.IconButton(IconKind.Pause, "播放 / 暂停", TogglePlayback);
        var controls = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Height = ControlsHeight, MaxWidth = 800,
            Margin = new Thickness(16, 0), Children = { _play, Ui.Column(_position, 1), Ui.Column(_time, 2) }
        };
        DockPanel.SetDock(controls, Dock.Bottom);
        var canvas = new Grid { Background = Brush.Parse("#111217"), Children = { _video, _error } };
        View = new DockPanel { Children = { controls, canvas } };
        _position.ValueChanged += (_, _) => { if (!_updating && _player is { IsSeekable: true } player) player.Time = (long)_position.Value; };
        _timer.Tick += (_, _) => Update();
    }

    public override async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_media is null) return;
        var engine = await Context.Engine();
        cancellationToken.ThrowIfCancellationRequested();
        _player = new MediaPlayer(engine);
        _player.EncounteredError += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            _error.Text = $"你的设备不支持 {System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant()} 格式。";
            _error.IsVisible = true;
        });
        _video.MediaPlayer = _player;
        // Peek plays videos automatically and does not loop them.
        _player.Play(_media);
        _timer.Start();
    }

    public override void TogglePlayback()
    {
        if (_player is not { } player || _disposed) return;
        if (player.IsPlaying) player.Pause();
        else if (player.State == VLCState.Ended) { var media = _media; Task.Run(() => { player.Stop(); player.Play(media!); }); }
        else player.Play();
        Update();
    }

    private void Update()
    {
        if (_player is not { } player || _disposed) return;
        _updating = true;
        _position.Maximum = Math.Max(1, player.Length); _position.Value = Math.Max(0, player.Time);
        _updating = false;
        _time.Text = $"{RecordingReviewWindow.FormatTime(TimeSpan.FromMilliseconds(Math.Max(0, player.Time)))} / {RecordingReviewWindow.FormatTime(TimeSpan.FromMilliseconds(Math.Max(0, player.Length)))}";
        if (_play is not null) _play.Content = new LineIcon(player.IsPlaying ? IconKind.Pause : IconKind.Play, brush: Brush.Parse("#1A1B22")) { Width = 16, Height = 16 };
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        var player = _player; var media = _media;
        _video.MediaPlayer = null;
        // Stopping LibVLC can block for a moment; the file handle is released when it returns.
        Task.Run(() =>
        {
            try { player?.Stop(); player?.Dispose(); media?.Dispose(); }
            catch (Exception error) { AppLog.Write("QuickLook video release: " + error.GetType().Name); }
        });
    }
}

internal sealed class QuickLookPdf(string path, QuickLookContext context) : QuickLookPreview(path, context)
{
    private const double Gap = 16;
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = true };
    private readonly StackPanel _pages = new() { Spacing = Gap, Margin = new Thickness(Gap) };
    private readonly List<Page> _items = [];
    private readonly DispatcherTimer _render = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly CancellationTokenSource _stop = new();
    private IQuickLookPdf? _document;
    private bool _rendering;

    private sealed class Page(Border frame, Image image, double ratio)
    {
        public Border Frame { get; } = frame;
        public Image Image { get; } = image;
        public double Ratio { get; } = ratio;
        public int RenderedWidth { get; set; }
        public Bitmap? Bitmap { get; set; }
    }

    public override async Task LoadAsync(CancellationToken cancellationToken)
    {
        _document = await Context.Source.OpenPdfAsync(Path, cancellationToken);
        for (var index = 0; index < _document.PageCount; index++)
        {
            var (width, height) = _document.PageSize(index);
            var image = new Image { Stretch = Stretch.Fill };
            var frame = new Border { Background = Brushes.White, Child = image, HorizontalAlignment = HorizontalAlignment.Center,
                BoxShadow = BoxShadows.Parse("0 1 4 0 #24101220") };
            _items.Add(new Page(frame, image, height / Math.Max(1, width)));
            _pages.Children.Add(frame);
        }
        _scroll.Content = _pages;
        _scroll.SizeChanged += (_, _) => { Layout(); Schedule(); };
        _scroll.ScrollChanged += (_, _) => Schedule();
        _render.Tick += (_, _) => { _render.Stop(); _ = RenderVisibleAsync(); };
        // Up and down scroll the document; left and right still move between files.
        _scroll.KeyDown += (_, e) =>
        {
            var line = 48; var page = Math.Max(line, _scroll.Viewport.Height - line);
            var offset = e.Key switch
            {
                Key.Up => -line, Key.Down => line, Key.PageUp => -page, Key.PageDown or Key.Space => page,
                Key.Home => double.NegativeInfinity, Key.End => double.PositiveInfinity, _ => double.NaN
            };
            if (double.IsNaN(offset) || e.KeyModifiers != KeyModifiers.None) return;
            var y = double.IsNegativeInfinity(offset) ? 0 : double.IsPositiveInfinity(offset) ? _scroll.Extent.Height : _scroll.Offset.Y + offset;
            _scroll.Offset = new Vector(0, Math.Clamp(y, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));
            e.Handled = true;
        };
        View = _scroll;
        Dispatcher.UIThread.Post(() => _scroll.Focus(), DispatcherPriority.Background);
    }

    private double PageWidth => Math.Max(80, _scroll.Viewport.Width - Gap * 2);

    private void Layout()
    {
        var width = PageWidth;
        foreach (var page in _items) { page.Frame.Width = width; page.Frame.Height = width * page.Ratio; }
    }

    private void Schedule() { _render.Stop(); _render.Start(); }

    private async Task RenderVisibleAsync()
    {
        if (_rendering || _document is null || _stop.IsCancellationRequested) return;
        _rendering = true;
        try
        {
            var top = _scroll.Offset.Y; var height = Math.Max(1, _scroll.Viewport.Height);
            var pixels = (int)Math.Round(PageWidth * Context.Scale);
            for (var index = 0; index < _items.Count; index++)
            {
                var page = _items[index];
                var bounds = page.Frame.Bounds;
                var near = bounds.Bottom >= top - height && bounds.Top <= top + height * 2;
                if (!near)
                {
                    // Pages far from the viewport give their memory back.
                    if (page.Bitmap is not null && (bounds.Bottom < top - height * 4 || bounds.Top > top + height * 5))
                    { page.Image.Source = null; page.Bitmap.Dispose(); page.Bitmap = null; page.RenderedWidth = 0; }
                    continue;
                }
                if (page.RenderedWidth == pixels) continue;
                var bitmap = await _document.RenderAsync(index, pixels, _stop.Token);
                if (_stop.IsCancellationRequested) { bitmap.Dispose(); return; }
                var previous = page.Bitmap;
                page.Bitmap = bitmap; page.RenderedWidth = pixels; page.Image.Source = bitmap;
                previous?.Dispose();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Write("QuickLook PDF render: " + error.GetType().Name); }
        finally { _rendering = false; }
    }

    public override void Dispose()
    {
        _stop.Cancel(); _render.Stop();
        foreach (var page in _items) { page.Image.Source = null; page.Bitmap?.Dispose(); page.Bitmap = null; }
        _document?.Dispose();
    }
}

internal sealed class QuickLookArchiveView(string path, QuickLookContext context) : QuickLookPreview(path, context)
{
    private const int MaximumChildren = 2000;
    private static readonly IBrush Secondary = Brush.Parse("#5F6270");

    public override async Task LoadAsync(CancellationToken cancellationToken)
    {
        var sharp = System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".7z" or ".rar";
        var archive = await Task.Run(() => sharp ? QuickLookSharpArchive.Read(Path, cancellationToken) : QuickLookArchive.Read(Path, cancellationToken), cancellationToken);
        var tree = new TreeView { Margin = new Thickness(8, 8, 8, 0) };
        // Peek expands only the root level.
        foreach (var entry in archive.Root) tree.Items.Add(Item(entry, expand: true));
        Grid Totals()
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*,Auto,*") };
            void Cell(string text, int column) => grid.Children.Add(Ui.Column(new TextBlock
            {
                Text = text, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush.Parse("#1A1B22"), TextTrimming = TextTrimming.CharacterEllipsis
            }, column));
            void Divider(int column) => grid.Children.Add(Ui.Column(new Border { Width = 1, Height = 20, Background = Brush.Parse("#E6E5EC") }, column));
            Cell($"{archive.Directories:N0} 目录", 0); Divider(1); Cell($"{archive.Files:N0} 个文件", 2); Divider(3);
            Cell($"{QuickLookPolicy.ReadableSize(archive.ArchiveSize, false)} (已提取 {QuickLookPolicy.ReadableSize(archive.ExtractedSize, false)})", 4);
            return grid;
        }
        var totals = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(12),
            BorderBrush = Brush.Parse("#E6E5EC"), BorderThickness = new Thickness(1), Child = Totals() };
        DockPanel.SetDock(totals, Dock.Bottom);
        View = new DockPanel { Children = { totals, tree } };
    }

    private TreeViewItem Item(QuickLookArchiveEntry entry, bool expand)
    {
        var icon = Context.Source.SmallIcon(entry.Name, entry.IsDirectory);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            icon is null ? new LineIcon(entry.IsDirectory ? IconKind.Folder : IconKind.File) { Width = 16, Height = 16 } : new Image { Source = icon, Width = 16, Height = 16 },
            new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center }
        } };
        if (!entry.IsDirectory) header.Children.Add(new TextBlock { Text = QuickLookPolicy.ReadableSize(entry.Size, false), Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
        var item = new TreeViewItem { Header = header };
        if (!entry.IsDirectory || entry.Children.Count == 0) return item;
        var filled = false;
        void Fill()
        {
            if (filled) return;
            filled = true; item.Items.Clear();
            foreach (var child in entry.Children.Take(MaximumChildren)) item.Items.Add(Item(child, expand: false));
            if (entry.Children.Count > MaximumChildren)
                item.Items.Add(new TreeViewItem { Header = new TextBlock { Text = $"还有 {entry.Children.Count - MaximumChildren:N0} 项", Foreground = Secondary } });
        }
        item.Items.Add(new TreeViewItem());
        item.PropertyChanged += (_, e) => { if (e.Property == TreeViewItem.IsExpandedProperty && item.IsExpanded) Fill(); };
        if (expand) { Fill(); item.IsExpanded = true; }
        return item;
    }
}

// Peek's UnsupportedFilePreview, also used for folders: icon on the left, name and details on the right.
internal sealed class QuickLookInfo(string path, QuickLookContext context) : QuickLookPreview(path, context)
{
    private readonly CancellationTokenSource _stop = new();
    private Bitmap? _icon;

    public override Task<(double Width, double Height)?> MeasureAsync(CancellationToken cancellationToken) =>
        Task.FromResult<(double, double)?>((QuickLookPolicy.InfoWidth, QuickLookPolicy.InfoHeight));

    public override async Task LoadAsync(CancellationToken cancellationToken)
    {
        var directory = Directory.Exists(Path);
        var info = directory ? (FileSystemInfo)new DirectoryInfo(Path) : new FileInfo(Path);
        if (!info.Exists) throw new FileNotFoundException("文件不存在。", Path);
        var type = Context.Source.TypeName(Path, directory);
        _icon = await Context.Source.ThumbnailAsync(Path, (int)Math.Round(180 * Context.Scale), thumbnailOnly: false, cancellationToken);
        Control icon = _icon is null ? new LineIcon(directory ? IconKind.Folder : IconKind.File) { Width = 120, Height = 120 }
            : new Image { Source = _icon, Stretch = Stretch.Uniform };
        var details = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = info.Name.Length > 0 ? info.Name : Path, FontSize = 28, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush.Parse("#1A1B22"), Margin = new Thickness(0, 0, 0, 6) },
            Label($"文件类型: {type}")
        } };
        var size = Label("大小: ");
        details.Children.Add(size);
        var dates = Label($"修改日期: {Date(info.LastWriteTime)}");
        if (directory)
        {
            var contains = Label("包含: ");
            var scanning = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new QuickLookSpinner { Width = 14, Height = 14 }, Label("正在扫描…") } };
            var warning = new LineIcon(IconKind.Warn, brush: Brush.Parse(DesignTokens.Attention)) { Width = 14, Height = 14, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
            var containsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { contains, warning } };
            details.Children.Add(containsRow); details.Children.Add(scanning);
            void Show(QuickLookFolderTotals totals)
            {
                var pending = !totals.Done && totals.Files == 0 && totals.Folders == 0;
                scanning.IsVisible = pending; containsRow.IsVisible = !pending; size.IsVisible = !pending;
                var suffix = totals.Done && totals.Incomplete ? " (未完成)" : "";
                size.Text = $"大小: {QuickLookPolicy.ReadableSize(totals.Bytes)}{suffix}";
                contains.Text = $"包含: {QuickLookPolicy.FolderContents(totals.Files, totals.Folders)}{suffix}";
                warning.IsVisible = totals.Done && totals.Incomplete;
            }
            Show(default);
            var token = _stop.Token;
            // Recursive totals grow live, refreshed at most 15 times a second.
            _ = Task.Run(() => QuickLookFolderScan.Run(Path, totals => Dispatcher.UIThread.Post(() => { if (!token.IsCancellationRequested) Show(totals); }),
                TimeSpan.FromMilliseconds(1000 / 15.0), token), token);
        }
        else size.Text = $"大小: {QuickLookPolicy.ReadableSize(((FileInfo)info).Length)}";
        details.Children.Add(dates);
        var frame = new Border { Width = 180, Height = 180, VerticalAlignment = VerticalAlignment.Center, Child = icon };
        View = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 24, Margin = new Thickness(24), Children = { frame, Ui.Column(details, 1) } };
    }

    public override void Dispose() { _stop.Cancel(); _icon?.Dispose(); }
}

// Peek's ProgressRing: an arc turning once a second.
internal sealed class QuickLookSpinner : Control
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double _angle;
    public QuickLookSpinner()
    {
        Width = Height = 32;
        _timer.Tick += (_, _) => { _angle = (_angle + 6) % 360; InvalidateVisual(); };
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }
    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var thickness = Math.Max(2, size / 10);
        var radius = (size - thickness) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var start = _angle * Math.PI / 180; var end = start + Math.PI * 1.4;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start)), false);
            g.ArcTo(new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end)), new Size(radius, radius), 0, true, SweepDirection.Clockwise);
        }
        context.DrawGeometry(null, new Pen(Brush.Parse(DesignTokens.Accent), thickness, lineCap: PenLineCap.Round), geometry);
    }
}
