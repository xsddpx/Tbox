using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Tbox.Desktop.Platform;

public sealed record CapturedDisplay(uint Id, double Left, double Top, double Width, double Height, byte[] Png)
{
    private byte[] _png = Png;
    internal byte[]? BgraPixels { get; init; }
    internal int PixelWidth { get; init; }
    internal int PixelHeight { get; init; }
    // Compatibility for diagnostics/export. The interactive capture path never needs PNG encoding.
    public byte[] Png
    {
        get
        {
            if (_png.Length != 0 || BgraPixels is null) return _png;
            using var picture = CreatePicture();
            return _png = picture.Png;
        }
        init => _png = value;
    }
    internal long HistoryBytes => BgraPixels?.LongLength ?? _png.LongLength;
    // PNG captures (macOS) do not set PixelWidth/Height; read their IHDR without decoding.
    internal (int Width, int Height) HistoryPixelSize
    {
        get
        {
            if (PixelWidth > 0 && PixelHeight > 0) return (PixelWidth, PixelHeight);
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (_png.Length < 24 || !_png.AsSpan(0, 8).SequenceEqual(signature)) return (0, 0);
            return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(_png.AsSpan(16, 4)),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(_png.AsSpan(20, 4)));
        }
    }
    internal Models.Picture CreatePicture() => BgraPixels is { } pixels
        ? Models.Picture.FromPixels(pixels, PixelWidth, PixelHeight) : new Models.Picture(Png);
}
public sealed record DesktopPlacement(double Left, double Top, double Width, double Height);
public sealed record DesktopPoint(double X, double Y);
public sealed record RecognizedLine(string Text, double Left, double Top, double Width, double Height);
public sealed record RecognitionResult(string Text, IReadOnlyList<RecognizedLine> Lines);

// Top-left desktop units: macOS points, Windows physical pixels. Image pixels remain separate.
// Platform code owns permission prompts and all interaction with the operating system.
public interface IPlatformServices : IDisposable
{
    bool HasScreenPermission { get; }
    bool RequestScreenPermission();
    void OpenScreenPermissionSettings();
    Task<IReadOnlyList<CapturedDisplay>> CaptureAsync(CancellationToken cancellationToken);
    Task<byte[]> CaptureRegionAsync(DesktopPlacement region, CancellationToken cancellationToken);
    Task<RecognitionResult> RecognizeAsync(byte[] png, CancellationToken cancellationToken);
    // Global hotkeys, one per action. Registering an action replaces its previous shortcut;
    // false means the system or another application already holds the combination.
    bool RegisterHotkey(Tbox.Core.HotkeyBinding binding, Action<Tbox.Core.HotkeyAction> onPressed);
    void UnregisterHotkey(Tbox.Core.HotkeyAction action);
    void PlaceWindow(nint handle, DesktopPlacement placement, bool overlay);
    void MoveToPointer(nint handle);
    DesktopPlacement GetWindowPlacement(nint handle);
    DesktopPlacement GetWorkArea(nint handle);
    double DesktopUnitsPerDip(nint handle);
    double GetDisplayTopSafeInset(uint displayId) => 0;
    DesktopPoint GetPointerPosition();
    ICaptureTargetSource CreateCaptureTargets();
    void PrepareCaptureOverlay(nint handle, bool clickThrough);
    IRegionRecorder CreateRecorder(RecordingArea area, bool systemAudio, int framesPerSecond);
    IFolderNavigation CreateFolderNavigation();
    IStartupRegistration CreateStartupRegistration();

    async Task<string?> PickFolderAsync(Window owner, string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    // Publishes clipboard image formats that the platform's own applications read. Returning false keeps the
    // shared Avalonia clipboard path. bgra holds top-down, non-premultiplied pixels matching png.
    bool TryWriteClipboardImage(byte[] png, byte[] bgra, int width, int height) => false;
}

public interface ICaptureTargetSource
{
    bool EnableElementDetection();
    Tbox.Core.CaptureTargetBounds? FindTarget(Tbox.Core.CaptureDetectionMode mode, DesktopPoint point);
}
