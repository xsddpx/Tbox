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
    public Func<CancellationToken, Task<byte[]>>? CaptureRegion { get; set; }
    public async Task<CapturedPixels> CaptureRegionAsync(DesktopPlacement region, CancellationToken cancellationToken)
    {
        using var frame = new Models.Picture(await (CaptureRegion?.Invoke(cancellationToken) ?? throw new NotSupportedException()));
        return new CapturedPixels(frame.GetPixels(), frame.Bitmap.PixelSize.Width, frame.Bitmap.PixelSize.Height);
    }
    public Func<Models.Picture, CancellationToken, Task<RecognitionResult>>? OcrPicture { get; init; }
    public Task<RecognitionResult> RecognizeAsync(Models.Picture picture, CancellationToken cancellationToken) =>
        OcrPicture?.Invoke(picture, cancellationToken) ?? RecognizeAsync(picture.Png, cancellationToken);
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
    public bool SupportsSystemScreenshotShortcutTakeover { get; set; }
    public bool CanStartSystemScreenshotShortcutTakeover { get; set; }
    public Action? SystemScreenshotShortcutHandler { get; private set; }
    public List<bool> SystemScreenshotShortcutAttempts { get; } = [];
    public bool IsSystemScreenshotShortcutTakeoverActive => SystemScreenshotShortcutHandler is not null;
    public bool TryStartSystemScreenshotShortcutTakeover(Action onPressed, bool requestPermission)
    {
        SystemScreenshotShortcutAttempts.Add(requestPermission);
        if (!CanStartSystemScreenshotShortcutTakeover) return false;
        SystemScreenshotShortcutHandler = onPressed;
        return true;
    }
    public void StopSystemScreenshotShortcutTakeover() => SystemScreenshotShortcutHandler = null;
    public Action<DesktopPlacement, bool>? WindowPlaced { get; set; }
    public DesktopPlacement? WindowPlacementOverride { get; set; }
    // Optional second display to the right of the primary one; window handles are not tracked, so only regions can reach it.
    public DesktopPlacement? SecondaryWorkArea { get; set; }
    public void PlaceWindow(nint handle, DesktopPlacement placement, bool overlay) => WindowPlaced?.Invoke(placement, false);
    public void MoveWindow(nint handle, DesktopPlacement placement) => WindowPlaced?.Invoke(placement, true);
    public void MoveToPointer(nint handle) { }
    public DesktopPlacement GetWindowPlacement(nint handle) => WindowPlacementOverride ?? new(0, 0, 800, 600);
    public DesktopPlacement GetWorkArea(nint handle) => new(0, 0, 1600, 1000);
    public DesktopPlacement GetWorkAreaFor(DesktopPlacement region) =>
        SecondaryWorkArea is { } second && region.Left + region.Width / 2 >= second.Left ? second : new(0, 0, 1600, 1000);
    public double DesktopUnitsPerDip(nint handle) => 1;
    public double DisplayTopSafeInset { get; set; }
    public double GetDisplayTopSafeInset(uint displayId) => DisplayTopSafeInset;
    public DesktopPoint GetPointerPosition() => new(100, 100);
    public ICaptureTargetSource CreateCaptureTargets() => new EmptyTargets();
    public void PrepareCaptureOverlay(nint handle, bool clickThrough) { }
    public IRegionRecorder? Recorder { get; set; }
    public bool? LastRecorderCursor { get; private set; }
    public IRegionRecorder CreateRecorder(RecordingArea area, bool systemAudio, bool cursor, int framesPerSecond)
    { LastRecorderCursor = cursor; return Recorder ?? throw new NotSupportedException(); }
    public FolderNavigationResult FolderJumpResult { get; set; } = new(true);
    public IFolderNavigation CreateFolderNavigation() => new FakeFolders(this);
    public IStartupRegistration StartupRegistration { get; set; } = new EmptyStartup();
    public IStartupRegistration CreateStartupRegistration() => StartupRegistration;
    public FakeQuickLook QuickLook { get; } = new();
    public IQuickLookSource CreateQuickLook() => QuickLook;
    public FakeKeepAwake KeepAwake { get; set; } = new();
    public IKeepAwake CreateKeepAwake(string stateDirectory) => KeepAwake;
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

// Records keep-awake requests and simulates refusals and the macOS password prompt.
internal sealed class FakeKeepAwake : IKeepAwake
{
    public bool HasLid { get; set; } = true;
    public bool LidNeedsAuthorization { get; set; }
    public bool RefuseSystem { get; set; }
    // Simulates cancelling the macOS password prompt.
    public bool RefuseLid { get; set; }
    // Lets a test observe the state while a password prompt is open.
    public TaskCompletionSource? Authorization { get; set; }
    public bool RequiresLidApproval { get; set; }
    public int ApprovalRequests { get; private set; }

    public string? RequestLidApproval()
    {
        ApprovalRequests++;
        return null;
    }
    public List<KeepAwakeRequest> Requests { get; } = [];
    public KeepAwakeRequest Applied { get; private set; } = KeepAwakeRequest.None;
    public bool Disposed { get; private set; }

    public async Task<KeepAwakeResult> ApplyAsync(KeepAwakeRequest request)
    {
        Requests.Add(request);
        if (request.Lid != Applied.Lid && Authorization is { } pending) await pending.Task;
        if (request.System && RefuseSystem) return new KeepAwakeResult(Applied = KeepAwakeRequest.None, "系统拒绝保持唤醒。");
        if (request.Lid && RequiresLidApproval)
            return new KeepAwakeResult(Applied = request with { Lid = false }, RequiresLidApproval: true);
        var lid = request.Lid && !RefuseLid;
        Applied = request with { Lid = lid };
        return new KeepAwakeResult(Applied, request.Lid && !lid ? "已取消授权，合上盖子时仍会睡眠。" : null);
    }

    public void Dispose() { Disposed = true; Applied = KeepAwakeRequest.None; }
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
