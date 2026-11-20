using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
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
    private readonly VideoSize _source;
    private readonly VideoView _video = new();
    private readonly RecordingTrimBar _track = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, Value = 100, Width = 80, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new() { Text = "00:00 / 00:00", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#5F6270"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _range = new() { FontSize = DesignTokens.Caption, Foreground = Brush.Parse("#5F6270"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = Ui.Description("");
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6, Width = 140, CornerRadius = new CornerRadius(3), IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    // Format choice is a segmented control (spec ④); MP4 first because it is the default.
    internal static readonly RecordingExportFormat[] Formats = [RecordingExportFormat.Mp4, RecordingExportFormat.Gif, RecordingExportFormat.WebP, RecordingExportFormat.ContactSheet];
    private static readonly string[] FormatNames = ["MP4", "GIF", "WebP", "六宫格"];
    private readonly Func<int> _formatIndex;
    private readonly Action<bool> _enableFormat;
    private readonly Control _frameRateGroup, _customGroup;
    private readonly TextBlock _customHeight = new() { FontSize = 13, Foreground = Brush.Parse("#5F6270"), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _play, _save, _cancelExport, _rerecord, _resetRange;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Size presets for the current format; the extra last item is "自定义" (width input).
    private IReadOnlyList<RecordingSizeChoice> _sizes = [];
    private LibVLC? _engine;
    private MediaPlayer? _player;
    private LibVLCSharp.Shared.Media? _media;
    private CancellationTokenSource? _exportCancellation;
    private Task _saveTask = Task.CompletedTask;
    private Task _initialization = Task.CompletedTask;
    private Task<bool>? _closeRequest;
    private ConfirmWindow? _discardConfirmation;
    private bool _saving, _selectingSavePath, _saved, _closing, _allowClose, _discardUnsaved;
    public event Action? Released;

    internal RecordingTrimBar Track => _track;
    internal ComboBox FrameRate { get; } = new() { MinWidth = 0, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        ItemsSource = RecordingExportProfiles.FrameRates.Select(rate => rate + " FPS").ToArray() };
    internal ComboBox Size { get; } = new() { MinWidth = 132, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    internal TextBox CustomWidth { get; } = new() { Width = 64, MinWidth = 0, Height = 30, MinHeight = 30, FontSize = 13, MaxLength = 5,
        VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0) };
    internal RecordingExportFormat Format => Formats[_formatIndex()];

    public RecordingReviewWindow(DesktopController controller, RecordingWorkspace workspace, RecordingArea area, RecordingOutput output, TimeSpan duration)
    {
        _controller = controller; _workspace = workspace; _area = area; _output = output; _duration = duration;
        _source = new VideoSize(area.PixelWidth, area.PixelHeight);
        Ui.Configure(this, "录屏 · Tbox", 900, 680);
        MinWidth = 760; MinHeight = 500;
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
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
        {
            _volume.Resources["SliderThumbBackground" + state] = Brushes.White;
            _volume.Resources["SliderTrackFill" + state] = Brush.Parse("#E6E4EE");
        }
        _track.Duration = duration;
        var speaker = new LineIcon(output.HasSystemAudio ? IconKind.Speaker : IconKind.Mute, brush: Brush.Parse("#5F6270")) { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        var playback = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 12, Margin = new Thickness(20, 4, 20, 0), Children =
        {
            _play, Ui.Column(_track, 1), Ui.Column(_time, 2), Ui.Column(speaker, 3), Ui.Column(_volume, 4)
        } };
        // Range line under the track (PixPin: drag the handles at both ends; only the part between them is exported).
        _resetRange = Ui.Link("恢复全部", () => _track.Range = new(TimeSpan.Zero, _track.Duration));
        var rangeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(64, 0, 20, 0), Height = 22,
            Children = { _range, _resetRange } };

        var format = Ui.Segmented(FormatNames, 0, _ => { ApplyFormatDefaults(); UpdateRange(); }, out _formatIndex, out _enableFormat);
        var chips = format.GetLogicalDescendants().OfType<Button>().ToArray();
        for (var i = 0; i < chips.Length; i++) ToolTip.SetTip(chips[i], RecordingExportProfiles.Get(Formats[i]).Description);
        AutomationProperties.SetName(FrameRate, "导出帧率"); AutomationProperties.SetName(Size, "导出尺寸"); AutomationProperties.SetName(CustomWidth, "自定义宽度");
        _frameRateGroup = Group("帧率", FrameRate);
        _customGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
            Children = { CustomWidth, _customHeight } };
        var sizeGroup = Group("尺寸", Size, _customGroup);
        Size.SelectionChanged += (_, _) => { _customGroup.IsVisible = Size.SelectedIndex == _sizes.Count; UpdateCustomHeight(); };
        CustomWidth.TextChanged += (_, _) => UpdateCustomHeight();
        _track.RangeChanged += _ => UpdateRange();
        _track.Seeked += time => { if (!_closing && _player is { } player) player.Time = (long)time.TotalMilliseconds; UpdateTime(time); };
        _status.FontSize = DesignTokens.Caption;
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { format, _frameRateGroup, sizeGroup, _progress, _status } };
        _status.VerticalAlignment = VerticalAlignment.Center; _status.TextWrapping = TextWrapping.NoWrap; _status.TextTrimming = TextTrimming.CharacterEllipsis;
        var bar = Ui.ResultBar(left, _cancelExport, _save);
        ApplyFormatDefaults(); UpdateRange();

        var size = $"{area.PixelWidth} × {area.PixelHeight} · {FormatTime(duration)} · {RecordingFrameRatePolicy.RecordingFrameRate} FPS · {(output.HasSystemAudio ? "系统声音" : "静音")}";
        var header = Ui.ResultHeader(IconKind.Record, ToolTone.Red, "录屏预览", Ui.Description(size), _rerecord);
        var strip = Ui.TitleStrip("录屏 · Tbox", brand);
        var canvas = new Border { Background = Brush.Parse("#111217"), CornerRadius = new CornerRadius(12), Margin = new Thickness(12, 0), ClipToBounds = true, Child = _video };
        DockPanel.SetDock(strip, Dock.Top); DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom);
        DockPanel.SetDock(rangeRow, Dock.Bottom); DockPanel.SetDock(playback, Dock.Bottom);
        Content = new DockPanel { Children = { strip, header, bar, rangeRow, playback, canvas } };
        Closed += (_, _) => brand.Dispose();
        _play.IsEnabled = false;
        Opened += (_, _) => _initialization = InitializePlaybackAsync();
        _timer.Tick += (_, _) => UpdatePlayback();
        _volume.ValueChanged += (_, _) => { if (!_closing && _player is { } player) player.Volume = (int)_volume.Value; };
        KeyDown += (_, e) =>
        {
            if (e.Source is TextBox) return;
            if (e.Key == Key.Space) controller.Run(TogglePlaybackAsync);
            else if (e.Key == Key.S && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) controller.Run(SaveAsync);
            else if (e.Key == Key.Escape) controller.Run(async () => { await RequestCloseAsync(); });
            else return;
            e.Handled = true;
        };
        Closing += (_, e) => { if (!_allowClose) { e.Cancel = true; controller.Run(async () => { await RequestCloseAsync(); }); } };
        Closed += (_, _) => _closed.TrySetResult();
    }

    private static StackPanel Group(string label, params Control[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = label, FontSize = DesignTokens.Caption, Foreground = Brush.Parse("#5F6270"), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.AddRange(items);
        return panel;
    }

    private bool Even => Format == RecordingExportFormat.Mp4;

    // Every format starts from its own defaults; nothing is remembered between recordings.
    private void ApplyFormatDefaults()
    {
        var format = Format; var profile = RecordingExportProfiles.Get(format);
        _frameRateGroup.IsVisible = format != RecordingExportFormat.ContactSheet;
        FrameRate.SelectedIndex = Math.Max(0, RecordingExportProfiles.FrameRates.ToList().IndexOf(profile.DefaultFramesPerSecond));
        if (format == RecordingExportFormat.ContactSheet)
        {
            // Each cell is limited by its long edge; "原尺寸" keeps the recorded pixels.
            _sizes = new[] { new RecordingSizeChoice("原尺寸", _source) }.Concat(ContactSheetPolicy.LongEdges
                .Where(edge => edge < Math.Max(_source.Width, _source.Height))
                .Select(edge => new RecordingSizeChoice($"每格 ≤ {edge} px", RecordingSizePolicy.FitLongEdge(_source, edge, false)))).ToArray();
            var fallback = _sizes.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Label == $"每格 ≤ {ContactSheetPolicy.DefaultLongEdge} px");
            Fill(fallback.c.Label is null ? 0 : fallback.i);
        }
        else
        {
            _sizes = RecordingSizePolicy.Choices(_source, Even);
            Fill(RecordingSizePolicy.DefaultChoice(_source, format, Even));
        }
        CustomWidth.Text = OutputSize(Size.SelectedIndex).Width.ToString();
    }

    private void Fill(int selected)
    {
        var sheet = Format == RecordingExportFormat.ContactSheet;
        var items = _sizes.Select(c =>
        {
            var shown = sheet ? ContactSheetPolicy.SheetSize(c.Size) : c.Size;
            return $"{c.Label}   {shown.Width} × {shown.Height}";
        }).Append("自定义").ToArray();
        Size.ItemsSource = items; Size.SelectedIndex = selected;
    }

    private VideoSize OutputSize(int index)
    {
        if (index >= 0 && index < _sizes.Count) return _sizes[index].Size;
        return RecordingSizePolicy.ScaleToWidth(_source, int.TryParse(CustomWidth.Text, out var width) && width > 0 ? width : _source.Width, Even);
    }

    private void UpdateCustomHeight()
    {
        if (!_customGroup.IsVisible) return;
        var size = OutputSize(-1);
        _customHeight.Text = Format == RecordingExportFormat.ContactSheet
            ? $"× {size.Height}（输出 {ContactSheetPolicy.SheetSize(size).Width} × {ContactSheetPolicy.SheetSize(size).Height}）"
            : $"× {size.Height}";
    }

    internal RecordingExportOptions Options()
    {
        var format = Format;
        var fps = format == RecordingExportFormat.ContactSheet ? 0 : RecordingExportProfiles.FrameRates[Math.Max(0, FrameRate.SelectedIndex)];
        return new RecordingExportOptions(format, fps, OutputSize(Size.SelectedIndex), _track.Range);
    }

    private void UpdateRange()
    {
        var range = _track.Range; var full = range.IsFull(_track.Duration);
        _range.Text = full ? $"导出全部 · {RecordingTrimBar.FormatPrecise(range.Start)} – {RecordingTrimBar.FormatPrecise(range.End)}　拖动两端红色手柄截取"
            : $"截取 {RecordingTrimBar.FormatPrecise(range.Start)} – {RecordingTrimBar.FormatPrecise(range.End)} · 共 {RecordingTrimBar.FormatPrecise(range.Length)}";
        _resetRange.IsVisible = !full;
        _track.Ticks = Format == RecordingExportFormat.ContactSheet ? ContactSheetPolicy.SampleTimes(range, RecordingFrameRatePolicy.RecordingFrameRate) : [];
        if (_player?.IsPlaying == true) return;
        // A paused playhead outside the handles jumps to the start handle.
        var now = _player is { } player ? TimeSpan.FromMilliseconds(Math.Max(0, player.Time)) : _track.Position;
        if (now < range.Start || now > range.End)
        {
            if (_player is { } paused) paused.Time = (long)range.Start.TotalMilliseconds;
            now = range.Start;
        }
        _track.Position = now; UpdateTime(now);
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
            _play.IsEnabled = false; _volume.IsEnabled = false;
        }
    }

    private async Task TogglePlaybackAsync()
    {
        if (_closing || _player is not { } player) return;
        if (player.IsPlaying) player.Pause();
        else
        {
            var range = _track.Range;
            var restart = player.State == VLCState.Ended || player.Time < (long)range.Start.TotalMilliseconds ||
                player.Time >= (long)range.End.TotalMilliseconds - 50;
            if (player.State == VLCState.Ended) await Task.Run(player.Stop);
            if (_closing) return;
            player.Play();
            // Playback stays between the handles: once the end handle is reached, play again from the start handle.
            if (restart) player.Time = (long)range.Start.TotalMilliseconds;
        }
        UpdatePlayback();
    }

    private void UpdatePlayback()
    {
        if (_closing || _player is not { } player) return;
        if (player.Length > 0 && Math.Abs(player.Length - _track.Duration.TotalMilliseconds) > 1)
        { _track.Duration = TimeSpan.FromMilliseconds(player.Length); UpdateRange(); }
        var now = TimeSpan.FromMilliseconds(Math.Max(0, player.Time));
        if (player.IsPlaying && now >= _track.Range.End && !_track.Range.IsFull(_track.Duration)) { player.Pause(); now = _track.Range.End; }
        if (!_track.IsDragging) _track.Position = now;
        UpdateTime(now);
        _play.Content = new LineIcon(player.IsPlaying ? IconKind.Pause : IconKind.Play, brush: Brush.Parse("#1A1B22")) { Width = 18, Height = 18 };
    }

    private void UpdateTime(TimeSpan now) => _time.Text = $"{FormatTime(now)} / {FormatTime(_track.Duration)}";

    private async Task SaveAsync()
    {
        if (_saving || _closing) return;
        _saveTask = SaveCoreAsync(); await _saveTask;
        if (_saved) await RequestCloseAsync();
    }

    private void SetEditable(bool on)
    {
        _save.IsEnabled = on; _enableFormat(on); _rerecord.IsEnabled = on; _track.IsHitTestVisible = on; _resetRange.IsEnabled = on;
        FrameRate.IsEnabled = Size.IsEnabled = CustomWidth.IsEnabled = on;
    }

    private async Task SaveCoreAsync()
    {
        _saving = true; _exportCancellation = new CancellationTokenSource();
        var options = Options();
        var profile = RecordingExportProfiles.Get(options.Format);
        try
        {
            IStorageFile? file;
            _selectingSavePath = true;
            try
            {
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "保存录屏",
                    SuggestedFileName = (options.Format == RecordingExportFormat.ContactSheet ? stamp + "_六宫格" : stamp) + profile.Extension,
                    DefaultExtension = profile.Extension.TrimStart('.'), ShowOverwritePrompt = true,
                    FileTypeChoices = [new FilePickerFileType(profile.DisplayName) { Patterns = ["*" + profile.Extension] }]
                });
            }
            finally { _selectingSavePath = false; }
            _exportCancellation.Token.ThrowIfCancellationRequested();
            if (file is null) return;
            if (file.TryGetLocalPath() is not { } path) throw new InvalidOperationException("请选择本机可写入的文件位置。");
            SetEditable(false);
            _cancelExport.IsVisible = true; _progress.IsVisible = true; _progress.Value = 0;
            var detail = options.Format == RecordingExportFormat.ContactSheet ? $"{ContactSheetPolicy.SheetSize(options.Size).Width} × {ContactSheetPolicy.SheetSize(options.Size).Height}"
                : $"{options.Size.Width} × {options.Size.Height} · {options.FramesPerSecond} FPS";
            _status.Text = $"正在保存 {FormatNames[_formatIndex()]} · {detail}";
            var exported = await RecordingExporter.ExportAsync(_output.FilePath, _workspace.ExportPath(options.Format), options, _source, _track.Duration,
                new Progress<double>(value => _progress.Value = value), _exportCancellation.Token);
            await RecordingExporter.SaveAtomicallyAsync(exported, path, _exportCancellation.Token);
            _saved = true;
        }
        catch (OperationCanceledException) { _status.Text = "已取消导出，母版保留。"; }
        catch (Exception error) { _status.Text = error.Message; }
        finally
        {
            _saving = false; _exportCancellation.Dispose(); _exportCancellation = null;
            SetEditable(true);
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
