#if WINDOWS
using Tbox.App;

namespace Tbox.Desktop.Platform;

internal sealed class WindowsFolderNavigation : IFolderNavigation
{
    private readonly ExplorerFolderTracker _tracker = new();
    private readonly SemaphoreSlim _navigation = new(1);
    public string CurrentDirectory => _tracker.CurrentFolder;
    public bool HasPermission => true;
    public bool RequestPermission() => true;
    public void Start() => _tracker.Start();
    public void Stop() => _tracker.Stop();
    public async Task<FolderNavigationResult> JumpAsync(CancellationToken cancellationToken)
    {
        if (!await _navigation.WaitAsync(0, cancellationToken)) return new(false, "文件夹跳转正在进行。");
        var window = NativeMethods.GetForegroundWindow();
        try
        {
            return await Task.Run(() =>
            {
                var deadline = Environment.TickCount64 + 300;
                while (Environment.TickCount64 < deadline &&
                    ((NativeMethods.GetAsyncKeyState(NativeMethods.VkControl) & 0x8000) != 0 || (NativeMethods.GetAsyncKeyState(NativeMethods.VkG) & 0x8000) != 0))
                { cancellationToken.ThrowIfCancellationRequested(); Thread.Sleep(5); }
                cancellationToken.ThrowIfCancellationRequested();
                var result = FileDialogNavigator.Navigate(window, _tracker.RefreshCurrentFolder());
                return result switch
                {
                    NavigationResult.Success => new FolderNavigationResult(true),
                    NavigationResult.UnsupportedWindow => new(false, "当前窗口不是受支持的标准文件对话框。"),
                    NavigationResult.InvalidPath => new(false, "最近使用的文件夹已经不存在。"),
                    _ => new(false, "无法控制当前文件对话框。")
                };
            }, cancellationToken);
        }
        finally { _navigation.Release(); }
    }
    public void Dispose() => _tracker.Dispose();
}
#endif
