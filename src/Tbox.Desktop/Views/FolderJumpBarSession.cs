using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using Tbox.Core;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Views;

internal sealed class FolderJumpBarSession : IDisposable
{
    private readonly DesktopController _controller;
    private readonly IFolderNavigation _navigation;
    private readonly CancellationToken _shutdown;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _followTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private CancellationTokenSource? _observation;
    private IDisposable? _dialogWatch;
    private int _watchRevision;
    private FolderDialogTarget? _target;
    private FolderJumpBarWindow? _bar;
    private DesktopPlacement? _placement;
    private double _placementScale;
    private bool _checking;
    private bool _refreshing;
    private bool _navigating;
    private long _refreshAfter;

    public FolderJumpBarSession(DesktopController controller, IFolderNavigation navigation, CancellationToken shutdown)
    {
        _controller = controller;
        _navigation = navigation;
        _shutdown = shutdown;
        _timer.Tick += (_, _) => _ = CheckAsync();
        _followTimer.Tick += (_, _) => FlushTrackedPlacement();
    }

    public void Sync()
    {
        if (_controller.Settings is { FolderJumpEnabled: true, ShowFolderJumpBar: true } && !_shutdown.IsCancellationRequested)
        {
            if (_observation is not null) return;
            _observation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
            _timer.Start();
            return;
        }
        Dispose();
    }

    internal async Task CheckAsync()
    {
        if (_checking || _navigating || _observation is not { } source) return;
        _checking = true;
        var token = source.Token;
        try
        {
            var next = _navigation.HasPermission
                ? await _navigation.FindDialogAsync(_target?.Id, ActiveBar, token)
                : null;
            if (token.IsCancellationRequested || _navigating) return;
            SetTarget(next);
            if (_bar is { IsVisible: true } bar && !_refreshing && ActiveBar == 0 && Environment.TickCount64 >= _refreshAfter)
                _ = RefreshOpenedAsync(bar, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            AppLog.Write("Folder bar observation: " + error.GetType().Name);
            SetTarget(null);
        }
        finally { _checking = false; }
    }

    private void SetTarget(FolderDialogTarget? next)
    {
        var targetChanged = _target?.Id != next?.Id;
        if (targetChanged)
        {
            StopWatching();
            CloseBar();
        }
        _target = next;
        if (next is null) return;
        if (targetChanged)
        {
            var revision = _watchRevision;
            _dialogWatch = _navigation.WatchDialog(next.Id, change => OnDialogChange(change, revision));
        }
        if (_bar is null && Placement(next, FolderJumpBarWindow.CompactHeight) is not null)
        {
            _bar = new FolderJumpBarWindow(_controller, _navigation, SelectAsync, ReturnAsync, LayoutBar);
            _refreshAfter = 0;
        }
        LayoutBar();
    }

    private void OnDialogChange(FolderDialogChange change, int revision)
    {
        if (revision != _watchRevision) return;
        if (change == FolderDialogChange.Position)
        {
            if (!_followTimer.IsEnabled) _followTimer.Start();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (revision != _watchRevision) return;
            if (change == FolderDialogChange.Closed) SetTarget(null);
            else FlushTrackedPlacement();
            if (_target is null) _ = CheckAsync();
        }, DispatcherPriority.Input);
    }

    private void FlushTrackedPlacement()
    {
        _followTimer.Stop();
        if (_observation is null || _dialogWatch is null || _target is not { } target) return;
        try { SetTarget(_navigation.ReadTrackedDialog(target.Id, ActiveBar)); }
        catch (Exception error)
        {
            AppLog.Write("Folder bar placement: " + error.GetType().Name);
            SetTarget(null);
        }
    }

    private void StopWatching()
    {
        _watchRevision++;
        _followTimer.Stop();
        var watch = _dialogWatch;
        _dialogWatch = null;
        watch?.Dispose();
    }

