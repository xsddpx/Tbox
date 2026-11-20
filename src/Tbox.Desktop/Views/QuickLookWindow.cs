using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Views;

// 速览 (spec: PowerToys Peek). One window kept for the session: hidden when closed, resized and centred for each file.
internal sealed class QuickLookWindow : Window
{
    private const double TitleBarHeight = QuickLookPolicy.TitleBarHeight, CaptionButtonsWidth = 140;
    private static readonly IBrush Strong = Brush.Parse("#1A1B22"), Secondary = Brush.Parse("#5F6270");
    private readonly DesktopController _controller;
    private readonly IQuickLookSource _source;
    private readonly TextBlock _counter = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Strong, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _name = new() { FontSize = 12, Foreground = Strong, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _open, _pin;
    private readonly TextBlock _openText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _content = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly Border _infoBar;
    private readonly TextBlock _infoText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Strong, VerticalAlignment = VerticalAlignment.Center };
    private readonly Bitmap _brand;
    private QuickLookNavigator _items = new([]);
    private QuickLookPreview? _preview;
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _folderRead;
    private LibVLC? _engine;
    private Task<LibVLC>? _engineTask;
    private long _lastNavigation;
    private bool _pinned, _dialogOpen, _placed;
    private DesktopPlacement? _placement;
    private int _revision;
    private string? _app;

