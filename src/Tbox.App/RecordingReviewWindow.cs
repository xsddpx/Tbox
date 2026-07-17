using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Tbox.Core;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace Tbox.App;

internal sealed class RecordingReviewWindow : Window, IDisposable
{
    private readonly RecordingResult _result;
    private readonly MediaPlayer _mediaPlayer;
    private readonly MediaPlayerElement _playerElement;
    private readonly AppWindow _appWindow;
    private readonly Slider _positionSlider;
    private readonly TextBlock _timeText;
    private readonly TextBlock _statusText;
    private readonly Button _playButton;
    private readonly Button _formatButton;
    private readonly Button _saveButton;
    private readonly Button _rerecordButton;
    private readonly Button _closeButton;
    private readonly ProgressBar _exportProgress;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _exportCancellation;
    private RecordingExportFormat _format = RecordingExportFormat.Mp4;
    private bool _updatingPosition;
    private bool _saved;
    private bool _allowClose;
    private bool _confirmingClose;
    private bool _closeStarted;
    private bool _closedHandled;
    private bool _playbackDetached;
    private bool _disposed;
    private RectInt32? _rerecordAfterClose;

    internal event Action<RectInt32>? RerecordRequested;
    internal event Action<RecordingReviewWindow>? Dismissed;

    internal RecordingReviewWindow(RecordingResult result)
    {
        _result = result;
        Title = "Tbox 录屏回放";
        var palette = TboxUi.Palette;

        _mediaPlayer = new MediaPlayer
        {
            AutoPlay = false,
            IsLoopingEnabled = false,
            Source = MediaSource.CreateFromUri(new Uri(result.Workspace.MasterFilePath))
        };
        _playerElement = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _playerElement.SetMediaPlayer(_mediaPlayer);

        _positionSlider = new Slider { Minimum = 0, Maximum = Math.Max(0.1, result.Duration.TotalSeconds) };
        _positionSlider.ValueChanged += OnPositionChanged;
        _timeText = Text("00:00/" + FormatDuration(result.Duration));
        _playButton = Button("▶", "播放或暂停");
        _playButton.Click += (_, _) => TogglePlayback();
        var volume = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 1,
            Width = 96
        };
        volume.ValueChanged += (_, args) => _mediaPlayer.Volume = args.NewValue;

