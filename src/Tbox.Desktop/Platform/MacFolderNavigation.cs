using System.Runtime.InteropServices;
using Tbox.Core;

namespace Tbox.Desktop.Platform;

internal sealed class MacFolderNavigation : IFolderNavigation
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private CancellationTokenSource? _tracking;
    private string _current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string CurrentDirectory => Volatile.Read(ref _current);
    public bool HasPermission => NativePermission() != 0;
    public bool RequestPermission() => NativeRequestPermission() != 0;
    public void Start()
    {
        if (_tracking is not null) return;
        _tracking = new(); _ = TrackAsync(_tracking.Token);
    }
    public void Stop() { var previous = _tracking; _tracking = null; previous?.Cancel(); previous?.Dispose(); }
    private async Task TrackAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!HasPermission || !await _gate.WaitAsync(0, cancellationToken)) continue;
                try { await Task.Run(() => Refresh(false), cancellationToken); }
                catch (Exception error) when (error is not OperationCanceledException) { AppLog.Write("Finder observation: " + error.GetType().Name); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Refresh(bool force)
    {
        var observation = MacPlatformServices.ReadJson<Observation>(NativeDirectory(force ? 1 : 0));
        if (observation.Observed) Volatile.Write(ref _current, FolderTargetPolicy.Resolve(observation.Path, _home));
    }
    public async Task<FolderNavigationResult> JumpAsync(Shortcut shortcut, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) return new(false, "正在刷新 Finder 目录，请稍后再试。");
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested(); Refresh(true);
                var result = MacPlatformServices.ReadJson<NativeJumpResult>(NativeJump(FolderTargetPolicy.Resolve(CurrentDirectory, _home),
                    ShortcutPolicy.MacKeyCode(shortcut.Key) ?? 0x05));
                if (!result.Success && result.Diagnostic is not null) AppLog.Write("Folder navigation: " + result.Diagnostic);
                return new FolderNavigationResult(result.Success, result.Message);
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }
    public void Dispose() => Stop();
    private sealed record Observation(bool Observed, string? Path);
    private sealed record NativeJumpResult(bool Success, string? Message, string? Diagnostic);
    [DllImport("TboxNative", EntryPoint = "tbox_folder_permission")] private static extern int NativePermission();
    [DllImport("TboxNative", EntryPoint = "tbox_enable_elements")] private static extern int NativeRequestPermission();
    [DllImport("TboxNative", EntryPoint = "tbox_finder_directory")] private static extern nint NativeDirectory(int refresh);
    [DllImport("TboxNative", EntryPoint = "tbox_folder_jump")] private static extern nint NativeJump([MarshalAs(UnmanagedType.LPUTF8Str)] string directory, int hotkeyCode);
}