    public QuickLookWindow(DesktopController controller, IQuickLookSource source)
    {
        _controller = controller; _source = source;
        Ui.Configure(this, QuickLookPolicy.WindowTitle(null), 680, 548);
        // Peek's XAML minimum; sizes are set in physical pixels by Place, so Avalonia must not apply its own.
        MinWidth = 480; MinHeight = 320; Width = double.NaN; Height = double.NaN;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ExtendClientAreaToDecorationsHint = true; ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        _brand = Ui.Brand();

        _open = TitleButton(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            new LineIcon(IconKind.OpenFile, brush: Strong) { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center }, _openText
        } }, "", OpenCurrent);
        _pin = TitleButton(new LineIcon(IconKind.Pin, brush: Strong) { Width = 16, Height = 16 }, "将窗口固定为当前大小", TogglePin);
        var titleBar = new Grid
        {
            Height = TitleBarHeight, ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"),
            Margin = new Thickness(0, 0, CaptionButtonsWidth, 0),
            Children =
            {
                new Image { Source = _brand, Width = 16, Height = 16, Margin = new Thickness(16, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false },
                Ui.Column(_counter, 1), Ui.Column(_name, 2), Ui.Column(_open, 3), Ui.Column(_pin, 4)
            }
        };
        _counter.IsHitTestVisible = false; _name.IsHitTestVisible = false;
        _pin.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(titleBar, Dock.Top);

        var close = TitleButton(new LineIcon(IconKind.Close, brush: Strong) { Width = 14, Height = 14 }, "关闭", CloseInfoBar);
        _infoBar = new Border
        {
            IsVisible = false, Margin = new Thickness(12, 0, 12, 8), Padding = new Thickness(14, 8), CornerRadius = new CornerRadius(8),
            Background = Brush.Parse("#FDE7E9"), BorderBrush = Brush.Parse("#F4BFC4"), BorderThickness = new Thickness(1),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10, Children =
            {
                new LineIcon(IconKind.Warn, brush: Brush.Parse("#C42B1C")) { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center },
                Ui.Column(_infoText, 1), Ui.Column(close, 2)
            } }
        };
        DockPanel.SetDock(_infoBar, Dock.Top);
        Content = new DockPanel { Children = { titleBar, _infoBar, _content } };

        AddHandler(KeyDownEvent, OnTunnelKey, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnBubbleKey, RoutingStrategies.Bubble);
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && !_dialogOpen) { e.Handled = true; controller.Run(DeleteCurrentAsync); }
        }, RoutingStrategies.Bubble);
        Deactivated += (_, _) =>
        {
            if (_controller.Settings.QuickLook.CloseAfterLosingFocus && !_dialogOpen && IsVisible) Dispatcher.UIThread.Post(Dismiss);
        };
        Closing += (_, e) =>
        {
            if (controller.IsQuitting) return;
            e.Cancel = true; Dismiss();
        };
        Closed += (_, _) => { ClearPreview(); _engine?.Dispose(); _brand.Dispose(); };
        SizeChanged += (_, _) => Adapt();
        ApplySettings(controller.Settings.QuickLook);
    }

    internal nint Handle => TryGetPlatformHandle()?.Handle ?? 0;
    internal string? CurrentPath => _items.Current;
    internal int ItemCount => _items.Count;
    internal bool Pinned => _pinned;

    internal void ApplySettings(QuickLookSettings settings)
    {
        Topmost = settings.AlwaysOnTop;
        ShowInTaskbar = settings.ShowInTaskbar;
    }

    // Peek's Toggle: a second activation switches to a newly selected single file, otherwise closes.
    internal async Task ToggleAsync(nint foreground)
    {
        var selection = foreground == Handle ? null : await _source.ReadSelectionAsync(foreground, CancellationToken.None);
        // A minimized window (Show desktop, the minimize button) counts as closed, so the key opens it again.
        var open = IsVisible && WindowState != WindowState.Minimized;
        AppLog.Write($"QuickLook toggle: open={open} state={WindowState} items={selection?.Items.Count ?? 0}");
        if (open)
        {
            if (selection is { Multiple: false } && !string.Equals(selection.Items[0], _items.Current, StringComparison.OrdinalIgnoreCase))
            {
                Open(selection, foreground);
                _source.BringToForeground(Handle);
            }
            else Dismiss();
            return;
        }
        // Nothing selectable (renaming, a virtual folder): Peek returns without showing a window.
        if (selection is null) return;
        Open(selection, foreground);
    }

    private void Open(QuickLookSelection selection, nint foreground)
    {
        _folderRead?.Cancel();
        CloseInfoBar(keepOpen: true);
        _items = new QuickLookNavigator(selection.Items);
        _lastNavigation = 0;
        if (!selection.Multiple)
        {
            // A single selection walks the whole folder. The folder list loads behind the first preview.
            var read = _folderRead = new CancellationTokenSource();
            var selected = selection.Items[0];
            _controller.Run(async () =>
            {
                var folder = await _source.ReadFolderAsync(foreground, read.Token);
                if (read.IsCancellationRequested || folder.Count == 0) return;
                _items.Replace(folder, _items.Current ?? selected);
                UpdateTitle();
            });
        }
        _controller.Run(() => ShowCurrentAsync(foreground));
    }

    private void Navigate(int step)
    {
        if (_items.Count < 2) return;
        // Peek ignores navigation within 100 ms of the previous one.
        var now = Environment.TickCount64;
        if (now - _lastNavigation < QuickLookPolicy.NavigationThrottle.TotalMilliseconds) return;
        _lastNavigation = now;
        _items.Move(step);
        _controller.Run(() => ShowCurrentAsync(Handle));
    }

    private async Task ShowCurrentAsync(nint monitorWindow)
    {
        var revision = ++_revision;
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        var token = loading.Token;
        ClearPreview();
        UpdateTitle();
        if (_items.Current is not { } path) { ShowEmpty(); EnsureShown(); return; }
        _content.Content = Centered(new QuickLookSpinner());
        var monitor = _source.MonitorOf(monitorWindow == 0 ? Handle : monitorWindow);
        var context = new QuickLookContext(_source, monitor.Scale, (int)monitor.Bounds.Width, (int)monitor.Bounds.Height, Engine,
            _controller.Settings.QuickLook.ShowFileInfoTooltip);
        var preview = Create(path, context);
        _preview = preview;
        (double Width, double Height)? size;
        try { size = await preview.MeasureAsync(token); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (revision != _revision) return;
            AppLog.Write($"QuickLook measure {preview.GetType().Name}: {error}");
            preview = Fallback(preview, context);
            size = await preview.MeasureAsync(token);
        }
        if (revision != _revision) return;
        Place(monitor, size, preview.EffectivePixels);
        EnsureShown();
        try { await preview.LoadAsync(token); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (revision != _revision) return;
            AppLog.Write($"QuickLook load {preview.GetType().Name}: {error}");
            if (preview is QuickLookInfo) { ShowError(); return; }
            preview = Fallback(preview, context);
            Place(monitor, await preview.MeasureAsync(token), preview.EffectivePixels);
            try { await preview.LoadAsync(token); }
            catch (Exception second) when (second is not OperationCanceledException) { if (revision == _revision) ShowError(); return; }
        }
        if (revision != _revision) return;
        if (preview.HasTooltip && context.Tooltips) SetTooltip(preview.View, path);
        _content.Content = preview.View;
    }

    private QuickLookPreview Create(string path, QuickLookContext context)
    {
        try { return QuickLookPreview.Create(path, context); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new QuickLookInfo(path, context); }
    }

    // Peek falls back to the file-information page when a previewer fails.
    private QuickLookPreview Fallback(QuickLookPreview failed, QuickLookContext context)
    {
        failed.Dispose();
        var info = new QuickLookInfo(failed.Path, context);
        _preview = info;
        return info;
    }

    private Task<LibVLC> Engine() => _engineTask ??= Task.Run(() => _engine = MediaRuntime.Create());

    // Peek's window size: the preview's request fitted within 80% of the monitor, centred on it. A pinned window stays put.
    private void Place(QuickLookMonitor monitor, (double Width, double Height)? requested, bool effectivePixels)
    {
        if (_pinned && _placed) return;
        var (width, height) = QuickLookPolicy.WindowSize(requested, effectivePixels, monitor.Bounds.Width, monitor.Bounds.Height, monitor.Scale);
        width = Math.Max(width, MinWidth); height = Math.Max(height, MinHeight);
        var pixelWidth = width * monitor.Scale; var pixelHeight = height * monitor.Scale;
        var placement = new DesktopPlacement(monitor.Bounds.Left + (monitor.Bounds.Width - pixelWidth) / 2,
            monitor.Bounds.Top + (monitor.Bounds.Height - pixelHeight) / 2, pixelWidth, pixelHeight);
        _placement = placement;
        // A minimized window keeps its size for later; it is applied when EnsureShown restores the window.
        if (WindowState == WindowState.Minimized) return;
        Apply(placement);
    }

    private void Apply(DesktopPlacement placement)
    {
        var handle = Handle;
        if (handle == 0) return;
        // Moving to a display with another scale resizes the window once more; the second call restores the size.
        _controller.Platform.ResizeWindow(handle, placement);
        _controller.Platform.ResizeWindow(handle, placement);
        _placed = true;
    }

    private void EnsureShown()
    {
        // Avalonia shows a hidden window in the state it was left in. A preview minimized from the taskbar or by
        // Win+D would otherwise open minimized every time, appearing for a moment and shrinking away.
        var restore = WindowState != WindowState.Normal;
        if (restore) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        if (restore && _placement is { } placement) Apply(placement);
        _source.PreviewWindow = Handle;
        _source.BringToForeground(Handle);
        Activate();
    }

    internal void Dismiss()
    {
        _folderRead?.Cancel(); _loading?.Cancel(); _revision++;
        // Hiding releases the preview so the file is not held open.
        ClearPreview();
        CloseInfoBar(keepOpen: true);
        if (IsVisible) Hide();
    }

    private void ClearPreview()
    {
        _content.Content = null;
        var preview = _preview; _preview = null;
        preview?.Dispose();
    }

    private void UpdateTitle()
    {
        var path = _items.Current;
        var name = path is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } file ? file : path;
        Title = QuickLookPolicy.WindowTitle(name);
        _name.Text = name ?? "";
        _counter.Text = QuickLookPolicy.Counter(_items.Index, _items.Count);
        _counter.IsVisible = _items.Count > 1;
        _app = path is null || Directory.Exists(path) ? null : _source.DefaultAppName(path);
        _openText.Text = _app is null ? "" : QuickLookPolicy.OpenWith(_app);
        var tip = _app is null ? null : $"{QuickLookPolicy.OpenWith(_app)} (Enter)";
        ToolTip.SetTip(_open, tip); AutomationProperties.SetName(_open, tip ?? "打开");
        Adapt();
    }

    // Peek's adaptive title bar: text at 560 epx and wider, icon only down to 340, hidden below.
    private void Adapt()
    {
        var width = Bounds.Width > 0 ? Bounds.Width : 680;
        _open.IsVisible = _app is not null && width >= 340;
        _openText.IsVisible = width >= 560;
    }

    private void TogglePin()
    {
        _pinned = !_pinned;
        _pin.Content = new LineIcon(_pinned ? IconKind.Unpin : IconKind.Pin, brush: Strong) { Width = 16, Height = 16 };
        var tip = _pinned ? "取消固定窗口" : "将窗口固定为当前大小";
        ToolTip.SetTip(_pin, tip); AutomationProperties.SetName(_pin, tip);
    }

    private void OpenCurrent()
    {
        if (_items.Current is not { } path || _app is null) return;
        _controller.Run(() => { _source.Open(path, Handle); return Task.CompletedTask; });
    }

    private void OnTunnelKey(object? sender, KeyEventArgs e)
    {
        if (_dialogOpen) return;
        var modifiers = e.KeyModifiers;
        // Peek forces these through its keyboard hook, even while the preview content has focus.
        if (e.Key == Key.Escape && modifiers == KeyModifiers.None || e.Key == Key.W && modifiers == KeyModifiers.Control) { e.Handled = true; Dismiss(); return; }
        if (modifiers == KeyModifiers.Alt && e.Key is Key.Left or Key.Right) { e.Handled = true; Navigate(e.Key == Key.Left ? -1 : 1); return; }
        if (e.Key == Key.Enter && modifiers == KeyModifiers.None) { e.Handled = true; OpenCurrent(); return; }
        if (e.Key == Key.C && modifiers == KeyModifiers.Control && FocusManager?.GetFocusedElement() is not TextBox) { e.Handled = true; _controller.Run(CopyFileAsync); }
    }

    private void OnBubbleKey(object? sender, KeyEventArgs e)
    {
        if (_dialogOpen || e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key is Key.Left or Key.Up) { e.Handled = true; Navigate(-1); }
        else if (e.Key is Key.Right or Key.Down) { e.Handled = true; Navigate(1); }
        else if (e.Key == Key.Space) { e.Handled = true; _preview?.TogglePlayback(); }
    }

    private async Task CopyFileAsync()
    {
        if (_items.Current is not { } path || Clipboard is not { } clipboard) return;
        Avalonia.Platform.Storage.IStorageItem? item = Directory.Exists(path) ? await StorageProvider.TryGetFolderFromPathAsync(new Uri(path))
            : await StorageProvider.TryGetFileFromPathAsync(new Uri(path));
        if (item is null) return;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateFile(item));
        await clipboard.SetDataAsync(data);
        await clipboard.FlushAsync();
    }

    // Peek: confirm (unless turned off), move to the Recycle Bin, then show the next item in the last direction.
    private async Task DeleteCurrentAsync()
    {
        if (_items.Current is not { } path) return;
        // Peek means to skip folders here; only files are deleted.
        if (!File.Exists(path)) return;
        _dialogOpen = true;
        try
        {
            if (_controller.Settings.QuickLook.ConfirmFileDelete)
            {
                var dialog = new QuickLookDeleteDialog();
                var answer = await dialog.ShowDialog<bool>(this);
                if (!answer) return;
                if (dialog.DontAskAgain) await _controller.SetQuickLookAsync(_controller.Settings.QuickLook with { ConfirmFileDelete = false });
            }
            _loading?.Cancel(); ClearPreview();
            var (deleted, error) = await _source.DeleteAsync(path, Handle);
            if (!deleted)
            {
                if (error is not null) ShowInfoBar($"无法删除“{Path.GetFileName(path)}”。 {error}");
                await ShowCurrentAsync(Handle);
                return;
            }
            _items.RemoveCurrent();
            await ShowCurrentAsync(Handle);
        }
        finally { _dialogOpen = false; }
    }

    private void ShowInfoBar(string message) { _infoText.Text = message; _infoBar.IsVisible = true; }

    private void CloseInfoBar() => CloseInfoBar(keepOpen: false);
    private void CloseInfoBar(bool keepOpen)
    {
        _infoBar.IsVisible = false;
        // With nothing left to preview, closing the message closes the window, as in Peek.
        if (!keepOpen && _items.Current is null) Dismiss();
    }

    private void ShowEmpty() => _content.Content = Centered(new TextBlock { Text = "没有其他要预览的文件。", FontSize = 14, Foreground = Secondary });

    private void ShowError()
    {
        ClearPreview();
        _content.Content = Centered(new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, Children =
        {
            new LineIcon(IconKind.Warn, brush: Secondary) { Width = 128, Height = 128, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = "预览此文件时出错。", FontSize = 14, Foreground = Strong, HorizontalAlignment = HorizontalAlignment.Center }
        } });
    }

    // Peek's file tooltip: one line per known value, shown below the pointer in the upper half and above it in the lower half.
    private void SetTooltip(Control view, string path)
    {
        var info = new FileInfo(path);
        var lines = new List<string> { $"文件名: {info.Name}", $"项目类型: {_source.TypeName(path, false)}" };
        if (info.Exists) { lines.Add($"修改日期: {QuickLookPreview.Date(info.LastWriteTime)}"); lines.Add($"大小: {QuickLookPolicy.ReadableSize(info.Length, false)}"); }
        ToolTip.SetTip(view, string.Join(Environment.NewLine, lines));
        ToolTip.SetPlacement(view, PlacementMode.Bottom);
        view.PointerMoved += (_, e) =>
        {
            var y = e.GetPosition(view).Y;
            ToolTip.SetPlacement(view, y < view.Bounds.Height / 2 ? PlacementMode.Pointer : PlacementMode.Top);
        };
    }

    private static Control Centered(Control child)
    {
        child.HorizontalAlignment = HorizontalAlignment.Center; child.VerticalAlignment = VerticalAlignment.Center;
        return new Panel { Children = { child } };
    }

    // Title-bar buttons never take keyboard focus, so Space and Enter stay with the preview.
    private static Button TitleButton(object content, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = content, Height = 32, MinHeight = 32, Padding = new Thickness(10, 0), Focusable = false, VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, BorderThickness = default, CornerRadius = new CornerRadius(6), VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse("#0F000000");
        button.Resources["ButtonBackgroundPressed"] = Brush.Parse("#1A000000");
        if (tooltip.Length > 0) { ToolTip.SetTip(button, tooltip); AutomationProperties.SetName(button, tooltip); }
        button.Click += (_, _) => action();
        return button;
    }
}

