using System.Runtime.InteropServices;
using System.Diagnostics;
using Tbox.Core;

namespace Tbox.Desktop.Platform;

internal sealed class MacFolderNavigation : IFolderNavigation
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private CancellationTokenSource? _tracking;
    private string _current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string[] _recentPaths = [];
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
        if (!observation.Observed) return;
        Volatile.Write(ref _current, FolderTargetPolicy.Resolve(observation.Path, _home));
        if (observation.Path is { } path && Directory.Exists(path))
            Volatile.Write(ref _recentPaths, FolderJumpList.Remember(_recentPaths, path));
    }

    public Task<FolderDialogTarget?> FindDialogAsync(long? retainedTarget, nint activeBar, CancellationToken cancellationToken) =>
        Task.Run(() => MacPlatformServices.ReadJson<DialogResponse>(NativeDialog(retainedTarget ?? 0, activeBar)).Target, cancellationToken);

    public Task<IReadOnlyList<string>> GetOpenFoldersAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var paths = MacPlatformServices.ReadJson<OpenFolders>(NativeOpenFolders()).Paths;
        return FolderJumpList.OrderOpened(paths.Where(Directory.Exists), Volatile.Read(ref _recentPaths)).ToArray();
    }, cancellationToken);

    public async Task<IReadOnlyList<string>> SearchFoldersAsync(string query, CancellationToken cancellationToken)
    {
        var search = new ProcessStartInfo("/usr/bin/mdfind") { RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetDirectoryName(query) is { } parent && Path.IsPathFullyQualified(parent) && Directory.Exists(parent))
        {
            search.ArgumentList.Add("-onlyin");
            search.ArgumentList.Add(parent);
            query = Path.GetFileName(query);
        }
        var term = query.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal).Replace("?", "\\?", StringComparison.Ordinal);
        search.ArgumentList.Add($"kMDItemContentType == 'public.folder' && kMDItemFSName == '*{term}*'cd");
        using var process = Process.Start(search);
        if (process is null) return [];
        var paths = new List<string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            while (paths.Count < FolderJumpList.SearchLimit && await process.StandardOutput.ReadLineAsync(deadline.Token) is { } path)
                if (Directory.Exists(path)) paths.Add(path);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        finally
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
        }
        return paths;
    }

    public Task<bool> RestoreDialogAsync(long target, nint activeBar, CancellationToken cancellationToken) =>
        Task.Run(() => NativeRestore(target, activeBar) != 0, cancellationToken);

    public async Task<FolderNavigationResult> JumpToAsync(long target, nint activeBar, string path, Shortcut? shortcut,
        CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) return new(false, "文件夹跳转正在进行。");
        try
        {
            return await Task.Run(() =>
            {
                if (!Directory.Exists(path)) return new FolderNavigationResult(false, "目标文件夹不可用。");
                var key = shortcut is { } trigger ? ShortcutPolicy.MacKeyCode(trigger.Key) ?? 0x05 : 0x24;
                return ReadJump(NativeJumpTo(path, key, target, activeBar));
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }
    public async Task<FolderNavigationResult> JumpAsync(Shortcut shortcut, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) return new(false, "正在刷新 Finder 目录，请稍后再试。");
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested(); Refresh(true);
                return ReadJump(NativeJump(FolderTargetPolicy.Resolve(CurrentDirectory, _home),
                    ShortcutPolicy.MacKeyCode(shortcut.Key) ?? 0x05));
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }
    private static FolderNavigationResult ReadJump(nint response)
    {
        var result = MacPlatformServices.ReadJson<NativeJumpResult>(response);
        if (!result.Success && result.Diagnostic is not null) AppLog.Write("Folder navigation: " + result.Diagnostic);
        return new(result.Success, result.Message);
    }
    public void Dispose() => Stop();
    private sealed record Observation(bool Observed, string? Path);
    private sealed record DialogResponse(FolderDialogTarget? Target);
    private sealed record OpenFolders(string[] Paths);
    private sealed record NativeJumpResult(bool Success, string? Message, string? Diagnostic);
    [DllImport("TboxNative", EntryPoint = "tbox_folder_permission")] private static extern int NativePermission();
    [DllImport("TboxNative", EntryPoint = "tbox_enable_elements")] private static extern int NativeRequestPermission();
    [DllImport("TboxNative", EntryPoint = "tbox_finder_directory")] private static extern nint NativeDirectory(int refresh);
    [DllImport("TboxNative", EntryPoint = "tbox_folder_jump")] private static extern nint NativeJump([MarshalAs(UnmanagedType.LPUTF8Str)] string directory, int hotkeyCode);
    [DllImport("TboxNative", EntryPoint = "tbox_folder_dialog")] private static extern nint NativeDialog(long retained, nint bar);
    [DllImport("TboxNative", EntryPoint = "tbox_finder_open_folders")] private static extern nint NativeOpenFolders();
    [DllImport("TboxNative", EntryPoint = "tbox_restore_folder_dialog")] private static extern int NativeRestore(long target, nint bar);
    [DllImport("TboxNative", EntryPoint = "tbox_folder_jump_to")] private static extern nint NativeJumpTo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string directory, int hotkeyCode, long target, nint bar);
}
