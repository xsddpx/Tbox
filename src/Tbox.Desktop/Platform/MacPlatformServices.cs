using System.Runtime.InteropServices;
using System.Text.Json;

namespace Tbox.Desktop.Platform;

public sealed class MacPlatformServices : IPlatformServices
{
    private const string Library = "TboxNative";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private HotkeyCallback? _callback;
    private readonly SemaphoreSlim _recognitionLock = new(1, 1);

    public bool HasScreenPermission => NativePermission() != 0;
    public bool RequestScreenPermission() => NativeRequestPermission() != 0;

    public async Task<IReadOnlyList<CapturedDisplay>> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Task.Run(() => ReadJson<CaptureResponse>(NativeCapture()), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Displays.Select(display => new CapturedDisplay(
            display.Id, display.Left, display.Top, display.Width, display.Height,
            Convert.FromBase64String(display.Png))).ToArray();
    }

    public async Task<RecognitionResult> RecognizeAsync(byte[] png, CancellationToken cancellationToken)
    {
        // Serialise native Vision work; cancelling discards results, never publishes an older request.
        await _recognitionLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Task.Run(() => ReadJson<RecognitionResult>(NativeRecognize(png, png.Length)), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally { _recognitionLock.Release(); }
    }

    public bool RegisterHotkeys(Action<int> onHotkey)
    {
        _callback = id => onHotkey(id);
        return NativeRegisterHotkeys(_callback) == 0;
    }

    public void PlaceWindow(nint handle, DesktopPlacement p, bool overlay) =>
        NativePlaceWindow(handle, p.Left, p.Top, p.Width, p.Height, overlay ? 1 : 0);
    public void MoveToPointer(nint handle) => NativeMoveToPointer(handle);
    public DesktopPlacement GetWindowPlacement(nint handle) => ReadJson<DesktopPlacement>(NativeWindowPlacement(handle));
    public DesktopPlacement GetWorkArea(nint handle) => ReadJson<DesktopPlacement>(NativeWorkArea(handle));
    public double DesktopUnitsPerDip(nint handle) => 1;
    public DesktopPoint GetPointerPosition() => ReadJson<DesktopPoint>(NativePointerPosition());
    public ICaptureTargetSource CreateCaptureTargets() => new MacCaptureTargets();

    public void Dispose()
    {
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

    private sealed record NativeDisplay(uint Id, double Left, double Top, double Width, double Height, string Png);
    private sealed record CaptureResponse(NativeDisplay[] Displays);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HotkeyCallback(int id);
    [DllImport(Library, EntryPoint = "tbox_permission")] private static extern int NativePermission();
    [DllImport(Library, EntryPoint = "tbox_request_permission")] private static extern int NativeRequestPermission();
    [DllImport(Library, EntryPoint = "tbox_capture")] private static extern nint NativeCapture();
    [DllImport(Library, EntryPoint = "tbox_recognize")] private static extern nint NativeRecognize(byte[] data, int length);
    [DllImport(Library, EntryPoint = "tbox_register_hotkeys")] private static extern int NativeRegisterHotkeys(HotkeyCallback callback);
    [DllImport(Library, EntryPoint = "tbox_unregister_hotkeys")] private static extern void NativeUnregisterHotkeys();
    [DllImport(Library, EntryPoint = "tbox_place_window")] private static extern void NativePlaceWindow(nint handle, double left, double top, double width, double height, int overlay);
    [DllImport(Library, EntryPoint = "tbox_move_to_pointer")] private static extern void NativeMoveToPointer(nint handle);
    [DllImport(Library, EntryPoint = "tbox_window_placement")] private static extern nint NativeWindowPlacement(nint handle);
    [DllImport(Library, EntryPoint = "tbox_work_area")] private static extern nint NativeWorkArea(nint handle);
    [DllImport(Library, EntryPoint = "tbox_pointer_position")] private static extern nint NativePointerPosition();
    [DllImport(Library, EntryPoint = "tbox_free")] private static extern void NativeFree(nint pointer);
}
