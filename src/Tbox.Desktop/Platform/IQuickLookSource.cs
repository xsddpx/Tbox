using Avalonia.Media.Imaging;

namespace Tbox.Desktop.Platform;

// What the user had selected when 速览 was activated. Paths are file-system paths in the order the view shows them.
public sealed record QuickLookSelection(IReadOnlyList<string> Items, bool Multiple);

public sealed record QuickLookMonitor(DesktopPlacement Bounds, double Scale);

public interface IQuickLookPdf : IDisposable
{
    int PageCount { get; }
    // Page size in points (1/96 inch).
    (double Width, double Height) PageSize(int index);
    Task<Bitmap> RenderAsync(int index, int pixelWidth, CancellationToken cancellationToken);
}

// A system preview handler drawing into the preview (Peek's ShellPreviewHandlerControl). Failed fires when the
// handler cannot show the file after it was attached, so the window can fall back to another preview.
public interface IQuickLookHostedPreview : IDisposable
{
    Avalonia.Controls.Control View { get; }
    event Action? Failed;
}

// The operating-system half of 速览: activation from the file manager, reading its selection and file services.
// Activation callbacks arrive on a background thread with the foreground window that received the key.
public interface IQuickLookSource : IDisposable
{
    // spaceToActivate: a bare Space; otherwise shortcut. Either way only while the file manager, the desktop or
    // the preview window is in front and no text field is being edited.
    void Start(bool spaceToActivate, Tbox.Core.Shortcut shortcut, Action<nint> activated);
    void Stop();
    // Recording a shortcut in the main window must receive the combination.
    bool Paused { set; }
    nint PreviewWindow { set; }
    Task<QuickLookSelection?> ReadSelectionAsync(nint window, CancellationToken cancellationToken);
    // Every item in the folder view of the window, in view order.
    Task<IReadOnlyList<string>> ReadFolderAsync(nint window, CancellationToken cancellationToken);
    QuickLookMonitor MonitorOf(nint window);
    void BringToForeground(nint window);

    IReadOnlySet<string> ImageExtensions { get; }
    Task<(int Width, int Height)?> ReadImageSizeAsync(string path, CancellationToken cancellationToken);
    // Decodes with the system codecs, honouring EXIF orientation, scaled down to fit maxWidth × maxHeight pixels.
    Task<Bitmap> DecodeImageAsync(string path, int maxWidth, int maxHeight, CancellationToken cancellationToken);
    // The shell thumbnail, or the file's icon when thumbnailOnly is false.
    Task<Bitmap?> ThumbnailAsync(string path, int size, bool thumbnailOnly, CancellationToken cancellationToken);
    // A small icon for a name that need not exist, e.g. an archive entry.
    Bitmap? SmallIcon(string name, bool directory);
    Task<IQuickLookPdf> OpenPdfAsync(string path, CancellationToken cancellationToken);
    // True when a system preview handler is registered for the extension (Office, Outlook, fonts, RTF ...).
    bool HasPreviewHandler(string extension) => false;
    // Starts the handler for the file; null when none is registered.
    Task<IQuickLookHostedPreview?> OpenPreviewHandlerAsync(string path, CancellationToken cancellationToken) => Task.FromResult<IQuickLookHostedPreview?>(null);
    // The system's legacy code page (GBK on Chinese Windows) for text files that are neither UTF-8 nor UTF-16.
    System.Text.Encoding? AnsiEncoding => null;
    string TypeName(string path, bool directory);
    string? DefaultAppName(string path);
    void Open(string path, nint owner);
    // Moves the item to the Recycle Bin. Returns the reason on failure; null on success, false when the user cancelled.
    Task<(bool Deleted, string? Error)> DeleteAsync(string path, nint owner);
}
