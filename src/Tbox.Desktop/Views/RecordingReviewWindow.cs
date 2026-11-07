using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
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
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, Value = 100, Width = 80 };
    private readonly TextBlock _time = Ui.Text("00:00 / 00:00", 12);
    private readonly TextBlock _status = Ui.Text("", 12);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 4, IsVisible = false };
    private readonly ComboBox _format = new() { ItemsSource = new[] { "GIF - 图像，兼容性高，质量较低", "WEBP - 图像，质量好，兼容性适中", "MP4 - 视频，体积小，质量好" }, SelectedIndex = 2, MinWidth = 300 };
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
    private bool _updating, _saving, _saved, _closing, _allowClose;
    public event Action? Released;

    public RecordingReviewWindow(DesktopController controller, RecordingWorkspace workspace, RecordingArea area, RecordingOutput output, TimeSpan duration)
    {
        _controller = controller; _workspace = workspace; _area = area; _output = output; _duration = duration;
        Ui.Configure(this, $"Tbox 录屏 · {area.PixelWidth} × {area.PixelHeight} · {FormatTime(duration)} · {(output.HasSystemAudio ? "系统声音已启用" : "静音")}", 960, 720);
        RequestedThemeVariant = ThemeVariant.Dark; Background = Brush.Parse("#202020"); MinWidth = 700; MinHeight = 450;
        _play = Ui.Button("▶", () => controller.Run(TogglePlaybackAsync), "播放 / 暂停 · Space");
        _save = Ui.Button("保存", () => controller.Run(SaveAsync), "保存录屏 · Command / Ctrl + S"); _save.Classes.Add("accent");
        _cancelExport = Ui.Button("取消导出", () => _exportCancellation?.Cancel()); _cancelExport.IsVisible = false;
        _rerecord = Ui.Button("重新录制", () => controller.Run(async () =>
        {
            if (await RequestCloseAsync()) await controller.StartRecordingAsync(area);
        }));
        _position.Maximum = Math.Max(1, duration.TotalMilliseconds);
        foreach (var name in new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
            "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed" })
            _position.Resources[name] = Brush.Parse("#F55353");
        var playback = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 12 };
        playback.Children.Add(_play); playback.Children.Add(_position); Grid.SetColumn(_position, 1);
        playback.Children.Add(_time); Grid.SetColumn(_time, 2); playback.Children.Add(_volume); Grid.SetColumn(_volume, 3);
        var exports = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 12 };
        exports.Children.Add(_format); exports.Children.Add(_cancelExport); Grid.SetColumn(_cancelExport, 1);
        exports.Children.Add(_rerecord); Grid.SetColumn(_rerecord, 2); exports.Children.Add(_save); Grid.SetColumn(_save, 3);
        var footer = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { playback, exports, _progress, _status } };
        DockPanel.SetDock(footer, Dock.Bottom);
        var canvas = new Border { Background = Brushes.Black, Child = _video };
        Content = new DockPanel { Children = { footer, canvas } };
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
        _play.Content = player.IsPlaying ? "Ⅱ" : "▶";
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
        var format = _format.SelectedIndex switch { 0 => RecordingExportFormat.Gif, 1 => RecordingExportFormat.WebP, _ => RecordingExportFormat.Mp4 };
        var profile = RecordingExportProfiles.Get(format);
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存录屏", SuggestedFileName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + profile.Extension,
                DefaultExtension = profile.Extension.TrimStart('.'), ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType(profile.DisplayName) { Patterns = ["*" + profile.Extension] }]
            });
            if (file is null) return;
            if (file.TryGetLocalPath() is not { } path) throw new InvalidOperationException("请选择本机可写入的文件位置。");
            _save.IsEnabled = false; _format.IsEnabled = false; _rerecord.IsEnabled = false;
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
            _save.IsEnabled = true; _format.IsEnabled = true; _rerecord.IsEnabled = true;
            _cancelExport.IsVisible = false; _progress.IsVisible = false;
        }
    }

    public async Task<bool> RequestCloseAsync()
    {
        if (_closeRequest is not null) return await _closeRequest;
        _closeRequest = CloseCoreAsync();
        try { return await _closeRequest; }
        finally { if (!_closing) _closeRequest = null; }
    }

    private async Task<bool> CloseCoreAsync()
    {
        if (_closing) { await _released.Task; return true; }
        if (_saving) { _exportCancellation?.Cancel(); await _saveTask; if (_closing) { await _released.Task; return true; } }
        if (!_saved && !await new ConfirmWindow("录屏尚未保存，是否放弃？", "放弃录屏").ShowDialog<bool>(this)) return false;
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