        _formatButton = Button("MP4", "选择导出格式");
        _formatButton.Flyout = CreateFormatFlyout();
        _saveButton = Button("另存为…", "导出录屏");
        TboxUi.StyleStandardButton(_saveButton, "导出录屏", primary: true);
        _saveButton.Click += async (_, _) =>
        {
            if (_exportCancellation is not null)
            {
                _exportCancellation.Cancel();
                return;
            }
            await SaveAsync();
        };
        _rerecordButton = Button("重新录制", "使用相同区域重新录制");
        _rerecordButton.Click += (_, _) => Rerecord();
        _closeButton = Button("关闭", "关闭回放");
        _closeButton.Click += (_, _) => Close();
        _statusText = Text(RecordingExportProfiles.Get(_format).Description);
        _statusText.TextWrapping = TextWrapping.Wrap;
        _exportProgress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Visibility = Visibility.Collapsed,
            Height = 3
        };

        var playbackRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        playbackRow.Children.Add(_playButton);
        playbackRow.Children.Add(_timeText);
        playbackRow.Children.Add(TboxUi.CreateText("音量", TboxUi.CaptionFontSize, secondary: true));
        playbackRow.Children.Add(volume);

        var exportRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        exportRow.Children.Add(_formatButton);
        exportRow.Children.Add(_saveButton);
        exportRow.Children.Add(_rerecordButton);
        exportRow.Children.Add(_closeButton);

        var controls = new StackPanel
        {
            Spacing = 4,
            Padding = new Thickness(12, 8, 12, 12),
            Background = TboxUi.Brush(palette.SurfaceSecondary)
        };
        controls.Children.Add(_positionSlider);
        controls.Children.Add(playbackRow);
        controls.Children.Add(exportRow);
        controls.Children.Add(_statusText);
        controls.Children.Add(_exportProgress);

        var root = new Grid
        {
            RequestedTheme = TboxUi.RequestedTheme,
            Background = new SolidColorBrush(Colors.Black),
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };
        root.Children.Add(_playerElement);
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);
        Content = root;

        var hwnd = WindowNative.GetWindowHandle(this);
        NativeMethods.DisableWindowTransitions(hwnd);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Tbox.ico"));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, true);
            presenter.IsAlwaysOnTop = false;
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }
        _appWindow.IsShownInSwitchers = true;
        TboxUi.ConfigureTitleBar(_appWindow, palette.IsDark);
        PositionWindow(hwnd, result.Region);
        _appWindow.Closing += OnWindowClosing;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => RefreshPlaybackPosition();
        _timer.Start();
        Closed += OnClosed;
    }

    internal string MasterFilePath => _result.Workspace.MasterFilePath;

    internal void CloseWithoutPrompt()
    {
        if (_closeStarted) return;
        _closeStarted = true;
        _allowClose = true;
        SetClosingState();
        Close();
    }

    private MenuFlyout CreateFormatFlyout()
    {
        var flyout = new MenuFlyout();
        foreach (var format in Enum.GetValues<RecordingExportFormat>())
        {
            var profile = RecordingExportProfiles.Get(format);
            var item = new MenuFlyoutItem
            {
                Text = $"{profile.DisplayName} - {profile.Description}",
                IsEnabled = format == RecordingExportFormat.Mp4 || RecordingExporter.IsImageExportAvailable,
                Tag = format
            };
            item.Click += (_, _) => SelectFormat((RecordingExportFormat)item.Tag);
            flyout.Items.Add(item);
        }
        return flyout;
    }

    private void SelectFormat(RecordingExportFormat format)
    {
        _format = format;
        var profile = RecordingExportProfiles.Get(format);
        _formatButton.Content = profile.DisplayName;
        _statusText.Text = profile.Description + (profile.SupportsAudio ? string.Empty : " · 图像格式不包含声音");
    }

    private async Task SaveAsync()
    {
        if (_exportCancellation is not null) return;
        var profile = RecordingExportProfiles.Get(_format);
        StorageFile? destination = null;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = _format == RecordingExportFormat.Mp4
                    ? PickerLocationId.VideosLibrary
                    : PickerLocationId.PicturesLibrary,
                SuggestedFileName = $"Tbox录屏_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}"
            };
            picker.FileTypeChoices.Add(profile.DisplayName, [profile.Extension]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            destination = await picker.PickSaveFileAsync();
            if (destination is null) return;

            SetExporting(true);
            _exportCancellation = new CancellationTokenSource();
            var progress = new Progress<double>(value => _exportProgress.Value = value);
            var exportPath = _format == RecordingExportFormat.Mp4
                ? _result.Workspace.MasterFilePath
                : _result.Workspace.GetExportPath(_format);
            if (_format != RecordingExportFormat.Mp4)
            {
                await RecordingExporter.ExportAsync(
                    _result.Workspace.MasterFilePath,
                    exportPath,
                    _format,
                    _result.Duration,
                    progress,
                    _exportCancellation.Token);
            }

            await using var source = new FileStream(exportPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var output = await destination.OpenStreamForWriteAsync();
            output.SetLength(0);
            await source.CopyToAsync(output, _exportCancellation.Token);
            await output.FlushAsync(_exportCancellation.Token);
            _saved = true;
            _statusText.Text = $"已保存 {profile.DisplayName}：{destination.Path}";
        }
        catch (OperationCanceledException)
        {
            _statusText.Text = "已取消导出，录屏母版仍保留在回放中。";
            if (destination is not null) await TryDeleteAsync(destination);
        }
        catch (Exception exception)
        {
            AppLog.Write($"Export recording failed: {exception}");
            _statusText.Text = "导出失败，仍可重试或改存 MP4。";
            if (destination is not null) await TryDeleteAsync(destination);
        }
        finally
        {
            _exportCancellation?.Dispose();
            _exportCancellation = null;
            SetExporting(false);
        }
    }

    private void SetExporting(bool exporting)
    {
        _exportProgress.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        _exportProgress.Value = 0;
        _formatButton.IsEnabled = !exporting;
        _saveButton.Content = exporting ? "取消导出" : "另存为…";
        _rerecordButton.IsEnabled = !exporting;
        _statusText.Text = exporting ? "正在导出…" : _statusText.Text;
    }

    private void TogglePlayback()
    {
        if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _mediaPlayer.Pause();
            _playButton.Content = "▶";
        }
        else
        {
            _mediaPlayer.Play();
            _playButton.Content = "Ⅱ";
        }
    }

    private void RefreshPlaybackPosition()
    {
        var session = _mediaPlayer.PlaybackSession;
        var duration = session.NaturalDuration > TimeSpan.Zero ? session.NaturalDuration : _result.Duration;
        _updatingPosition = true;
        _positionSlider.Maximum = Math.Max(0.1, duration.TotalSeconds);
        _positionSlider.Value = Math.Clamp(session.Position.TotalSeconds, 0, _positionSlider.Maximum);
        _updatingPosition = false;
        _timeText.Text = $"{FormatDuration(session.Position)}/{FormatDuration(duration)}";
        if (session.PlaybackState != MediaPlaybackState.Playing) _playButton.Content = "▶";
    }

    private void OnPositionChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingPosition) return;
        _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    private void Rerecord()
    {
        if (_closeStarted) return;
        _closeStarted = true;
        _allowClose = true;
        _rerecordAfterClose = _result.Region;
        SetClosingState();
        Close();
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closedHandled) return;
        _closedHandled = true;
        _closeStarted = true;
        SetClosingState();
        DetachPlayback();

        // Let MediaPlayerElement finish its XAML unload before releasing the player and
        // deleting the file it was reading. Synchronous teardown from the button/Closing
        // callback can surface a COM E_ABORT as an unhandled Microsoft.UI.Xaml failure.
        await Task.Delay(150);

        var rerecord = _rerecordAfterClose;
        try
        {
            Dispose();
        }
        catch (Exception exception)
        {
            AppLog.Write($"Dispose recording review failed: {exception}");
        }

        Dismissed?.Invoke(this);
        if (rerecord is { } region) RerecordRequested?.Invoke(region);
    }

    private void SetClosingState()
    {
        _timer.Stop();
        _exportCancellation?.Cancel();
        _playButton.IsEnabled = false;
        _formatButton.IsEnabled = false;
        _saveButton.IsEnabled = false;
        _rerecordButton.IsEnabled = false;
        _closeButton.IsEnabled = false;
    }

    private void DetachPlayback()
    {
        if (_playbackDetached) return;
        _playbackDetached = true;
        try
        {
            _mediaPlayer.Pause();
            _playerElement.SetMediaPlayer(null!);
            _mediaPlayer.Source = null;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Detach recording playback failed: {exception}");
        }
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || _saved) return;
        args.Cancel = true;
        if (!_confirmingClose) _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        _confirmingClose = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = (Content as FrameworkElement)?.XamlRoot,
                Title = "放弃未保存的录屏？",
                Content = "关闭后将删除本次录屏临时文件。",
                PrimaryButtonText = "放弃",
                CloseButtonText = "继续回放",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _closeStarted = true;
                _allowClose = true;
                SetClosingState();
                Close();
            }
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    private static async Task TryDeleteAsync(StorageFile file)
    {
        try
        {
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch
        {
            // Cleanup is best effort after a failed export.
        }
    }

    private static void PositionWindow(IntPtr hwnd, RectInt32 region)
    {
        var center = new PointInt32(region.X + region.Width / 2, region.Y + region.Height / 2);
        var work = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Clamp(region.Width, 480, Math.Max(480, work.Width - 60));
        var previewHeight = Math.Clamp(region.Height, 270, Math.Max(270, work.Height - 190));
        var height = Math.Min(work.Height - 40, previewHeight + 190);
        NativeMethods.MoveAndResizeWindow(hwnd, new RectInt32(
            Math.Clamp(region.X, work.X, work.X + work.Width - width),
            Math.Clamp(region.Y, work.Y, work.Y + work.Height - height),
            width,
            height));
    }

    private static TextBlock Text(string value) => new()
    {
        Text = value,
        FontSize = TboxUi.BodyFontSize,
        Foreground = TboxUi.Brush(TboxUi.Palette.TextPrimary),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Button Button(string content, string tooltip)
    {
        var button = new Button { Content = content, MinWidth = 56 };
        TboxUi.StyleStandardButton(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"hh\:mm\:ss")
        : duration.ToString(@"mm\:ss");

    public void Dispose()
    {
        if (_disposed) return;
        _timer.Stop();
        _exportCancellation?.Cancel();
        _exportCancellation?.Dispose();
        DetachPlayback();
        _appWindow.Closing -= OnWindowClosing;
        _mediaPlayer.Dispose();
        _result.Workspace.Dispose();
        _disposed = true;
    }
}
