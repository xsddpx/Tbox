using System.Runtime.InteropServices;
using Tbox.Core;

namespace Tbox.App;

internal sealed class FolderJumpModule : ITboxModule
{
    private const int HotkeyId = 0x5442;
    private readonly IntPtr _windowHandle;
    private readonly ExplorerFolderTracker _tracker;
    private readonly Action<string> _notify;
    private readonly IDisposable _messageSubscription;
    private int _navigationInProgress;
    private bool _disposed;

    public string Id => "folder-jump";
    public string DisplayName => "文件夹跳转";
    public string Description => "让文件对话框跳转到最近使用的资源管理器目录。";
    public bool IsEnabled { get; private set; }
    public event EventHandler<bool>? EnabledChanged;

    internal FolderJumpModule(IntPtr windowHandle, WindowMessageRouter messageRouter, Action<string> notify)
    {
        _windowHandle = windowHandle;
        _notify = notify;
        _tracker = new ExplorerFolderTracker();
        _messageSubscription = messageRouter.Subscribe(OnWindowMessage);
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (enabled == IsEnabled) return Task.CompletedTask;

        if (enabled)
        {
            if (!NativeMethods.RegisterHotKey(
                    _windowHandle,
                    HotkeyId,
                    NativeMethods.ModControl | NativeMethods.ModNoRepeat,
                    NativeMethods.VkG))
            {
                var error = Marshal.GetLastWin32Error();
                AppLog.Write($"RegisterHotKey failed. Win32={error}");
                _notify("Ctrl + G 已被其他应用占用。");
                return Task.CompletedTask;
            }

            try
            {
                _tracker.Start();
            }
            catch
            {
                NativeMethods.UnregisterHotKey(_windowHandle, HotkeyId);
                throw;
            }
            AppLog.Write("RegisterHotKey succeeded for Ctrl+G.");
        }
        else
        {
            NativeMethods.UnregisterHotKey(_windowHandle, HotkeyId);
            _tracker.Stop();
        }

        IsEnabled = enabled;
        EnabledChanged?.Invoke(this, enabled);
        return Task.CompletedTask;
    }

    private bool OnWindowMessage(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (message != NativeMethods.WmHotkey || wParam.ToInt32() != HotkeyId)
        {
            return false;
        }

        var dialog = NativeMethods.GetForegroundWindow();
        AppLog.Write($"Ctrl+G received for window 0x{dialog.ToInt64():X}.");
        if (Interlocked.Exchange(ref _navigationInProgress, 1) != 0)
        {
            AppLog.Write("Folder jump ignored because a navigation is already running.");
            return true;
        }

        _ = Task.Run(() => Navigate(dialog));
        return true;
    }

    private void Navigate(IntPtr dialog)
    {
        try
        {
            WaitForHotkeyRelease();
            var target = _tracker.RefreshCurrentFolder();
            var navigationResult = FileDialogNavigator.Navigate(dialog, target);
            AppLog.Write($"Navigation result: {navigationResult}");
            if (navigationResult != NavigationResult.Success)
            {
                _notify(navigationResult switch
                {
                    NavigationResult.UnsupportedWindow => "当前窗口不是受支持的标准文件对话框。",
                    NavigationResult.InvalidPath => "最近使用的文件夹已经不存在。",
                    _ => "无法控制当前文件对话框。"
                });
            }
        }
        catch (Exception exception)
        {
            AppLog.Write($"Navigation exception: {exception}");
            _notify("文件夹跳转失败，Tbox 已保持运行。");
        }
        finally
        {
            Volatile.Write(ref _navigationInProgress, 0);
        }
    }

    private static void WaitForHotkeyRelease()
    {
        var deadline = Environment.TickCount64 + 300;
        while (IsKeyDown(NativeMethods.VkControl) || IsKeyDown(NativeMethods.VkG))
        {
            if (Environment.TickCount64 >= deadline)
            {
                AppLog.Write("Folder-jump hotkey was still pressed after 300 ms; navigation continued.");
                return;
            }
            Thread.Sleep(5);
        }
    }

    private static bool IsKeyDown(int virtualKey)
        => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed) return;
        NativeMethods.UnregisterHotKey(_windowHandle, HotkeyId);
        _tracker.Dispose();
        _messageSubscription.Dispose();
        _disposed = true;
    }
}
