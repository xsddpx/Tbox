using Avalonia.Media.Imaging;
using Tbox.Core;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Tests;

// 速览 without Explorer: activation is driven by Activate, the selection and folder are set by the test.
internal sealed class FakeQuickLook : IQuickLookSource
{
    public Action<nint>? Activated { get; private set; }
    public bool SpaceToActivate { get; private set; }
    public Shortcut? Shortcut { get; private set; }
    public bool IsPaused { get; private set; }
    public QuickLookSelection? Selection { get; set; }
    public IReadOnlyList<string> Folder { get; set; } = [];
    public List<string> Deleted { get; } = [];
    public List<string> Opened { get; } = [];

    public void Start(bool spaceToActivate, Shortcut shortcut, Action<nint> activated) { SpaceToActivate = spaceToActivate; Shortcut = shortcut; Activated = activated; }
    public void Stop() => Activated = null;
    public bool Paused { set => IsPaused = value; }
    public nint PreviewWindow { set { } }
    public Task<QuickLookSelection?> ReadSelectionAsync(nint window, CancellationToken cancellationToken) => Task.FromResult(Selection);
    public Task<IReadOnlyList<string>> ReadFolderAsync(nint window, CancellationToken cancellationToken) => Task.FromResult(Folder);
    public QuickLookMonitor MonitorOf(nint window) => new(new DesktopPlacement(0, 0, 1920, 1080), 1);
    public void BringToForeground(nint window) { }
    public IReadOnlySet<string> ImageExtensions { get; } = new HashSet<string>();
    public Task<(int Width, int Height)?> ReadImageSizeAsync(string path, CancellationToken cancellationToken) => Task.FromResult<(int, int)?>(null);
    public Task<Bitmap> DecodeImageAsync(string path, int maxWidth, int maxHeight, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<Bitmap?> ThumbnailAsync(string path, int size, bool thumbnailOnly, CancellationToken cancellationToken) => Task.FromResult<Bitmap?>(null);
    public Bitmap? SmallIcon(string name, bool directory) => null;
    public Task<IQuickLookPdf> OpenPdfAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
    // Extensions with a fake system preview handler; FailHandler makes it fail after it is shown.
    public HashSet<string> Handlers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FailHandler { get; set; }
    public FakeHostedPreview? Hosted { get; private set; }
    public bool HasPreviewHandler(string extension) => Handlers.Contains(extension);
    public Task<IQuickLookHostedPreview?> OpenPreviewHandlerAsync(string path, CancellationToken cancellationToken)
    {
        if (!Handlers.Contains(Path.GetExtension(path))) return Task.FromResult<IQuickLookHostedPreview?>(null);
        Hosted = new FakeHostedPreview(path, FailHandler);
        return Task.FromResult<IQuickLookHostedPreview?>(Hosted);
    }
    public string TypeName(string path, bool directory) => directory ? "文件夹" : "文本文档";
    public string? DefaultAppName(string path) => "记事本";
    public void Open(string path, nint owner) => Opened.Add(path);
    public Task<(bool Deleted, string? Error)> DeleteAsync(string path, nint owner)
    {
        Deleted.Add(path); File.Delete(path);
        return Task.FromResult<(bool, string?)>((true, null));
    }
    public void Dispose() => Activated = null;
}

internal sealed class FakeHostedPreview : IQuickLookHostedPreview
{
    private readonly bool _fail;
    public FakeHostedPreview(string path, bool fail)
    {
        _fail = fail;
        var text = new Avalonia.Controls.TextBlock { Text = "handler:" + Path.GetFileName(path) };
        View = text;
        if (fail) text.AttachedToVisualTree += (_, _) => Failed?.Invoke();
    }
    public Avalonia.Controls.Control View { get; }
    public bool Disposed { get; private set; }
    public event Action? Failed;
    public void Dispose() => Disposed = true;
}
