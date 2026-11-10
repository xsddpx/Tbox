#if WINDOWS

namespace Tbox.Desktop.Platform;

internal sealed class WindowsFolderNavigation(WindowsFolderPicker picker) : IFolderNavigation
{
    private readonly ExplorerFolderTracker _tracker = new();
    private readonly SemaphoreSlim _navigation = new(1);
    public string CurrentDirectory => _tracker.CurrentFolder;
    public bool HasPermission => true;
    public bool RequestPermission() => true;
    public void Start() => _tracker.Start();
    public void Stop() => _tracker.Stop();
    public async Task<FolderNavigationResult> JumpAsync(Tbox.Core.Shortcut shortcut, CancellationToken cancellationToken)
    {
        if (!await _navigation.WaitAsync(0, cancellationToken)) return new(false, "文件夹跳转正在进行。");
        var window = NativeMethods.GetForegroundWindow();
        // The dialog must not see the hotkey: wait until Ctrl, Alt, Shift, Win and the shortcut's key are up.
        int[] held = [0x11, 0x12, 0x10, 0x5B, 0x5C, Tbox.Core.ShortcutPolicy.WindowsKeyCode(shortcut.Key) ?? 0x47];
        try
        {
            var owned = picker.Owns(window);
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
                return _tracker.RefreshCurrentFolder();
            }, cancellationToken);
            if (path is null) return new(false, "请松开快捷键后重试文件夹跳转。");
            if (owned) return await picker.NavigateAsync(window, path, cancellationToken);
            var result = await Task.Run(() => FileDialogNavigator.Navigate(window, path), cancellationToken);
            return result switch
            {
                NavigationResult.Success => new FolderNavigationResult(true),
                NavigationResult.UnsupportedWindow => new(false, "当前窗口不是受支持的标准文件对话框。"),
                NavigationResult.InvalidPath => new(false, "最近使用的文件夹已经不存在。"),
                _ => new(false, "无法控制当前文件对话框。")
            };
        }
        finally { _navigation.Release(); }
    }
    public void Dispose() => _tracker.Dispose();
}
#endif