// Peek's delete confirmation: "删除" and the default "取消", plus "不再显示此警告".
internal sealed class QuickLookDeleteDialog : Window
{
    private readonly CheckBox _dontAsk = new() { Content = "不再显示此警告", Focusable = true };
    public bool DontAskAgain => _dontAsk.IsChecked == true;

    public QuickLookDeleteDialog()
    {
        Ui.Configure(this, "速览", 400, 180);
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false; Background = Brushes.White;
        var cancel = Ui.Button("取消", () => Close(false));
        var delete = Ui.Primary("删除", () => Close(true));
        var actions = Ui.Row(delete, cancel);
        actions.HorizontalAlignment = HorizontalAlignment.Right; actions.Margin = new Thickness(0, 18, 0, 0);
        Content = new StackPanel { Margin = new Thickness(20, 20, 20, 16), Spacing = 8, Children =
        {
            new TextBlock { Text = "是否删除文件?", FontSize = DesignTokens.SectionTitle, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#1A1B22") },
            new TextBlock { Text = "是否确定要删除此文件?", FontSize = 13, Foreground = Brush.Parse("#3B3D49"), TextWrapping = TextWrapping.Wrap },
            _dontAsk, actions
        } };
        Opened += (_, _) => cancel.Focus();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(false); e.Handled = true; } };
    }
}
