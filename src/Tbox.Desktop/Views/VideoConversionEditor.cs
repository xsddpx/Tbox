using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Views;

internal sealed class VideoConversionEditor : UserControl
{
    private readonly DesktopController _controller;
    private readonly RecordingWorkspace _workspace;
    private readonly VideoConversionSource _mediaSource;
    private VideoSize Source => _mediaSource.Size;
    private readonly Func<Task> _savedRecording;
    private readonly VideoView _video = new();
    private readonly Image _scrubImage = new() { IsVisible = false, Stretch = Stretch.Uniform };
    private readonly RecordingTrimBar _track = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, Value = 100, Width = 80, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new() { FontSize = 12, FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") },
        FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#5F6270"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _recordingDetails = Ui.Description("");
    private readonly Grid _ruler = new() { Margin = new Thickness(5, 0) };
    private TimeSpan _rulerDuration;
    private readonly TextBlock _range = new() { FontSize = DesignTokens.Caption, Foreground = Brush.Parse("#5F6270"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = Ui.Description("");
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6, Width = 140, CornerRadius = new CornerRadius(3), IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    // Format choice is a segmented control (导出格式); MP4 first because it is the default.
    internal static readonly RecordingExportFormat[] Formats = [RecordingExportFormat.Mp4, RecordingExportFormat.Gif, RecordingExportFormat.WebP, RecordingExportFormat.ContactSheet];
    private static readonly string[] FormatNames = ["MP4", "GIF", "WebP", "宫格图"];
    private readonly Func<int> _formatIndex;
    private readonly Action<bool> _enableFormat;
    private readonly Control _frameRateGroup, _customGroup;
    private readonly TextBlock _presetDimensions = Ui.Description("");
    private readonly TextBlock _customHeight = new() { FontSize = 13, Foreground = Brush.Parse("#5F6270"), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _play, _save, _cancelExport, _resetRange;
    private readonly Border _countGroup, _gridPreview;
    private readonly TextBlock _gridLayout = Ui.Description("");
    private readonly WrapPanel _gridCells = new() { Orientation = Orientation.Horizontal };
    private readonly List<Image> _gridImages = [];
    private readonly TextBlock _outputType = Ui.Description("MP4");
    private readonly NumericUpDown _count = new() { Minimum = ContactSheetPolicy.MinCount, Maximum = ContactSheetPolicy.MaxCount,
        Value = ContactSheetPolicy.DefaultCount, Increment = 1, FormatString = "0", ClipValueToMinMax = true, MinHeight = 34 };
    private int FrameCount => (int)(_count.Value ?? ContactSheetPolicy.DefaultCount);
    private Task _thumbnailTask = Task.CompletedTask;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Size presets for the current format; the extra last item is "自定义" (width input).
    private IReadOnlyList<RecordingSizeChoice> _sizes = [];
    private LibVLC? _engine;
    private MediaPlayer? _player;
    private LibVLCSharp.Shared.Media? _media;
    private CancellationTokenSource? _exportCancellation;
    private readonly CancellationTokenSource _scrubCancellation = new();
    private Task _scrubTask = Task.CompletedTask;
    private Picture? _scrubFrame;
    private TimeSpan? _scrubPosition, _resumeFrom;
    private Task _restartTask = Task.CompletedTask;
    // Contact-sheet sample thumbnails keyed by time; a new range cancels the batch still decoding.
    private readonly Dictionary<TimeSpan, Picture> _thumbnails = [];
    private CancellationTokenSource? _thumbnailLoad;
    private IReadOnlyList<TimeSpan> _thumbnailTicks = [];
    // Card above a contact-sheet node: the sample frame (grey until decoded) and "04 · 00:03.2".
    private readonly Image _thumbnailImage = new() { Height = 72, Stretch = Stretch.Uniform };
    private readonly Border _thumbnailFrame = new() { Height = 72, MinWidth = 96, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = Brush.Parse("#2A2B33") };
    private readonly TextBlock _thumbnailLabel = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
    internal Popup ThumbnailPopup { get; }
    internal int LoadedThumbnails => _track.Ticks.Count(_thumbnails.ContainsKey);
    private Task _saveTask = Task.CompletedTask;
    private Task _initialization = Task.CompletedTask;
    private ConfirmWindow? _discardConfirmation;
    private bool _saving, _selectingSavePath, _saved, _closing, _started;
    private Task<bool>? _discardTask;
    internal event Action<bool>? BusyChanged;

    internal RecordingTrimBar Track => _track;
    internal ComboBox FrameRate { get; } = new() { MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        ItemsSource = RecordingExportProfiles.FrameRates.Select(rate => rate + " FPS").ToArray() };
    internal ComboBox Size { get; } = new() { MinWidth = 132, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    internal TextBox CustomWidth { get; } = new() { Width = 64, MinWidth = 0, Height = 30, MinHeight = 30, FontSize = 13, MaxLength = 5,
        VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0) };
    internal RecordingExportFormat Format => Formats[_formatIndex()];

    public VideoConversionEditor(DesktopController controller, RecordingWorkspace workspace, VideoConversionSource source, Action remove, Func<Task> savedRecording)
    {
        _controller = controller; _workspace = workspace; _mediaSource = source; _savedRecording = savedRecording;
        AutomationProperties.SetName(_scrubImage, "录屏定位画面");
        // Only the video canvas stays dark; the frame is the light result-window frame.
        _play = Ui.IconButton(IconKind.Play, "播放 / 暂停 · Space", () => controller.Run(TogglePlaybackAsync));
        _play.Background = Brushes.White; _play.BorderBrush = Brush.Parse("#1F101220"); _play.BorderThickness = new Thickness(1);
        _save = Ui.Primary("保存…", () => controller.Run(SaveAsync), "保存转换结果 · Command / Ctrl + S");
        _cancelExport = Ui.Button("取消导出", () => _exportCancellation?.Cancel()); _cancelExport.IsVisible = false;
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
        {
            _volume.Resources["SliderThumbBackground" + state] = Brushes.White;
            _volume.Resources["SliderTrackFill" + state] = Brush.Parse("#E6E4EE");
        }
        _track.Duration = source.Duration;
        _resetRange = Ui.Link("恢复全部", () => _track.Range = new(TimeSpan.Zero, _track.Duration));
        var transport = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 8,
            Children = { _play, Ui.Column(_time, 1), Ui.Column(_resetRange, 4) } };
        if (source.HasAudio)
        {
            transport.Children.Add(Ui.Column(new LineIcon(IconKind.Speaker) { Width = 16, Height = 16 }, 2));
            transport.Children.Add(Ui.Column(_volume, 3));
        }
        var format = Ui.Segmented(FormatNames, 0, _ => { ApplyFormatDefaults(); UpdateRange(); }, out _formatIndex, out _enableFormat);
        var chips = format.GetLogicalDescendants().OfType<Button>().ToArray();
        for (var i = 0; i < chips.Length; i++) ToolTip.SetTip(chips[i], RecordingExportProfiles.Get(Formats[i]).Description);
        AutomationProperties.SetName(FrameRate, "导出帧率"); AutomationProperties.SetName(Size, "导出尺寸"); AutomationProperties.SetName(CustomWidth, "自定义宽度");
        AutomationProperties.SetName(_count, "取帧数量");
        _countGroup = Setting("取帧数量", _count);
        _frameRateGroup = Setting("帧率", FrameRate);
        _customGroup = Setting("宽度", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { CustomWidth, _customHeight } });
        _customGroup.IsVisible = false;
        var sizeLabel = Ui.Description("尺寸");
        var sizeGroup = Setting(sizeLabel, Size);
        _count.ValueChanged += (_, _) => { UpdateSizeDetails(); UpdateRange(); };
        _gridPreview = new Border { Margin = new Thickness(0, 3, 0, 0), Child = new StackPanel { Spacing = 6, Children =
            { TwoEnds(Ui.Text("宫格预览", 12), _gridLayout), new Border { Background = Brush.Parse("#263340"), CornerRadius = new(6),
                HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(4), Child = _gridCells } } } };
        _syncSizeLabel = grid => sizeLabel.Text = grid ? "单格尺寸" : "尺寸";
        Size.SelectionChanged += (_, _) => UpdateSizeDetails();
        CustomWidth.TextChanged += (_, _) => UpdateSizeDetails();
        _track.RangeChanged += _ => UpdateRange();
        _thumbnailFrame.Child = _thumbnailImage;
        ThumbnailPopup = new Popup
        {
            // A separate popup surface stays above the native video view.
            PlacementTarget = _track, Placement = PlacementMode.Top, VerticalOffset = -2,
            IsHitTestVisible = false, TakesFocusFromNativeControl = false,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.FlipY,
            Child = new Border { Background = Brush.Parse("#1D1D24"), CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 4, 4, 2), IsHitTestVisible = false,
                BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 4, Blur = 12, Color = Color.FromArgb(64, 16, 18, 32) }),
                Child = new StackPanel { Spacing = 3, Children = { _thumbnailFrame, _thumbnailLabel } } }
        };
        _track.HotTickChanged += _ => ShowThumbnail();
        _track.Seeked += Seek;
        _status.FontSize = DesignTokens.Caption;
        _track.ThumbnailAt = at => _thumbnails.GetValueOrDefault(at)?.Bitmap;
        var fields = new StackPanel { Spacing = 8, Children = { Ui.Text("导出设置", 16), Group("格式", format),
            _countGroup, _frameRateGroup, sizeGroup, _customGroup, _gridPreview } };
        var actions = new StackPanel { Spacing = 5, Children = { _status, _progress, _cancelExport, TwoEnds(_outputType, _presetDimensions), _save } };
        _save.HorizontalAlignment = HorizontalAlignment.Stretch;
        _save.HorizontalContentAlignment = HorizontalAlignment.Center;
        DockPanel.SetDock(actions, Dock.Bottom);
        var export = Panel(new DockPanel { Children = { actions, fields } }, "#F4F7FA", 13);
        var timeline = new Border { Padding = new Thickness(12, 8),
            Child = new StackPanel { Spacing = 3, Children = { transport, _ruler, _track, _range, ThumbnailPopup } } };
        var canvas = new Border { Background = Brush.Parse("#202B37"),
            ClipToBounds = true, Child = new Grid { Children = { _video, _scrubImage } } };
        var removeButton = Ui.IconButton(IconKind.Close, "移除素材", remove);
        var header = Ui.ResultHeader(IconKind.Film, ToolTone.Grey, source.Name, _recordingDetails, removeButton);
        header.Margin = new Thickness(12, 8);
        var editor = Panel(new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children =
            { header, InRow(canvas, 1), InRow(timeline, 2) } }, "#FFFFFF", 0);
        var workspaceGrid = new Grid { ColumnSpacing = 12, RowSpacing = 9, Children = { editor, export } };
        void ArrangeWorkspace(double width)
        {
            var stacked = width < 546;
            workspaceGrid.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,258");
            workspaceGrid.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "*");
            Grid.SetColumn(export, stacked ? 0 : 1);
            Grid.SetRow(export, stacked ? 1 : 0);
            var videoWidth = width - (stacked ? 0 : 270);
            canvas.Height = Math.Clamp(videoWidth * 9 / 16, 155, 410);
            _gridCells.Width = 216;
        }
        ArrangeWorkspace(840);
        SizeChanged += (_, e) => { if (e.WidthChanged) ArrangeWorkspace(e.NewSize.Width); };
        Content = workspaceGrid;
        ApplyFormatDefaults(); UpdateRange();
        _play.IsEnabled = false;
        _timer.Tick += (_, _) => UpdatePlayback();
        _volume.ValueChanged += (_, _) => { if (!_closing && _player is { } player) player.Volume = (int)_volume.Value; };
        KeyDown += (_, e) =>
        {
            if (e.Source is TextBox or NumericUpDown or Button) return;
            if (e.Key == Key.Space) controller.Run(TogglePlaybackAsync);
            else if (e.Key == Key.S && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) controller.Run(SaveAsync);
            else return;
            e.Handled = true;
        };
    }

    internal void Start()
    {
        if (_started) return;
        _started = true;
        _initialization = InitializePlaybackAsync();
        Seek(TimeSpan.Zero);
    }

    internal void Pause()
    {
        if (!_closing && _player?.IsPlaying == true) _player.SetPause(true);
    }

    private static Control InRow(Control control, int row) { Grid.SetRow(control, row); return control; }

    private static Grid TwoEnds(Control left, Control right) => new()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { left, Ui.Column(right, 1) }
    };

    private static Border Setting(string label, Control control) => Setting(Ui.Description(label), control);

    private static Border Setting(TextBlock label, Control control)
    {
        label.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,144"), ColumnSpacing = 5,
            Children = { label, Ui.Column(control, 1) } } };
    }

    private static Border Panel(Control child, string background, int padding) => new()
    {
        Background = Brush.Parse(background), BorderBrush = Brush.Parse("#DCE4EC"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(11), Padding = new Thickness(padding), ClipToBounds = true, Child = child
    };

    private static StackPanel Group(string label, params Control[] items)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = DesignTokens.Caption, Foreground = Brush.Parse("#5F6270"), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.AddRange(items);
        return panel;
    }

    private readonly Action<bool> _syncSizeLabel;

    private bool Even => Format == RecordingExportFormat.Mp4;

    // Every format starts from its own defaults; nothing is remembered between recordings.
    private void ApplyFormatDefaults()
    {
        var format = Format; var profile = RecordingExportProfiles.Get(format);
        var grid = format == RecordingExportFormat.ContactSheet;
        _frameRateGroup.IsVisible = !grid; _countGroup.IsVisible = grid; _gridPreview.IsVisible = grid;
        _syncSizeLabel(grid);
        _outputType.Text = grid ? "JPG" : FormatNames[_formatIndex()];
        _save.Content = "保存" + (format == RecordingExportFormat.Mp4 ? "视频" : FormatNames[_formatIndex()]) + "…";
        AutomationProperties.SetName(_save, _save.Content!.ToString());
        FrameRate.SelectedIndex = Math.Max(0, RecordingExportProfiles.FrameRates.ToList().IndexOf(profile.DefaultFramesPerSecond));
        if (format == RecordingExportFormat.ContactSheet)
        {
            // Each cell is limited by its long edge; "原尺寸" keeps the recorded pixels.
            _sizes = new[] { new RecordingSizeChoice("原尺寸", Source) }.Concat(ContactSheetPolicy.LongEdges
                .Where(edge => edge < Math.Max(Source.Width, Source.Height))
                .Select(edge => new RecordingSizeChoice($"每格 ≤ {edge} px", RecordingSizePolicy.FitLongEdge(Source, edge, false)))).ToArray();
            var fallback = _sizes.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Label == $"每格 ≤ {ContactSheetPolicy.DefaultLongEdge} px");
            Fill(fallback.c.Label is null ? 0 : fallback.i);
        }
        else
        {
            _sizes = RecordingSizePolicy.Choices(Source, Even);
            Fill(RecordingSizePolicy.DefaultChoice(Source, format, Even));
        }
        CustomWidth.Text = OutputSize(Size.SelectedIndex).Width.ToString();
    }

    private void Fill(int selected)
    {
        Size.ItemsSource = _sizes.Select(choice => choice.Label).Append("自定义").ToArray();
        Size.SelectedIndex = selected;
        UpdateSizeDetails();
    }

    private void UpdateSizeDetails()
    {
        var index = Size.SelectedIndex;
        _customGroup.IsVisible = index == _sizes.Count;
        _presetDimensions.IsVisible = index >= 0;
        if (_presetDimensions.IsVisible)
        {
            var size = OutputSize(index);
            if (Format == RecordingExportFormat.ContactSheet) size = ContactSheetPolicy.SheetSize(size, FrameCount);
            _presetDimensions.Text = $"输出 {size.Width} × {size.Height}";
        }
        UpdateCustomHeight();
    }

    private VideoSize OutputSize(int index)
    {
        if (index >= 0 && index < _sizes.Count) return _sizes[index].Size;
        return RecordingSizePolicy.ScaleToWidth(Source, int.TryParse(CustomWidth.Text, out var width) && width > 0 ? width : Source.Width, Even);
    }

    private void UpdateCustomHeight()
    {
        if (!_customGroup.IsVisible) return;
        var size = OutputSize(-1);
        _customHeight.Text = Format == RecordingExportFormat.ContactSheet
            ? $"× {size.Height}（输出 {ContactSheetPolicy.SheetSize(size, FrameCount).Width} × {ContactSheetPolicy.SheetSize(size, FrameCount).Height}）"
            : $"× {size.Height}";
    }

    internal RecordingExportOptions Options()
    {
        var format = Format;
        var fps = format == RecordingExportFormat.ContactSheet ? 0 : RecordingExportProfiles.FrameRates[Math.Max(0, FrameRate.SelectedIndex)];
        return new RecordingExportOptions(format, fps, OutputSize(Size.SelectedIndex), _track.Range, format == RecordingExportFormat.ContactSheet ? FrameCount : ContactSheetPolicy.DefaultCount);
    }

    private void UpdateRange()
    {
        var range = _track.Range; var full = range.IsFull(_track.Duration);
        var rate = _mediaSource.FramesPerSecond > 0 ? $" · {_mediaSource.FramesPerSecond:0.##} FPS" : "";
        _recordingDetails.Text = $"{Source.Width} × {Source.Height} · {RecordingTrimBar.FormatPrecise(_track.Duration)}{rate} · {(_mediaSource.HasAudio ? (_mediaSource.IsRecording ? "系统声音" : "有声音") : "静音")}";
        _range.Text = full ? $"导出全部 · {RecordingTrimBar.FormatPrecise(range.Start)} – {RecordingTrimBar.FormatPrecise(range.End)}"
            : $"截取 {RecordingTrimBar.FormatPrecise(range.Start)} – {RecordingTrimBar.FormatPrecise(range.End)} · 共 {RecordingTrimBar.FormatPrecise(range.Length)}";
        _resetRange.IsVisible = !full;
        if (_rulerDuration != _track.Duration)
        {
            _rulerDuration = _track.Duration;
            _ruler.Children.Clear(); _ruler.ColumnDefinitions = new("*,*,*,*,*");
            for (var i = 0; i <= 4; i++)
            {
                var time = Ui.Description(RecordingTrimBar.FormatPrecise(_track.Duration * (i / 4d)));
                time.FontSize = 10;
                time.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == 4 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                _ruler.Children.Add(Ui.Column(time, i));
            }
        }
        var ticks = Format == RecordingExportFormat.ContactSheet ? _mediaSource.SampleTimes(range, FrameCount) : [];
        if (!ticks.SequenceEqual(_track.Ticks)) { _track.Ticks = ticks; BuildGrid(ticks); }
        var frames = ticks.Count > 0 ? ticks : _mediaSource.SampleTimes(new(TimeSpan.Zero, _track.Duration), ContactSheetPolicy.DefaultCount);
        _track.FrameTimes = frames;
        LoadThumbnails(frames);
        if (_track.Position < range.Start || _track.Position > range.End) Seek(range.Start);
        else UpdateTime(_track.Position);
    }

    private void BuildGrid(IReadOnlyList<TimeSpan> ticks)
    {
        _gridCells.Children.Clear(); _gridImages.Clear();
        var (columns, rows) = ContactSheetPolicy.Grid(FrameCount);
        _gridLayout.Text = $"{columns} 列 × {rows} 行";
        for (var index = 0; index < ticks.Count; index++)
        {
            var at = ticks[index];
            var width = Math.Floor(216d / columns);
            var image = new Image { Height = Math.Clamp((width - 4) * Source.Height / Source.Width, 23, 62), Stretch = Stretch.Uniform,
                Source = _thumbnails.GetValueOrDefault(at)?.Bitmap };
            _gridImages.Add(image);
            var label = new TextBlock { Text = (index + 1).ToString("00"), Foreground = Brushes.White, FontSize = 9 };
            var thumb = Ui.Button("", () => Seek(at), $"{index + 1:00} · {RecordingTrimBar.FormatPrecise(at)}");
            thumb.Width = width; thumb.MinWidth = 0; thumb.MinHeight = 0; thumb.Padding = new Thickness(2);
            thumb.Background = Brushes.Transparent; thumb.BorderThickness = default;
            thumb.Content = new StackPanel { Spacing = 1, Width = width - 4, Children = { image, label } };
            _gridCells.Children.Add(thumb);
        }
    }

    private void Seek(TimeSpan time)
    {
        if (_closing) return;
        _resumeFrom = null;
        _scrubPosition = time;
        if (_player?.IsPlaying == true) _player.SetPause(true);
        _track.Position = time;
        UpdateTime(time);
        if (File.Exists(RecordingExporter.FfmpegPath))
        {
            if (_scrubTask.IsCompleted) _scrubTask = PreviewScrubAsync();
        }
        else if (_player?.IsSeekable == true) _player.Time = (long)time.TotalMilliseconds;
    }

    private async Task PreviewScrubAsync()
    {
        // Decode one frame at a time and pick up the latest position when the pointer moves faster than decoding.
        // VLC cannot seek reliably once playback has ended, and decoding also shows the selected frame while paused.
        var frameLength = TimeSpan.FromSeconds(1d / Math.Max(1, _mediaSource.FramesPerSecond > 0 ? _mediaSource.FramesPerSecond : 30));
        var size = RecordingSizePolicy.FitLongEdge(Source, 960, false);
        try
        {
            while (_resumeFrom is null && _scrubPosition is { } time)
            {
                var lastFrame = _track.Duration > frameLength ? _track.Duration - frameLength : TimeSpan.Zero;
                var pixels = await RecordingExporter.ReadFrameAsync(_mediaSource.Path, _mediaSource.FrameAt(time > lastFrame && _mediaSource.AnimationFrames is null ? lastFrame : time), size, _scrubCancellation.Token);
                if (_closing || _resumeFrom is not null || _scrubPosition is null) break;
                var previous = _scrubFrame;
                _scrubFrame = Picture.FromPixels(pixels, size.Width, size.Height);
                _scrubImage.Source = _scrubFrame.Bitmap;
                _scrubImage.IsVisible = true;
                _video.IsVisible = false;
                previous?.Dispose();
                if (_scrubPosition == time) break;
            }
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error) { AppLog.Write("录屏定位预览：" + error.Message); }
    }

    private void ShowPlayback()
    {
        _video.IsVisible = true;
        _scrubImage.IsVisible = false;
        _scrubImage.Source = null;
        _scrubFrame?.Dispose(); _scrubFrame = null;
    }

    private void ShowThumbnail()
    {
        var index = _track.HotTick;
        if (index < 0 || _closing) { ThumbnailPopup.IsOpen = false; return; }
        var time = _track.Ticks[index];
        _thumbnailImage.Source = _thumbnails.TryGetValue(time, out var picture) ? picture.Bitmap : null;
        _thumbnailFrame.Width = _thumbnailImage.Source is null ? Math.Round(72 * Source.Width / (double)Source.Height) : double.NaN;
        _thumbnailLabel.Text = $"{index + 1:00} · {RecordingTrimBar.FormatPrecise(time)}";
        ThumbnailPopup.PlacementRect = _track.TickAnchor(index);
        // Reopen so the popup re-anchors to the new tick.
        ThumbnailPopup.IsOpen = false; ThumbnailPopup.IsOpen = true;
    }

    // Decodes the six sample frames at thumbnail size once the handles settle; the cache keeps frames shared by both ranges.
    private void LoadThumbnails(IReadOnlyList<TimeSpan> ticks)
    {
        if (_closing || _track.IsDragging || (_thumbnailLoad is not null && ticks.SequenceEqual(_thumbnailTicks))) return;
        _thumbnailLoad?.Cancel(); _thumbnailLoad = null;
        if (ticks.All(_thumbnails.ContainsKey) || !File.Exists(RecordingExporter.FfmpegPath)) return;
        var cancellation = _thumbnailLoad = new CancellationTokenSource(); _thumbnailTicks = ticks;
        foreach (var stale in _thumbnails.Keys.Except(ticks).ToArray())
        {
            _thumbnails[stale].Dispose(); _thumbnails.Remove(stale);
        }
        var size = RecordingSizePolicy.FitLongEdge(Source, 160, false);
        var previous = _thumbnailTask;
        _thumbnailTask = LoadAsync();
        async Task LoadAsync()
        {
            try
            {
                await previous;
                foreach (var time in ticks.Where(t => !_thumbnails.ContainsKey(t)).Distinct().ToArray())
                {
                    var pixels = await RecordingExporter.ReadFrameAsync(_mediaSource.Path, time, size, cancellation.Token);
                    if (cancellation.IsCancellationRequested || _closing) return;
                    _thumbnails[time] = Picture.FromPixels(pixels, size.Width, size.Height);
                    _track.InvalidateVisual();
                    for (var i = 0; i < _gridImages.Count; i++)
                        if (_track.Ticks[i] == time) _gridImages[i].Source = _thumbnails[time].Bitmap;
                    if (_track.HotTick >= 0 && _track.Ticks[_track.HotTick] == time) ShowThumbnail();
                }
            }
            // A missing thumbnail only leaves the placeholder; saving still reports its own errors.
            catch (OperationCanceledException) { }
            catch (Exception error) { AppLog.Write("视频缩略图：" + error.Message); }
            finally { if (_thumbnailLoad == cancellation) _thumbnailLoad = null; cancellation.Dispose(); }
        }
    }

    private async Task InitializePlaybackAsync()
    {
        _status.Text = "正在加载回放…";
        try
        {
            _engine = await Task.Run(MediaRuntime.Create);
            if (_closing) return;
            _media = new LibVLCSharp.Shared.Media(_engine, _mediaSource.Path, FromType.FromPath);
            _player = new MediaPlayer(_engine); _video.MediaPlayer = _player;
            _player.EncounteredError += (_, _) => Dispatcher.UIThread.Post(() => _status.Text = "回放失败，源素材保留，可尝试保存后用系统播放器打开。");
            _player.Media = _media;
            _play.IsEnabled = true; _timer.Start(); _status.Text = "";
        }
        catch (Exception error)
        {
            _status.Text = "回放组件不可用，仍可导出素材：" + error.Message;
            _play.IsEnabled = false; _volume.IsEnabled = false;
        }
    }

    private async Task TogglePlaybackAsync()
    {
        if (_closing || !_restartTask.IsCompleted || _player is not { } player) return;
        if (_resumeFrom is not null || player.IsPlaying && _scrubPosition is null)
        {
            _resumeFrom = null;
            player.SetPause(true);
        }
        else
        {
            var range = _track.Range;
            var position = _scrubPosition ?? _track.Position;
            var ended = player.State == VLCState.Ended;
            if (_scrubPosition is not null || ended || position < range.Start || position >= range.End)
                _resumeFrom = position < range.Start || position >= range.End || ended && _scrubPosition is null
                    ? range.Start : position;
            if (ended)
            {
                _restartTask = Task.Run(player.Stop);
                await _restartTask;
                if (_closing || _resumeFrom is null) return;
            }
            player.Play();
        }
        UpdatePlayback();
    }

    private void UpdatePlayback()
    {
        if (_closing || !_restartTask.IsCompleted || _player is not { } player) return;
        if (_mediaSource.IsRecording && player.Length > 0 && Math.Abs(player.Length - _track.Duration.TotalMilliseconds) > 1)
        { _track.Duration = TimeSpan.FromMilliseconds(player.Length); UpdateRange(); }
        if (_resumeFrom is { } target)
        {
            if (!player.IsPlaying || !player.IsSeekable) return;
            player.Time = (long)target.TotalMilliseconds;
            _resumeFrom = _scrubPosition = null;
            _track.Position = target;
            ShowPlayback();
        }
        else if (_scrubPosition is null && !_track.IsDragging)
        {
            var now = player.State == VLCState.Ended ? _track.Duration : TimeSpan.FromMilliseconds(Math.Max(0, player.Time));
            if (now >= _track.Range.End && !_track.Range.IsFull(_track.Duration)) Seek(_track.Range.End);
            else _track.Position = now;
        }
        UpdateTime(_track.Position);
        _play.Content = new LineIcon(player.IsPlaying && _scrubPosition is null ? IconKind.Pause : IconKind.Play, brush: Brush.Parse("#1A1B22")) { Width = 18, Height = 18 };
    }

    private void UpdateTime(TimeSpan now) =>
        _time.Text = $"{RecordingTrimBar.FormatPrecise(now, _track.Duration.TotalHours >= 1)} / {RecordingTrimBar.FormatPrecise(_track.Duration)}";

    private async Task SaveAsync()
    {
        if (_saving || _closing) return;
        _saveTask = SaveCoreAsync(); await _saveTask;
        if (_saved && _mediaSource.IsRecording) await _savedRecording();
    }

    private void SetEditable(bool on)
    {
        _save.IsEnabled = on; _enableFormat(on); _count.IsEnabled = on; _track.IsHitTestVisible = on; _resetRange.IsEnabled = on;
        FrameRate.IsEnabled = Size.IsEnabled = CustomWidth.IsEnabled = on;
    }

    private async Task SaveCoreAsync()
    {
        _saving = true; BusyChanged?.Invoke(true); _exportCancellation = new CancellationTokenSource();
        var options = Options();
        var profile = RecordingExportProfiles.Get(options.Format);
        try
        {
            IStorageFile? file;
            _selectingSavePath = true;
            try
            {
                var stamp = _mediaSource.IsRecording ? DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") : Path.GetFileNameWithoutExtension(_mediaSource.Name) + "_转换";
                file = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "保存" + profile.DisplayName,
                    SuggestedFileName = (options.Format == RecordingExportFormat.ContactSheet ? stamp + "_宫格图" : stamp) + profile.Extension,
                    DefaultExtension = profile.Extension.TrimStart('.'), ShowOverwritePrompt = true,
                    FileTypeChoices = [new FilePickerFileType(profile.DisplayName) { Patterns = ["*" + profile.Extension] }]
                });
            }
            finally { _selectingSavePath = false; }
            _exportCancellation.Token.ThrowIfCancellationRequested();
            if (file is null) return;
            if (file.TryGetLocalPath() is not { } path) throw new InvalidOperationException("请选择本机可写入的文件位置。");
            if (!_mediaSource.IsRecording && string.Equals(Path.GetFullPath(path),
                Path.GetFullPath(_mediaSource.OriginalPath ?? _mediaSource.Path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择其他名称保存，原素材会保留。");
            SetEditable(false);
            _cancelExport.IsVisible = true; _progress.IsVisible = true; _progress.Value = 0;
            var detail = options.Format == RecordingExportFormat.ContactSheet ? $"{ContactSheetPolicy.SheetSize(options.Size, options.FrameCount).Width} × {ContactSheetPolicy.SheetSize(options.Size, options.FrameCount).Height}"
                : $"{options.Size.Width} × {options.Size.Height} · {options.FramesPerSecond} FPS";
            _status.Text = $"正在保存 {FormatNames[_formatIndex()]} · {detail}";
            var exported = await RecordingExporter.ExportAsync(_mediaSource.Path, _workspace.ExportPath(options.Format), options, Source, _track.Duration,
                new Progress<double>(value => _progress.Value = value), _exportCancellation.Token, _mediaSource);
            await RecordingExporter.SaveAtomicallyAsync(exported, path, _exportCancellation.Token);
            _saved = true; _status.Text = "已保存。";
        }
        catch (OperationCanceledException) { _status.Text = "已取消导出，源素材保留。"; }
        catch (Exception error) { _status.Text = error.Message; }
        finally
        {
            _saving = false; BusyChanged?.Invoke(false); _exportCancellation.Dispose(); _exportCancellation = null;
            SetEditable(true);
            _cancelExport.IsVisible = false; _progress.IsVisible = false;
        }
    }

    internal async Task<bool> ConfirmDiscardAsync(Window owner, bool discardUnsaved)
    {
        if (discardUnsaved) _discardConfirmation?.Close(true);
        if (!_mediaSource.IsRecording || _saved || discardUnsaved) return true;
        if (_discardTask is { } pending) return await pending;
        _discardTask = ConfirmAsync(owner);
        try { return await _discardTask; }
        finally { _discardTask = null; }
    }

    private async Task<bool> ConfirmAsync(Window owner)
    {
        _discardConfirmation = new ConfirmWindow("录屏尚未保存，是否放弃？", "放弃录屏", "放弃后本次录制的内容会被删除，无法恢复。", destructive: true);
        try { return await _discardConfirmation.ShowDialog<bool>(owner); }
        finally { _discardConfirmation = null; }
    }

    internal async Task ReleaseAsync(Action detach)
    {
        if (_closing) { await _released.Task; return; }
        _closing = true;
        _timer.Stop(); _scrubCancellation.Cancel(); _thumbnailLoad?.Cancel(); ThumbnailPopup.IsOpen = false;
        try
        {
            _exportCancellation?.Cancel();
            // A native picker can stay open until its owner closes. A cancelled picker cannot start an export.
            if (_saving && !_selectingSavePath) await _saveTask;
            await _initialization; await _scrubTask; await _thumbnailTask;
            _scrubCancellation.Dispose();
            _scrubImage.Source = null; _scrubFrame?.Dispose();
            if (_player is { } player)
            {
                try { await _restartTask; await Task.Run(player.Stop); }
                catch (Exception error) { AppLog.Write("回放停止异常：" + error.Message); }
            }
            _video.MediaPlayer = null;
            if (_player is not null) _player.Media = null;
            detach();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Run(() => { _player?.Dispose(); _media?.Dispose(); _engine?.Dispose(); });
            foreach (var image in _gridImages) image.Source = null;
            foreach (var picture in _thumbnails.Values) picture.Dispose();
            _thumbnails.Clear();
            _workspace.Dispose();
        }
        finally { _released.TrySetResult(); }
    }
}
