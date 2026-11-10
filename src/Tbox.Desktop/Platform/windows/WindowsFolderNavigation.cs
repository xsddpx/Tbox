#if WINDOWS
using Tbox.Core;

namespace Tbox.Desktop.Platform;

internal sealed class WindowsFolderNavigation(WindowsFolderPicker picker, WindowsPlatformServices platform) : IFolderNavigation
{
    private readonly ExplorerFolderTracker _tracker = new();
    private readonly SemaphoreSlim _navigation = new(1);
    public string CurrentDirectory => _tracker.CurrentFolder;
    public bool HasPermission => true;
    public bool RequestPermission() => true;
    public void Start() => _tracker.Start();
    public void Stop() => _tracker.Stop();
    public Task<FolderDialogTarget?> FindDialogAsync(long? retainedTarget, nint activeBar, CancellationToken cancellationToken)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var target = foreground != 0 && foreground == activeBar ? (nint)(retainedTarget ?? 0) : foreground;
        if (!IsDialog(target)) return Task.FromResult<FolderDialogTarget?>(null);
        try
        {
            return Task.FromResult<FolderDialogTarget?>(new(target, platform.GetWindowPlacement(target),
                platform.GetWorkArea(target), platform.DesktopUnitsPerDip(target)));
        }
        catch (System.ComponentModel.Win32Exception) { return Task.FromResult<FolderDialogTarget?>(null); }
    }

    public Task<IReadOnlyList<string>> GetOpenFoldersAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        _tracker.RefreshCurrentFolder();
        return _tracker.ReadOpenFolders(cancellationToken);
    }, cancellationToken);

    public Task<IReadOnlyList<string>> SearchFoldersAsync(string query, CancellationToken cancellationToken) =>
        Task.Run(() => WindowsFolderIndex.Search(query, cancellationToken), cancellationToken);

    public async Task<bool> RestoreDialogAsync(long target, nint activeBar, CancellationToken cancellationToken)
    {
        var window = (nint)target;
        if (!IsDialog(window)) return false;
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == window) return true;
        if (activeBar == 0 || foreground != activeBar || !NativeMethods.SetForegroundWindow(window)) return false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (NativeMethods.GetForegroundWindow() == window) return IsDialog(window);
            await Task.Delay(15, cancellationToken);
        }
        return false;
    }

    public Task<FolderNavigationResult> JumpAsync(Shortcut shortcut, CancellationToken cancellationToken) =>
        NavigateAsync(NativeMethods.GetForegroundWindow(), 0, null, shortcut, cancellationToken);

    public Task<FolderNavigationResult> JumpToAsync(long target, nint activeBar, string path, Shortcut? shortcut,
        CancellationToken cancellationToken) => NavigateAsync((nint)target, activeBar, path, shortcut, cancellationToken);

    private bool IsDialog(nint window) => window != 0 && (picker.Owns(window) || FileDialogNavigator.IsStandardDialog(window));

    private async Task<FolderNavigationResult> NavigateAsync(nint window, nint activeBar, string? destination,
        Shortcut? shortcut, CancellationToken cancellationToken)
    {
        if (!await _navigation.WaitAsync(0, cancellationToken)) return new(false, "文件夹跳转正在进行。");
        try
        {
            // Let the dialog see neither the global shortcut nor the Enter used to select a result.
            int[] held = [0x11, 0x12, 0x10, 0x5B, 0x5C, shortcut is { } key ? ShortcutPolicy.WindowsKeyCode(key.Key) ?? 0x47 : 0x0D];
            var path = await Task.Run(() =>
            {
                var deadline = Environment.TickCount64 + 300;
                while (held.Any(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Environment.TickCount64 >= deadline) return null;
                    Thread.Sleep(5);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return destination ?? _tracker.RefreshCurrentFolder();
            }, cancellationToken);
            if (path is null) return new(false, "请松开快捷键后重试文件夹跳转。");
            if (destination is not null && !await RestoreDialogAsync(window, activeBar, cancellationToken))
                return new(false, "原文件对话框已关闭或不在前台。");
            if (picker.Owns(window))
            {
                if (!await Task.Run(() => Directory.Exists(path), cancellationToken)) return new(false, "目标文件夹不可用。");
                return await picker.NavigateAsync(window, path, cancellationToken);
            }
            var result = await Task.Run(() => FileDialogNavigator.Navigate(window, path), cancellationToken);
            return result switch
            {
                NavigationResult.Success => new FolderNavigationResult(true),
                NavigationResult.UnsupportedWindow => new(false, "当前窗口不是受支持的标准文件对话框。"),
                NavigationResult.InvalidPath => new(false, "目标文件夹不可用。"),
                _ => new(false, "无法控制当前文件对话框。")
            };
        }
        finally { _navigation.Release(); }
    }
    public void Dispose() => _tracker.Dispose();
}
#endif
