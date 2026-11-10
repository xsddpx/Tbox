using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Views;

internal sealed class RecordingReviewWindow : Window
{
    private readonly DesktopController _controller;
    private readonly RecordingWorkspace _workspace;
    private readonly RecordingArea _area;
    private readonly RecordingOutput _output;
    private readonly TimeSpan _duration;
    private readonly VideoView _video = new();
    private readonly Slider _position = new() { Minimum = 0, MinWidth = 100, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, Value = 100, Width = 80, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new() { Text = "00:00 / 00:00", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#5F6270"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = Ui.Description("");
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6, Width = 140, CornerRadius = new CornerRadius(3), IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    // Format choice is a segmented control (spec ④); MP4 first because it is the default.
    private static readonly RecordingExportFormat[] Formats = [RecordingExportFormat.Mp4, RecordingExportFormat.Gif, RecordingExportFormat.WebP];
    private static readonly string[] FormatHints = ["视频，体积小，质量好", "图像，兼容性高，质量较低", "图像，质量好，兼容性适中"];
    private readonly Func<int> _formatIndex;
    private readonly Action<bool> _enableFormat;
    private readonly Button _play, _save, _cancelExport, _rerecord;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LibVLC? _engine;
    private MediaPlayer? _player;
    private LibVLCSharp.Shared.Media? _media;
    private CancellationTokenSource? _exportCancellation;
    private Task _saveTask = Task.CompletedTask;
    private Task _initialization = Task.CompletedTask;
    private Task<bool>? _closeRequest;
    private ConfirmWindow? _discardConfirmation;
    private bool _updating, _saving, _selectingSavePath, _saved, _closing, _allowClose, _discardUnsaved;
    public event Action? Released;

    public RecordingReviewWindow(DesktopController controller, RecordingWorkspace workspace, RecordingArea area, RecordingOutput output, TimeSpan duration)
    {
        _controller = controller; _workspace = workspace; _area = area; _output = output; _duration = duration;
        Ui.Configure(this, "录屏 · Tbox", 900, 660);
        MinWidth = 700; MinHeight = 480;
        Ui.ExtendIntoTitleBar(this);
        var brand = Ui.Brand();
        // Only the video canvas stays dark; the frame is the light result-window frame.
        _play = Ui.IconButton(IconKind.Play, "播放 / 暂停 · Space", () => controller.Run(TogglePlaybackAsync));
        _play.Background = Brushes.White; _play.BorderBrush = Brush.Parse("#1F101220"); _play.BorderThickness = new Thickness(1);
        _save = Ui.Primary("保存…", () => controller.Run(SaveAsync), "保存录屏 · Command / Ctrl + S");
        _cancelExport = Ui.Button("取消导出", () => _exportCancellation?.Cancel()); _cancelExport.IsVisible = false;
        _rerecord = Ui.Button("重新录制", () => controller.Run(async () =>
        {
            if (await RequestCloseAsync()) await controller.StartRecordingAsync(area);
        }));
        _position.Maximum = Math.Max(1, duration.TotalMilliseconds);
        foreach (var name in new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed" })
            _position.Resources[name] = Ui.RecordRed;
        foreach (var slider in new[] { _position, _volume })
            foreach (var state in new[] { "", "PointerOver", "Pressed" })
            {
                slider.Resources["SliderThumbBackground" + state] = Brushes.White;
                slider.Resources["SliderTrackFill" + state] = Brush.Parse("#E6E4EE");
            }
        var speaker = new LineIcon(output.HasSystemAudio ? IconKind.Speaker : IconKind.Mute, brush: Brush.Parse("#5F6270")) { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        var playback = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 12, Margin = new Thickness(20, 12, 20, 4), Children =
        {
            _play, Ui.Column(_position, 1), Ui.Column(_time, 2), Ui.Column(speaker, 3), Ui.Column(_volume, 4)
        } };
        var hint = Ui.Description(FormatHints[0]); hint.FontSize = DesignTokens.Caption; hint.VerticalAlignment = VerticalAlignment.Center;
        var format = Ui.Segmented(["MP4", "GIF", "WebP"], 0, index => hint.Text = FormatHints[index], out _formatIndex, out _enableFormat);
        _status.FontSize = DesignTokens.Caption;
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { format, hint, _progress, _status } };
        _status.VerticalAlignment = VerticalAlignment.Center; _status.TextWrapping = TextWrapping.NoWrap; _status.TextTrimming = TextTrimming.CharacterEllipsis;
        var bar = Ui.ResultBar(left, _cancelExport, _save);

        var size = $"{area.PixelWidth} × {area.PixelHeight} · {FormatTime(duration)} · {(output.HasSystemAudio ? "系统声音" : "静音")}";
        var header = Ui.ResultHeader(IconKind.Record, ToolTone.Red, "录屏预览", Ui.Description(size), _rerecord);
        var strip = Ui.TitleStrip("录屏 · Tbox", brand);
        var canvas = new Border { Background = Brush.Parse("#111217"), CornerRadius = new CornerRadius(12), Margin = new Thickness(12, 0), ClipToBounds = true, Child = _video };
        DockPanel.SetDock(strip, Dock.Top); DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom); DockPanel.SetDock(playback, Dock.Bottom);
        Content = new DockPanel { Children = { strip, header, bar, playback, canvas } };
        Closed += (_, _) => brand.Dispose();
        _play.IsEnabled = false;
        Opened += (_, _) => _initialization = InitializePlaybackAsync();
        _timer.Tick += (_, _) => UpdatePlayback();
        _position.ValueChanged += (_, _) => { if (!_updating && !_closing && _player is { } player) player.Time = (long)_position.Value; };
        _volume.ValueChanged += (_, _) => { if (!_closing && _player is { } player) player.Volume = (int)_volume.Value; };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) controller.Run(TogglePlaybackAsync);
            else if (e.Key == Key.S && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) controller.Run(SaveAsync);
            else if (e.Key == Key.Escape) controller.Run(async () => { await RequestCloseAsync(); });
            else return;
            e.Handled = true;
        };
        Closing += (_, e) => { if (!_allowClose) { e.Cancel = true; controller.Run(async () => { await RequestCloseAsync(); }); } };
        Closed += (_, _) => _closed.TrySetResult();
    }

    private async Task InitializePlaybackAsync()
    {
        _status.Text = "正在加载回放…";
        try
        {
            _engine = await Task.Run(MediaRuntime.Create);
            if (_closing) return;
            _media = new LibVLCSharp.Shared.Media(_engine, _output.FilePath, FromType.FromPath);
            _player = new MediaPlayer(_engine); _video.MediaPlayer = _player;
            _player.EncounteredError += (_, _) => Dispatcher.UIThread.Post(() => _status.Text = "回放失败，母版保留，可尝试保存后用系统播放器打开。");
            _player.Play(_media); _play.IsEnabled = true; _timer.Start(); _status.Text = "";
        }
        catch (Exception error)
        {
            _status.Text = "回放组件不可用，仍可保存录屏：" + error.Message;
            _play.IsEnabled = false; _position.IsEnabled = false; _volume.IsEnabled = false;
        }
    }

    private async Task TogglePlaybackAsync()
    {
        if (_closing || _player is not { } player) return;
        if (player.IsPlaying) player.Pause();
        else { if (player.State == VLCState.Ended) await Task.Run(player.Stop); if (!_closing) player.Play(); }
        UpdatePlayback();
    }

    private void UpdatePlayback()
    {
        if (_closing || _player is not { } player) return;
        var total = player.Length > 0 ? player.Length : _duration.TotalMilliseconds;
        _updating = true; _position.Maximum = Math.Max(1, total); _position.Value = Math.Max(0, player.Time); _updating = false;
        _time.Text = $"{FormatTime(TimeSpan.FromMilliseconds(Math.Max(0, player.Time)))} / {FormatTime(TimeSpan.FromMilliseconds(total))}";
        _play.Content = new LineIcon(player.IsPlaying ? IconKind.Pause : IconKind.Play, brush: Brush.Parse("#1A1B22")) { Width = 18, Height = 18 };
    }

    private async Task SaveAsync()
    {
        if (_saving || _closing) return;
        _saveTask = SaveCoreAsync(); await _saveTask;
        if (_saved) await RequestCloseAsync();
    }

    private async Task SaveCoreAsync()
    {
        _saving = true; _exportCancellation = new CancellationTokenSource();
        var format = Formats[_formatIndex()];
        var profile = RecordingExportProfiles.Get(format);
        try
        {
            IStorageFile? file;
            _selectingSavePath = true;
            try
            {
                file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "保存录屏", SuggestedFileName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + profile.Extension,
                    DefaultExtension = profile.Extension.TrimStart('.'), ShowOverwritePrompt = true,
                    FileTypeChoices = [new FilePickerFileType(profile.DisplayName) { Patterns = ["*" + profile.Extension] }]
                });
            }
            finally { _selectingSavePath = false; }
            _exportCancellation.Token.ThrowIfCancellationRequested();
            if (file is null) return;
            if (file.TryGetLocalPath() is not { } path) throw new InvalidOperationException("请选择本机可写入的文件位置。");
            _save.IsEnabled = false; _enableFormat(false); _rerecord.IsEnabled = false;
            _cancelExport.IsVisible = true; _progress.IsVisible = true; _progress.Value = 0;
            _status.Text = format == RecordingExportFormat.Mp4 ? "正在保存…" : "正在转换图像…";
            var exported = await RecordingExporter.ExportAsync(_output.FilePath, _workspace.ExportPath(format), format, _duration,
                new Progress<double>(value => _progress.Value = value), _exportCancellation.Token);
            await RecordingExporter.SaveAtomicallyAsync(exported, path, _exportCancellation.Token);
            _saved = true;
        }
        catch (OperationCanceledException) { _status.Text = "已取消导出，母版保留。"; }
        catch (Exception error) { _status.Text = error.Message; }
        finally
        {
            _saving = false; _exportCancellation.Dispose(); _exportCancellation = null;
            _save.IsEnabled = true; _enableFormat(true); _rerecord.IsEnabled = true;
            _cancelExport.IsVisible = false; _progress.IsVisible = false;
        }
    }

    public async Task<bool> RequestCloseAsync(bool discardUnsaved = false)
    {
        if (discardUnsaved)
        {
            _discardUnsaved = true;
            _discardConfirmation?.Close(true);
        }
        if (_closeRequest is not null) return await _closeRequest;
        _closeRequest = CloseCoreAsync();
        try { return await _closeRequest; }
        finally { if (!_closing) _closeRequest = null; }
    }

    private async Task<bool> CloseCoreAsync()
    {
        if (_closing) { await _released.Task; return true; }
        if (_saving)
        {
            _exportCancellation?.Cancel();
            // A native picker can stay open until its owner closes. A cancelled picker cannot start an export.
            if (!_selectingSavePath) await _saveTask;
            if (_closing) { await _released.Task; return true; }
        }
        if (!_saved && !_discardUnsaved)
        {
            _discardConfirmation = new ConfirmWindow("录屏尚未保存，是否放弃？", "放弃录屏", "放弃后本次录制的内容会被删除，无法恢复。", destructive: true);
            try { if (!await _discardConfirmation.ShowDialog<bool>(this)) return false; }
            finally { _discardConfirmation = null; }
        }
        if (_closing) { await _released.Task; return true; }
        _closing = true; _timer.Stop();
        try
        {
            await _initialization;
            if (_player is { } player)
            {
                try { await Task.Run(player.Stop); }
                catch (Exception error) { _controller.Main.SetStatus("回放停止异常：" + error.Message); }
            }
            _video.MediaPlayer = null;
            if (_player is not null) _player.Media = null;
            _allowClose = true; Close(); await _closed.Task;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Run(() => { _player?.Dispose(); _media?.Dispose(); _engine?.Dispose(); });
            _workspace.Dispose();
        }
        // If native release fails, leave the session for startup cleanup rather than remove an in-use file.
        finally { _released.TrySetResult(); Released?.Invoke(); }
        return true;
    }

    internal static string FormatTime(TimeSpan duration) => duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"mm\:ss");
}
