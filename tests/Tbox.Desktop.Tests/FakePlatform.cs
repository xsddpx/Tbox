using Tbox.Core;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Tests;

internal sealed class FakePlatform : IPlatformServices
{
    public bool HasScreenPermission { get; set; }
    public int PermissionRequests { get; private set; }
    public int CaptureRequests { get; private set; }
    public IReadOnlyList<CapturedDisplay> Displays { get; set; } = [];
    public bool RequestScreenPermission() { PermissionRequests++; return HasScreenPermission; }
    public Task<IReadOnlyList<CapturedDisplay>> CaptureAsync(CancellationToken cancellationToken)
    { CaptureRequests++; return Task.FromResult(Displays); }
    public Task<byte[]> CaptureRegionAsync(DesktopPlacement region, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<RecognitionResult> RecognizeAsync(byte[] png, CancellationToken cancellationToken) => throw new NotSupportedException();
    public bool RegisterHotkeys(Action<int> onHotkey) => true;
    public void PlaceWindow(nint handle, DesktopPlacement placement, bool overlay) { }
    public void MoveToPointer(nint handle) { }
    public DesktopPlacement GetWindowPlacement(nint handle) => new(0, 0, 800, 600);
    public DesktopPlacement GetWorkArea(nint handle) => new(0, 0, 1600, 1000);
    public double DesktopUnitsPerDip(nint handle) => 1;
    public DesktopPoint GetPointerPosition() => new(100, 100);
    public ICaptureTargetSource CreateCaptureTargets() => new EmptyTargets();
    public void PrepareCaptureOverlay(nint handle, bool clickThrough) { }
    public void Dispose() { }
    private sealed class EmptyTargets : ICaptureTargetSource
    {
        public bool EnableElementDetection() => false;
        public CaptureTargetBounds? FindTarget(CaptureDetectionMode mode, DesktopPoint point) => null;
    }
}
