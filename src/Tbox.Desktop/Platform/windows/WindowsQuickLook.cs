#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Tbox.Core;

namespace Tbox.Desktop.Platform;

// Explorer, desktop and shell services for 速览. Shell COM runs on one STA worker so the UI thread never waits on Explorer.
internal sealed class WindowsQuickLook : IQuickLookSource
{
    private readonly StaWorker _shell = new("Tbox QuickLook shell");
    private readonly Dictionary<string, Bitmap?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private QuickLookActivation? _activation;
    private bool _paused;
    private nint _preview;
    private IReadOnlySet<string>? _imageExtensions;

    public void Start(bool spaceToActivate, Shortcut shortcut, Action<nint> activated)
    {
        _activation ??= new QuickLookActivation { Paused = _paused, PreviewWindow = _preview };
        _activation.Configure(spaceToActivate, shortcut, activated);
    }

    public void Stop() { _activation?.Dispose(); _activation = null; }
    public bool Paused { set { _paused = value; if (_activation is not null) _activation.Paused = value; } }
    public nint PreviewWindow { set { _preview = value; if (_activation is not null) _activation.PreviewWindow = value; } }

    public Task<QuickLookSelection?> ReadSelectionAsync(nint window, CancellationToken cancellationToken) =>
        _shell.Run(() => QuickLookWindows.ReadSelection(window), cancellationToken);
    public Task<IReadOnlyList<string>> ReadFolderAsync(nint window, CancellationToken cancellationToken) =>
        _shell.Run(() => QuickLookWindows.ReadFolder(window), cancellationToken);
    public QuickLookMonitor MonitorOf(nint window) => QuickLookWindows.MonitorOf(window);
    public void BringToForeground(nint window) => QuickLookWindows.BringToForeground(window);

    public IReadOnlySet<string> ImageExtensions => _imageExtensions ??= ReadImageExtensions();

    private static IReadOnlySet<string> ReadImageExtensions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var codec in Windows.Graphics.Imaging.BitmapDecoder.GetDecoderInformationEnumerator())
                foreach (var extension in codec.FileExtensions) set.Add(extension);
        }
        catch (Exception error) { AppLog.Write("QuickLook codecs: " + error.GetType().Name); }
        // Peek adds QOI; Tbox has no QOI decoder, so it is left to the shell thumbnail.
        return set;
    }

    public async Task<(int Width, int Height)?> ReadImageSizeAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await Windows.Storage.Streams.FileRandomAccessStream.OpenAsync(path, Windows.Storage.FileAccessMode.Read).AsTask(cancellationToken);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    public async Task<Bitmap> DecodeImageAsync(string path, int maxWidth, int maxHeight, CancellationToken cancellationToken)
    {
        using var stream = await Windows.Storage.Streams.FileRandomAccessStream.OpenAsync(path, Windows.Storage.FileAccessMode.Read).AsTask(cancellationToken);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        var ratio = Math.Min(1, Math.Min(maxWidth / (double)Math.Max(1, decoder.OrientedPixelWidth), maxHeight / (double)Math.Max(1, decoder.OrientedPixelHeight)));
        // The same ratio on both raw sides, so it does not matter whether the transform scales before or after orientation.
        var transform = new Windows.Graphics.Imaging.BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * ratio)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * ratio)),
            InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
        };
        using var software = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, transform, Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
        var width = software.PixelWidth; var height = software.PixelHeight;
        var pixels = new byte[checked(width * height * 4)];
        software.CopyToBuffer(pixels.AsBuffer());
        return QuickLookWindows.ToBitmap(pixels, width, height, AlphaFormat.Premul);
    }

    public Task<Bitmap?> ThumbnailAsync(string path, int size, bool thumbnailOnly, CancellationToken cancellationToken) =>
        _shell.Run(() => QuickLookWindows.Thumbnail(path, size, thumbnailOnly), cancellationToken);

    public Bitmap? SmallIcon(string name, bool directory)
    {
        var key = directory ? "\\" : Path.GetExtension(name);
        if (_icons.TryGetValue(key, out var cached)) return cached;
        return _icons[key] = QuickLookWindows.SmallIcon(name, directory);
    }

    public async Task<IQuickLookPdf> OpenPdfAsync(string path, CancellationToken cancellationToken)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken);
        var document = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken);
        return new WindowsPdf(document);
    }

    public string TypeName(string path, bool directory) => QuickLookWindows.TypeName(path, directory);
    public string? DefaultAppName(string path) => QuickLookWindows.DefaultAppName(path);
    public void Open(string path, nint owner) => QuickLookWindows.Open(path, owner);
    public Task<(bool Deleted, string? Error)> DeleteAsync(string path, nint owner) =>
        _shell.Run(() => QuickLookWindows.Delete(path, owner), CancellationToken.None);

    public void Dispose()
    {
        Stop();
        _shell.Dispose();
        foreach (var icon in _icons.Values) icon?.Dispose();
        _icons.Clear();
    }

    private sealed class WindowsPdf(Windows.Data.Pdf.PdfDocument document) : IQuickLookPdf
    {
        private readonly SemaphoreSlim _render = new(1, 1);
        public int PageCount => (int)document.PageCount;
        public (double Width, double Height) PageSize(int index)
        {
            using var page = document.GetPage((uint)index);
            return (page.Size.Width, page.Size.Height);
        }

        public async Task<Bitmap> RenderAsync(int index, int pixelWidth, CancellationToken cancellationToken)
        {
            await _render.WaitAsync(cancellationToken);
            try
            {
                using var page = document.GetPage((uint)index);
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var options = new Windows.Data.Pdf.PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(1, pixelWidth),
                    BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId
                };
                await page.RenderToStreamAsync(stream, options).AsTask(cancellationToken);
                stream.Seek(0);
                using var managed = stream.AsStreamForRead();
                return new Bitmap(managed);
            }
            finally { _render.Release(); }
        }

        public void Dispose() => _render.Dispose();
    }
}

// One background STA thread with a message loop for shell COM objects.
internal sealed class StaWorker : IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaWorker(string name)
    {
        _thread = new Thread(() => { foreach (var work in _queue.GetConsumingEnumerable()) work(); }) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_queue.IsAddingCompleted) { result.SetCanceled(); return result.Task; }
        _queue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested) { result.TrySetCanceled(cancellationToken); return; }
            try { result.TrySetResult(work()); }
            catch (Exception error) { result.TrySetException(error); }
        });
        return result.Task;
    }

    public void Dispose() { _queue.CompleteAdding(); _thread.Join(1000); }
}
#endif
