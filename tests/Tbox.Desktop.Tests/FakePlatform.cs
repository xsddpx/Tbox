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
    public Task<IReadOnlyList<CapturedDisplay>>? PendingCapture { get; set; }
    public Action? CaptureStarting { get; set; }
    public RecognitionResult? OcrResult { get; set; }
    public bool RequestScreenPermission() { PermissionRequests++; return HasScreenPermission; }
    public void OpenScreenPermissionSettings() { PermissionSettingsOpens++; }
    public Task<IReadOnlyList<CapturedDisplay>> CaptureAsync(CancellationToken cancellationToken)
    { CaptureRequests++; CaptureStarting?.Invoke(); return PendingCapture ?? Task.FromResult(Displays); }
    public Task<byte[]> CaptureRegionAsync(DesktopPlacement region, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<RecognitionResult> RecognizeAsync(byte[] png, CancellationToken cancellationToken) =>
        Task.FromResult(OcrResult ?? throw new NotSupportedException());
    // Shortcuts listed here behave as if another application holds them.
    public HashSet<Shortcut> TakenShortcuts { get; } = [];
    public Dictionary<HotkeyAction, Shortcut> Hotkeys { get; } = [];
    public Action<HotkeyAction>? HotkeyHandler { get; private set; }
    public bool RegisterHotkey(HotkeyBinding binding, Action<HotkeyAction> onPressed)
    {
        HotkeyHandler = onPressed; Hotkeys.Remove(binding.Action);
        if (TakenShortcuts.Contains(binding.Shortcut)) return false;
        Hotkeys[binding.Action] = binding.Shortcut; return true;
    }
    public void UnregisterHotkey(HotkeyAction action) => Hotkeys.Remove(action);
    public void PlaceWindow(nint handle, DesktopPlacement placement, bool overlay) { }
    public void MoveToPointer(nint handle) { }
    public DesktopPlacement GetWindowPlacement(nint handle) => new(0, 0, 800, 600);
    public DesktopPlacement GetWorkArea(nint handle) => new(0, 0, 1600, 1000);
    public double DesktopUnitsPerDip(nint handle) => 1;
    public double DisplayTopSafeInset { get; set; }
    public double GetDisplayTopSafeInset(uint displayId) => DisplayTopSafeInset;
    public DesktopPoint GetPointerPosition() => new(100, 100);
    public ICaptureTargetSource CreateCaptureTargets() => new EmptyTargets();
    public void PrepareCaptureOverlay(nint handle, bool clickThrough) { }
    public IRegionRecorder? Recorder { get; set; }
    public IRegionRecorder CreateRecorder(RecordingArea area, bool systemAudio, int framesPerSecond) => Recorder ?? throw new NotSupportedException();
    public FolderNavigationResult FolderJumpResult { get; set; } = new(true);
    public IFolderNavigation CreateFolderNavigation() => new FakeFolders(this);
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
    private sealed class FakeFolders(FakePlatform platform) : IFolderNavigation
    {
        public string CurrentDirectory => "";
        public bool HasPermission => true;
        public bool RequestPermission() => true;
        public void Start() { }
        public void Stop() { }
        public Task<FolderNavigationResult> JumpAsync(Shortcut shortcut, CancellationToken cancellationToken) => Task.FromResult(platform.FolderJumpResult);
        public Task<FolderDialogTarget?> FindDialogAsync(long? retainedTarget, nint activeBar, CancellationToken cancellationToken) => Task.FromResult<FolderDialogTarget?>(null);
        public Task<IReadOnlyList<string>> GetOpenFoldersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> SearchFoldersAsync(string query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> RestoreDialogAsync(long target, nint activeBar, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<FolderNavigationResult> JumpToAsync(long target, nint activeBar, string path, Shortcut? shortcut,
            CancellationToken cancellationToken) => Task.FromResult(platform.FolderJumpResult);
        public void Dispose() { }
    }
}

// A recorder that only tracks calls, for toolbar state tests.
internal sealed class FakeRecorder : IRegionRecorder
{
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    public Task StartAsync(string path, CancellationToken cancellationToken) { Started = true; return Task.CompletedTask; }
    public void Pause() { }
    public void Resume() { }
    public Task<RecordingOutput> StopAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public void Dispose() => Disposed = true;
}
