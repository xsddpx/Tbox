using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Tbox.Core;
using Windows.Graphics;

namespace Tbox.App;

internal sealed class ScreenPasteModule : ITboxModule
{
    private const int CaptureHotkeyId = 0x5443;
    private const int ClipboardPasteHotkeyId = 0x5444;
    private const int TogglePinsHotkeyId = 0x5445;
    private const uint VkF6 = 0x75;
    private const uint VkF7 = 0x76;
    private const uint VkF8 = 0x77;
    private readonly IntPtr _windowHandle;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string> _notify;
    private readonly IDisposable _messageSubscription;
    private CaptureCache _captureCache;
    private readonly List<PinnedImageWindow> _pins = [];
    private Task _pendingCaptureSaveTask = Task.CompletedTask;
    private readonly List<CaptureOverlayWindow> _captureOverlays = [];
    private bool _captureStarting;
    private bool _disposed;

    public string Id => "screen-paste";
    public string DisplayName => "截图与贴图";
    public string Description => "使用 F6 在任意显示器截图、F7 贴出剪贴板图像、F8 整体显示或隐藏全部贴图。";
    public bool IsEnabled { get; private set; }
    public event EventHandler<bool>? EnabledChanged;

    internal ScreenPasteModule(
        IntPtr windowHandle,
        WindowMessageRouter messageRouter,
        DispatcherQueue dispatcher,
        string cacheDirectory,
        Action<string> notify)
    {
        _windowHandle = windowHandle;
        _dispatcher = dispatcher;
        _notify = notify;
        _messageSubscription = messageRouter.Subscribe(OnWindowMessage);
        _captureCache = new CaptureCache(cacheDirectory);
    }

    internal string CacheDirectory => _captureCache.DirectoryPath;

    internal void SetCacheDirectory(string directoryPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _captureCache = new CaptureCache(directoryPath);
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (enabled == IsEnabled) return Task.CompletedTask;

        if (enabled)
        {
            if (!NativeMethods.RegisterHotKey(_windowHandle, CaptureHotkeyId, NativeMethods.ModNoRepeat, VkF6))
            {
                _notify($"F6 已被其他应用占用（Win32={Marshal.GetLastWin32Error()}）。");
                return Task.CompletedTask;
            }

            if (!NativeMethods.RegisterHotKey(_windowHandle, ClipboardPasteHotkeyId, NativeMethods.ModNoRepeat, VkF7))
            {
                var error = Marshal.GetLastWin32Error();
                NativeMethods.UnregisterHotKey(_windowHandle, CaptureHotkeyId);
                _notify($"F7 已被其他应用占用（Win32={error}）。");
                return Task.CompletedTask;
            }

            if (!NativeMethods.RegisterHotKey(_windowHandle, TogglePinsHotkeyId, NativeMethods.ModNoRepeat, VkF8))
            {
                var error = Marshal.GetLastWin32Error();
                NativeMethods.UnregisterHotKey(_windowHandle, CaptureHotkeyId);
                NativeMethods.UnregisterHotKey(_windowHandle, ClipboardPasteHotkeyId);
                _notify($"F8 已被其他应用占用（Win32={error}）。");
                return Task.CompletedTask;
            }
        }
        else
        {
            NativeMethods.UnregisterHotKey(_windowHandle, CaptureHotkeyId);
            NativeMethods.UnregisterHotKey(_windowHandle, ClipboardPasteHotkeyId);
            NativeMethods.UnregisterHotKey(_windowHandle, TogglePinsHotkeyId);
            CloseWindows();
        }

        IsEnabled = enabled;
        EnabledChanged?.Invoke(this, enabled);
        return Task.CompletedTask;
    }

    private bool OnWindowMessage(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (message != NativeMethods.WmHotkey) return false;

        var hotkey = wParam.ToInt32();
        if (hotkey == CaptureHotkeyId)
        {
            _dispatcher.TryEnqueue(async () => await StartCaptureAsync());
            return true;
        }
        if (hotkey == ClipboardPasteHotkeyId)
        {
            _dispatcher.TryEnqueue(async () => await PasteClipboardAsync());
            return true;
        }
        if (hotkey == TogglePinsHotkeyId)
        {
            _dispatcher.TryEnqueue(TogglePinsVisibility);
            return true;
        }
        return false;
    }

    private async Task StartCaptureAsync()
    {
        if (_captureStarting || _captureOverlays.Count > 0) return;
        _captureStarting = true;
        var overlays = new List<CaptureOverlayWindow>();
        try
        {
            var screens = ScreenCaptureService.CaptureAllMonitors();
            foreach (var screen in screens)
            {
                overlays.Add(await CaptureOverlayWindow.CreateAsync(screen));
            }
            if (_disposed || !IsEnabled)
            {
                foreach (var overlay in overlays) overlay.Close();
                return;
            }

            _captureOverlays.AddRange(overlays);
            var sessionCompleted = false;
            var selectionSession = new CaptureSelectionSession();
            for (var index = 0; index < overlays.Count; index++)
            {
                var overlayIndex = index;
                var overlay = overlays[index];
                var completingOverlay = overlay;
                overlay.SelectionStarted += () =>
                {
                    var previousIndex = selectionSession.BeginSelection(overlayIndex, overlays.Count);
                    if (previousIndex is { } peerIndex && peerIndex != overlayIndex)
                    {
                        overlays[peerIndex].ResetForPeerSelection();
                    }
                };
                overlay.Completed += result =>
                {
                    if (sessionCompleted) return;
                    sessionCompleted = true;
                    _captureOverlays.Clear();
                    foreach (var peer in overlays)
                    {
                        if (!ReferenceEquals(peer, completingOverlay)) peer.Close();
                    }
                    if (result is not null) QueueCapture(result);
                };
            }

            var cursor = ScreenCaptureService.GetCursorPosition();
            var activeIndex = CaptureDisplayPolicy.FindContainingDisplayIndex(
                screens.Select(screen => new CaptureDisplayBounds(
                    screen.Left,
                    screen.Top,
                    screen.Width,
                    screen.Height)).ToArray(),
                cursor.X,
                cursor.Y);
            for (var index = 0; index < overlays.Count; index++)
            {
                if (index != activeIndex) overlays[index].Show(activate: false);
            }
            overlays[activeIndex].Show(activate: true);
        }
        catch (Exception exception)
        {
            _captureOverlays.Clear();
            foreach (var overlay in overlays) overlay.Close();
            AppLog.Write($"Screen capture failed: {exception}");
            _notify("无法开始截图，Tbox 已保持运行。");
        }
        finally
        {
            _captureStarting = false;
        }
    }

