using Tbox.Core;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Tests;

internal sealed class FakePlatform : IPlatformServices
{
    public bool HasScreenPermission { get; set; }
    public int PermissionRequests { get; private set; }
    public int CaptureRequests { get; private set; }
    public int PermissionSettingsOpens { get; private set; }
    public IReadOnlyList<CapturedDisplay> Displays { get; set; } = [];
    public bool RequestScreenPermission() { PermissionRequests++; return HasScreenPermission; }
    public void OpenScreenPermissionSettings() { PermissionSettingsOpens++; }
    public Task<IReadOnlyList<CapturedDisplay>> CaptureAsync(CancellationToken cancellationToken)
    { CaptureRequests++; return Task.FromResult(Displays); }
    public Task<byte[]> CaptureRegionAsync(DesktopPlacement region, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<RecognitionResult> RecognizeAsync(byte[] png, CancellationToken cancellationToken) => throw new NotSupportedException();
    public bool RegisterHotkeys(Action<int> onHotkey, int mask = 7) => true;
    public void PlaceWindow(nint handle, DesktopPlacement placement, bool overlay) { }
    public void MoveToPointer(nint handle) { }
    public DesktopPlacement GetWindowPlacement(nint handle) => new(0, 0, 800, 600);
    public DesktopPlacement GetWorkArea(nint handle) => new(0, 0, 1600, 1000);
    public double DesktopUnitsPerDip(nint handle) => 1;
    public DesktopPoint GetPointerPosition() => new(100, 100);
    public ICaptureTargetSource CreateCaptureTargets() => new EmptyTargets();
    public void PrepareCaptureOverlay(nint handle, bool clickThrough) { }
    public IRegionRecorder CreateRecorder(RecordingArea area, bool systemAudio, int framesPerSecond) => throw new NotSupportedException();
    public IFolderNavigation CreateFolderNavigation() => new EmptyFolders();
    public IStartupRegistration StartupRegistration { get; set; } = new EmptyStartup();
    public IStartupRegistration CreateStartupRegistration() => StartupRegistration;
    public void Dispose() { }
    private sealed class EmptyStartup : IStartupRegistration
    {
        public Task<StartupState> ReadAsync() => Task.FromResult(new StartupState(false));
        public Task<StartupState> SetEnabledAsync(bool enabled) => Task.FromResult(new StartupState(enabled));
    }
    private sealed class EmptyTargets : ICaptureTargetSource
    {
        public bool EnableElementDetection() => false;
        public CaptureTargetBounds? FindTarget(CaptureDetectionMode mode, DesktopPoint point) => null;
    }
    private sealed class EmptyFolders : IFolderNavigation
    {
        public string CurrentDirectory => "";
        public bool HasPermission => true;
        public bool RequestPermission() => true;
        public void Start() { }
        public void Stop() { }
        public Task<FolderNavigationResult> JumpAsync(CancellationToken cancellationToken) => Task.FromResult(new FolderNavigationResult(true));
        public void Dispose() { }
    }
}
