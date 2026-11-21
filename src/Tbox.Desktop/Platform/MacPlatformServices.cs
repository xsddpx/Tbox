using System.Runtime.InteropServices;
using System.Text.Json;
using Tbox.Core;

namespace Tbox.Desktop.Platform;

public sealed class MacPlatformServices : IPlatformServices
{
    private const string Library = "TboxNative";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private HotkeyCallback? _callback;
    private ScreenshotShortcutCallback? _screenshotShortcutCallback;
    private readonly SemaphoreSlim _recognitionLock = new(1, 1);

    public bool HasScreenPermission => NativePermission() != 0;
    public bool RequestScreenPermission() => NativeRequestPermission() != 0;
    public void OpenScreenPermissionSettings() => NativeOpenPermissionSettings();

    public Task<IReadOnlyList<CapturedDisplay>> CaptureAsync(CancellationToken cancellationToken) =>
        Task.Run(() => CapturePixels(null, cancellationToken), cancellationToken);

    private static IReadOnlyList<CapturedDisplay> CapturePixels(DesktopPlacement? region, CancellationToken token)
    {
        var frames = new List<CapturedDisplay>();
        Exception? readError = null;
        int Receive(uint id, double left, double top, double width, double height, int pixelWidth, int pixelHeight, nint data)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var pixels = GC.AllocateUninitializedArray<byte>(checked(pixelWidth * pixelHeight * 4));
                Marshal.Copy(data, pixels, 0, pixels.Length);
                frames.Add(new CapturedDisplay(id, left, top, width, height, [])
                    { PixelWidth = pixelWidth, PixelHeight = pixelHeight, BgraPixels = pixels });
                return 1;
            }
            catch (Exception ex) { readError = ex; return 0; }
        }

        var error = NativeCapturePixels(region is null ? 0 : 1, region?.Left ?? 0, region?.Top ?? 0,
            region?.Width ?? 0, region?.Height ?? 0, Receive);
        try
        {
            token.ThrowIfCancellationRequested();
            if (readError is not null) throw new InvalidOperationException("无法读取截图。", readError);
            if (error != 0) throw new InvalidOperationException(Marshal.PtrToStringUTF8(error));
            return frames;
        }
        finally { if (error != 0) NativeFree(error); }
    }

    public Task<RecognitionResult> RecognizeAsync(byte[] png, CancellationToken cancellationToken) =>
        RecognizeAsync(() => NativeRecognize(png, png.Length), cancellationToken);

    public Task<RecognitionResult> RecognizeAsync(Models.Picture picture, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OpaqueOcrPixels(picture) is not { } pixels) return RecognizeAsync(picture.Png, cancellationToken);
        var size = picture.Bitmap.PixelSize;
        return RecognizeAsync(() => NativeRecognizePixels(pixels, pixels.Length, size.Width, size.Height), cancellationToken);
    }

    internal static byte[]? OpaqueOcrPixels(Models.Picture picture)
    {
        // Preserve original encoded data (including imported formats/metadata) and transparent-image handling.
        if (picture.HasEncodedPng) return null;
        // Keep this full-size buffer only until the native request completes.
        var pixels = picture.CopyPixels();
        for (var alpha = 3; alpha < pixels.Length; alpha += 4)
            if (pixels[alpha] != 255) return null;
        return pixels;
    }

    private async Task<RecognitionResult> RecognizeAsync(Func<nint> recognize, CancellationToken cancellationToken)
    {
        // Serialise native Vision work; cancelling discards results, never publishes an older request.
        await _recognitionLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Task.Run(() => ReadJson<RecognitionResult>(recognize()), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally { _recognitionLock.Release(); }
    }

    public Task<CapturedPixels> CaptureRegionAsync(DesktopPlacement region, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var frame = CapturePixels(region, cancellationToken).Single();
        return new CapturedPixels(frame.BgraPixels!, frame.PixelWidth, frame.PixelHeight);
    }, cancellationToken);

    public bool RegisterHotkey(HotkeyBinding binding, Action<HotkeyAction> onPressed)
    {
        _callback ??= id => onPressed((HotkeyAction)id);
        var shortcut = binding.Shortcut;
        if (ShortcutPolicy.MacKeyCode(shortcut.Key) is not { } key) return false;
        // Carbon modifier flags: cmdKey 0x100, shiftKey 0x200, optionKey 0x800, controlKey 0x1000.
        var modifiers = (shortcut.Meta ? 0x100 : 0) | (shortcut.Shift ? 0x200 : 0) | (shortcut.Alt ? 0x800 : 0) | (shortcut.Control ? 0x1000 : 0);
        return NativeRegisterHotkey(_callback, (int)binding.Action, key, modifiers) == 0;
    }
    public void UnregisterHotkey(HotkeyAction action) => NativeUnregisterHotkey((int)action);

    public bool SupportsSystemScreenshotShortcutTakeover => true;
    public bool IsSystemScreenshotShortcutTakeoverActive => NativeScreenshotShortcutActive() != 0;

    public bool TryStartSystemScreenshotShortcutTakeover(Action onPressed, bool requestPermission)
    {
        _screenshotShortcutCallback ??= () => onPressed();
        if (NativeStartScreenshotShortcut(_screenshotShortcutCallback, requestPermission ? 1 : 0) != 0) return true;
        _screenshotShortcutCallback = null;
        return false;
    }

    public void StopSystemScreenshotShortcutTakeover()
    {
        NativeStopScreenshotShortcut();
        _screenshotShortcutCallback = null;
    }

    public void PlaceWindow(nint handle, DesktopPlacement p, bool overlay) =>
        NativePlaceWindow(handle, p.Left, p.Top, p.Width, p.Height, overlay ? 1 : 0);
    public void MoveToPointer(nint handle) => NativeMoveToPointer(handle);
    public DesktopPlacement GetWindowPlacement(nint handle) => ReadJson<DesktopPlacement>(NativeWindowPlacement(handle));
    public DesktopPlacement GetWorkArea(nint handle) => ReadJson<DesktopPlacement>(NativeWorkArea(handle));
    public DesktopPlacement GetWorkAreaFor(DesktopPlacement region) =>
        ReadJson<DesktopPlacement>(NativeWorkAreaFor(region.Left, region.Top, region.Width, region.Height));
    public double DesktopUnitsPerDip(nint handle) => 1;
    public double GetDisplayTopSafeInset(uint displayId) => NativeDisplayTopSafeInset(displayId);
    public DesktopPoint GetPointerPosition() => ReadJson<DesktopPoint>(NativePointerPosition());
    public ICaptureTargetSource CreateCaptureTargets() => new MacCaptureTargets();
    public void PrepareCaptureOverlay(nint handle, bool clickThrough) => NativeCaptureOverlay(handle, clickThrough ? 1 : 0);
    public IRegionRecorder CreateRecorder(RecordingArea area, bool systemAudio, bool cursor, int framesPerSecond) => new MacRegionRecorder(area, systemAudio, cursor, framesPerSecond);
    public IFolderNavigation CreateFolderNavigation() => new MacFolderNavigation();
    public IWindowTopmost CreateWindowTopmost() => new MacWindowTopmost(this);
    public IStartupRegistration CreateStartupRegistration() => new MacStartupRegistration();
    public IKeepAwake CreateKeepAwake(string stateDirectory) => new MacKeepAwake(stateDirectory);

    public void Dispose()
    {
        StopSystemScreenshotShortcutTakeover();
        NativeUnregisterHotkeys();
        _callback = null;
    }

    internal static T ReadJson<T>(nint pointer)
    {
        if (pointer == 0) throw new InvalidOperationException("系统服务没有返回结果。");
        try
        {
            var text = Marshal.PtrToStringUTF8(pointer) ?? throw new InvalidOperationException("系统服务返回了空结果。");
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.GetString());
            return JsonSerializer.Deserialize<T>(text, JsonOptions) ?? throw new InvalidOperationException("系统服务结果无效。");
        }
        finally { NativeFree(pointer); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CaptureFrameCallback(uint id, double left, double top, double width, double height,
        int pixelWidth, int pixelHeight, nint pixels);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HotkeyCallback(int id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ScreenshotShortcutCallback();
    [DllImport(Library, EntryPoint = "tbox_start_screenshot_shortcut")] private static extern int NativeStartScreenshotShortcut(ScreenshotShortcutCallback callback, int requestPermission);
    [DllImport(Library, EntryPoint = "tbox_stop_screenshot_shortcut")] private static extern void NativeStopScreenshotShortcut();
    [DllImport(Library, EntryPoint = "tbox_screenshot_shortcut_active")] private static extern int NativeScreenshotShortcutActive();
    [DllImport(Library, EntryPoint = "tbox_permission")] private static extern int NativePermission();
    [DllImport(Library, EntryPoint = "tbox_request_permission")] private static extern int NativeRequestPermission();
    [DllImport(Library, EntryPoint = "tbox_open_permission_settings")] private static extern void NativeOpenPermissionSettings();
    [DllImport(Library, EntryPoint = "tbox_capture_pixels")] private static extern nint NativeCapturePixels(
        int regionOnly, double left, double top, double width, double height, CaptureFrameCallback receive);
    [DllImport(Library, EntryPoint = "tbox_capture_overlay")] private static extern void NativeCaptureOverlay(nint handle, int clickThrough);
    [DllImport(Library, EntryPoint = "tbox_recognize_pixels")] private static extern nint NativeRecognizePixels(byte[] pixels, int length, int width, int height);
    [DllImport(Library, EntryPoint = "tbox_recognize")] private static extern nint NativeRecognize(byte[] data, int length);
    [DllImport(Library, EntryPoint = "tbox_register_hotkey")] private static extern int NativeRegisterHotkey(HotkeyCallback callback, int id, int keyCode, int modifiers);
    [DllImport(Library, EntryPoint = "tbox_unregister_hotkey")] private static extern void NativeUnregisterHotkey(int id);
    [DllImport(Library, EntryPoint = "tbox_unregister_hotkeys")] private static extern void NativeUnregisterHotkeys();
    [DllImport(Library, EntryPoint = "tbox_place_window")] private static extern void NativePlaceWindow(nint handle, double left, double top, double width, double height, int overlay);
    [DllImport(Library, EntryPoint = "tbox_move_to_pointer")] private static extern void NativeMoveToPointer(nint handle);
    [DllImport(Library, EntryPoint = "tbox_window_placement")] private static extern nint NativeWindowPlacement(nint handle);
    [DllImport(Library, EntryPoint = "tbox_work_area")] private static extern nint NativeWorkArea(nint handle);
    [DllImport(Library, EntryPoint = "tbox_work_area_for")] private static extern nint NativeWorkAreaFor(double left, double top, double width, double height);
    [DllImport(Library, EntryPoint = "tbox_display_top_safe_inset")] private static extern double NativeDisplayTopSafeInset(uint displayId);
    [DllImport(Library, EntryPoint = "tbox_pointer_position")] private static extern nint NativePointerPosition();
    [DllImport(Library, EntryPoint = "tbox_free")] private static extern void NativeFree(nint pointer);
}