    private async Task RefreshOpenedAsync(FolderJumpBarWindow bar, CancellationToken token)
    {
        _refreshing = true;
        try
        {
            var opened = await _navigation.GetOpenFoldersAsync(token);
            if (!token.IsCancellationRequested && _bar == bar && !_navigating && ActiveBar == 0)
                bar.SetOpenFolders(opened);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { AppLog.Write("Folder bar open directories: " + error.GetType().Name); }
        finally { _refreshAfter = Environment.TickCount64 + 2000; _refreshing = false; }
    }

    private nint ActiveBar => _bar is { IsActive: true, IsVisible: true } ? _bar.TryGetPlatformHandle()?.Handle ?? 0 : 0;

    public async Task<bool> TryJumpAsync(Shortcut shortcut)
    {
        if (_target is not { } target || _bar is not { IsVisible: true } bar) return false;
        var activeBar = ActiveBar;
        var current = await _navigation.FindDialogAsync(target.Id, activeBar, _shutdown);
        if (current?.Id != target.Id || _bar != bar) return false;
        if (bar.RecommendedPath is { } path)
        {
            await SelectAsync(path, shortcut);
            return true;
        }
        if (activeBar == 0) return false;
        return !await ReturnAsync();
    }

    private async Task SelectAsync(string path, Shortcut? shortcut)
    {
        if (_navigating || _target is not { } target || _bar is not { } bar) return;
        _navigating = true;
        try
        {
            var result = await _navigation.JumpToAsync(target.Id, ActiveBar, path, shortcut, _shutdown);
            if (result.Success && bar == _bar) bar.Reset();
            if (!result.Success && bar == _bar) bar.ShowFailure(result.Message);
            await _controller.ReportFolderJumpAsync(result, path);
        }
        finally { _navigating = false; }
    }

    private async Task<bool> ReturnAsync()
    {
        if (_navigating || _target is not { } target) return false;
        _navigating = true;
        try { return await _navigation.RestoreDialogAsync(target.Id, ActiveBar, _shutdown); }
        finally { _navigating = false; }
    }

    private void LayoutBar()
    {
        if (_bar is not { } bar || _target is not { } target) return;
        if (Placement(target, bar.DesiredBarHeight) is not { } placement)
        {
            _placement = null;
            if (bar.IsVisible) bar.Hide();
            return;
        }
        var sameSize = bar.IsVisible && _placementScale == target.Scale &&
            placement.Width == _placement?.Width && placement.Height == _placement?.Height;
        if (sameSize && placement == _placement) return;
        if (!sameSize)
        {
            bar.Width = placement.Width / target.Scale;
            bar.Height = placement.Height / target.Scale;
        }
        if (!bar.IsVisible)
        {
            bar.Position = new PixelPoint((int)Math.Round(placement.Left), (int)Math.Round(placement.Top));
            bar.Show();
        }
        if (bar.TryGetPlatformHandle() is { } handle)
        {
            if (sameSize) _controller.Platform.MoveWindow(handle.Handle, placement);
            else _controller.Platform.PlaceWindow(handle.Handle, placement, overlay: false);
        }
        _placement = placement;
        _placementScale = target.Scale;
    }

    internal static DesktopPlacement? Placement(FolderDialogTarget target, double desiredHeight)
    {
        var bounds = target.Bounds;
        var work = target.WorkArea;
        var scale = target.Scale;
        var gap = 6 * scale;
        var below = work.Top + work.Height - bounds.Top - bounds.Height - gap;
        var above = bounds.Top - work.Top - gap;
        var useBelow = below >= desiredHeight * scale || below >= above;
        var height = Math.Min(desiredHeight * scale, useBelow ? below : above);
        if (height < FolderJumpBarWindow.CompactHeight * scale || work.Width < 280 * scale) return null;
        var width = Math.Min(work.Width, Math.Clamp(bounds.Width - 36 * scale, 300 * scale, 588 * scale));
        var left = Math.Clamp(bounds.Left + (bounds.Width - width) / 2, work.Left, work.Left + work.Width - width);
        var top = useBelow ? bounds.Top + bounds.Height + gap : bounds.Top - gap - height;
        return new(left, top, width, height);
    }

    private void CloseBar()
    {
        var bar = _bar;
        _bar = null;
        _placement = null;
        bar?.Close();
    }

    public void Dispose()
    {
        _timer.Stop();
        StopWatching();
        _observation?.Cancel();
        _observation?.Dispose();
        _observation = null;
        _target = null;
        CloseBar();
    }
}