    private void QueueCapture(CaptureCompletion result)
    {
        var previousSave = _pendingCaptureSaveTask;
        var cache = _captureCache;
        _pendingCaptureSaveTask = ProcessCaptureAfterAsync(previousSave, cache, result);
    }

    private async Task ProcessCaptureAfterAsync(Task previousSave, CaptureCache cache, CaptureCompletion result)
    {
        await previousSave;
        try
        {
            var pngBytes = await result.Screen.CreatePngBytesAsync();
            var cacheFilePath = await cache.SaveAsync(pngBytes);
            PruneCache(cache, cacheFilePath);
            switch (result.Action)
            {
                case CaptureCompletionAction.Pin:
                    if (_disposed || !IsEnabled) return;
                    await CreatePinAsync(
                        cacheFilePath,
                        new RectInt32(
                            result.Screen.Left,
                            result.Screen.Top,
                            result.Screen.Width,
                            result.Screen.Height));
                    break;
                case CaptureCompletionAction.Copy:
                    await ClipboardImageService.CopyFileAsync(cacheFilePath);
                    break;
                case CaptureCompletionAction.Save:
                    if (result.SaveDestination is null)
                    {
                        throw new InvalidOperationException("截图保存目标缺失。");
                    }
                    await using (var source = new FileStream(
                                     cacheFilePath,
                                     FileMode.Open,
                                     FileAccess.Read,
                                     FileShare.Read,
                                     bufferSize: 81920,
                                     useAsync: true))
                    await using (var destination = await result.SaveDestination.OpenStreamForWriteAsync())
                    {
                        destination.SetLength(0);
                        await source.CopyToAsync(destination);
                        await destination.FlushAsync();
                    }
                    break;
            }
        }
        catch (Exception exception)
        {
            AppLog.Write($"Process capture failed: {exception}");
            _notify(result.Action switch
            {
                CaptureCompletionAction.Copy => "截图已完成，但无法保存或复制图像。",
                CaptureCompletionAction.Save => "截图已完成，但无法保存到所选位置。",
                _ => "截图已完成，但无法保存或创建贴图。"
            });
        }
    }

    private async Task PasteClipboardAsync()
    {
        try
        {
            var pngBytes = await ClipboardImageService.TryReadPngAsync();
            if (pngBytes is null)
            {
                _notify("剪贴板中没有可贴图的图像。");
                return;
            }

            var cache = _captureCache;
            var cacheFilePath = await cache.SaveAsync(pngBytes);
            PruneCache(cache, cacheFilePath);
            if (_disposed || !IsEnabled) return;
            await CreatePinAsync(cacheFilePath);
        }
        catch (Exception exception)
        {
            AppLog.Write($"Paste clipboard image failed: {exception}");
            _notify("无法读取或贴出剪贴板图像。");
        }
    }

    private void TogglePinsVisibility()
    {
        if (_pins.Count == 0)
        {
            _notify("当前没有可显示或隐藏的贴图。");
            return;
        }

        var shouldHide = _pins.Any(pin => pin.IsVisible);
        foreach (var pin in _pins.ToArray())
        {
            if (shouldHide)
            {
                pin.Hide();
            }
            else
            {
                pin.Show(activate: false);
            }
        }
    }

    private async Task CreatePinAsync(string? cacheFilePath = null, RectInt32? initialBounds = null)
    {
        cacheFilePath ??= _captureCache.GetLatestFilePath();
        if (cacheFilePath is null)
        {
            _notify("还没有缓存截图，请先按 F6 截图。");
            return;
        }

        var pin = await PinnedImageWindow.CreateAsync(cacheFilePath, _notify, initialBounds);
        pin.Dismissed += OnPinDismissed;
        _pins.Add(pin);
        pin.Show(activate: true);
    }

    private void PruneCache(CaptureCache cache, string newestFilePath)
    {
        try
        {
            cache.Prune(_pins.Select(pin => pin.CacheFilePath).Append(newestFilePath));
        }
        catch (Exception exception)
        {
            AppLog.Write($"Capture cache cleanup failed: {exception}");
        }
    }

    private void OnPinDismissed(PinnedImageWindow pin)
    {
        pin.Dismissed -= OnPinDismissed;
        _pins.Remove(pin);
    }

    private void CloseWindows()
    {
        foreach (var overlay in _captureOverlays.ToArray()) overlay.Close();
        _captureOverlays.Clear();
        ClosePins();
    }

    private void ClosePins()
    {
        foreach (var pin in _pins.ToArray()) pin.Close();
        _pins.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        NativeMethods.UnregisterHotKey(_windowHandle, CaptureHotkeyId);
        NativeMethods.UnregisterHotKey(_windowHandle, ClipboardPasteHotkeyId);
        NativeMethods.UnregisterHotKey(_windowHandle, TogglePinsHotkeyId);
        CloseWindows();
        _messageSubscription.Dispose();
        _disposed = true;
    }
}
