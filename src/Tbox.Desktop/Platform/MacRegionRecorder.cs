using System.Runtime.InteropServices;

namespace Tbox.Desktop.Platform;

internal sealed class MacRegionRecorder(RecordingArea area, bool audio, int fps) : IRegionRecorder
{
    private RecorderHandle? _handle;
    private bool _disposed;
    public string? Failure => _handle is null ? null : Call(handle => MacPlatformServices.ReadJson<Status>(NativeStatus(handle)).Failure);
    public async Task StartAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Task.Run(() => MacPlatformServices.ReadJson<Started>(NativeStart(path,
            area.Bounds.Left, area.Bounds.Top, area.Bounds.Width, area.Bounds.Height, area.PixelWidth, area.PixelHeight, fps, audio ? 1 : 0)));
        var owned = new RecorderHandle((nint)result.Handle);
        if (_disposed) { owned.Dispose(); throw new ObjectDisposedException(nameof(MacRegionRecorder)); }
        _handle = owned;
        cancellationToken.ThrowIfCancellationRequested();
    }
    public void Pause() => Call(handle => { NativePause(handle); return 0; });
    public void Resume() => Call(handle => { NativeResume(handle); return 0; });
    public async Task<RecordingOutput> StopAsync(CancellationToken cancellationToken)
    {
        return await Task.Run(() => Call(handle => MacPlatformServices.ReadJson<RecordingOutput>(NativeStop(handle))));
    }
    public void Dispose()
    {
        _disposed = true;
        Interlocked.Exchange(ref _handle, null)?.Dispose();
    }
    private T Call<T>(Func<nint, T> action)
    {
        var owned = _handle ?? throw new InvalidOperationException("录屏尚未开始或已关闭。");
        var retained = false;
        try { owned.DangerousAddRef(ref retained); return action(owned.DangerousGetHandle()); }
        finally { if (retained) owned.DangerousRelease(); }
    }
    private sealed class RecorderHandle : SafeHandle
    {
        public RecorderHandle(nint value) : base(0, true) => SetHandle(value);
        public override bool IsInvalid => handle == 0;
        protected override bool ReleaseHandle() { NativeDispose(handle); return true; }
    }
    private sealed record Started(long Handle);
    private sealed record Status(string? Failure);
    [DllImport("TboxNative", EntryPoint = "tbox_record_start")] private static extern nint NativeStart([MarshalAs(UnmanagedType.LPUTF8Str)] string path, double left, double top, double width, double height, int pixelWidth, int pixelHeight, int fps, int audio);
    [DllImport("TboxNative", EntryPoint = "tbox_record_pause")] private static extern void NativePause(nint handle);
    [DllImport("TboxNative", EntryPoint = "tbox_record_resume")] private static extern void NativeResume(nint handle);
    [DllImport("TboxNative", EntryPoint = "tbox_record_status")] private static extern nint NativeStatus(nint handle);
    [DllImport("TboxNative", EntryPoint = "tbox_record_stop")] private static extern nint NativeStop(nint handle);
    [DllImport("TboxNative", EntryPoint = "tbox_record_dispose")] private static extern void NativeDispose(nint handle);
}
