namespace Tbox.Desktop.Platform;

public sealed record CapturedDisplay(uint Id, double Left, double Top, double Width, double Height, byte[] Png);
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
    bool RegisterHotkeys(Action<int> onHotkey, int mask = 7);
    void PlaceWindow(nint handle, DesktopPlacement placement, bool overlay);
    void MoveToPointer(nint handle);
    DesktopPlacement GetWindowPlacement(nint handle);
    DesktopPlacement GetWorkArea(nint handle);
    double DesktopUnitsPerDip(nint handle);
    DesktopPoint GetPointerPosition();
    ICaptureTargetSource CreateCaptureTargets();
    void PrepareCaptureOverlay(nint handle, bool clickThrough);
    IRegionRecorder CreateRecorder(RecordingArea area, bool systemAudio, int framesPerSecond);
    IFolderNavigation CreateFolderNavigation();
}

public interface ICaptureTargetSource
{
    bool EnableElementDetection();
    Tbox.Core.CaptureTargetBounds? FindTarget(Tbox.Core.CaptureDetectionMode mode, DesktopPoint point);
}
