using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tbox.Core;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
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
    private readonly TextBlock _formatDescriptionText;
    private readonly Button _playButton;
    private readonly FontIcon _playIcon;
    private readonly Button _formatButton;
    private readonly Button _saveButton;
    private readonly Button _rerecordButton;
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
        Title = "Tbox 录屏预览";

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

        _positionSlider = new Slider
        {
            Minimum = 0,
            Maximum = Math.Max(0.1, result.Duration.TotalSeconds)
        };
        ApplyRecordingSliderVisual(_positionSlider);
        AutomationProperties.SetName(_positionSlider, "播放进度");
        _positionSlider.ValueChanged += OnPositionChanged;
        _timeText = Text("00:00 / " + FormatDuration(result.Duration));
        _timeText.MinWidth = 96;
        _playIcon = FluentIcon("\uE768", 16);
        _playButton = IconButton(_playIcon, "播放");
        _playButton.Click += (_, _) => TogglePlayback();
        var volume = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 1,
            Width = 96,
            Foreground = TboxUi.Brush(Colors.White)
        };
        AutomationProperties.SetName(volume, "播放音量");
        volume.ValueChanged += (_, args) => _mediaPlayer.Volume = args.NewValue;

        _formatButton = FormatButton("MP4", "选择导出格式");
        _formatButton.Flyout = CreateFormatFlyout();
        _saveButton = LabeledButton("\uE74E", "保存", "导出录屏", primary: true);
        _saveButton.Click += async (_, _) =>
        {
            if (_exportCancellation is not null)
            {
                _exportCancellation.Cancel();
                return;
            }
            await SaveAsync();
        };
        _rerecordButton = LabeledButton("\uE777", "重新录制", "使用相同区域重新录制");
        _rerecordButton.Click += (_, _) => Rerecord();
        _formatDescriptionText = Text(FormatDescription(_format), secondary: true);
        _formatDescriptionText.TextWrapping = TextWrapping.Wrap;
        _statusText = Text("拖动进度条定位 · Space 播放/暂停 · Ctrl+S 保存 · Esc 关闭", secondary: true);
        _statusText.FontSize = TboxUi.CaptionFontSize;
        _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
        _exportProgress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Visibility = Visibility.Collapsed,
            Height = 3,
            Foreground = TboxUi.Brush(TboxUi.ScreenshotModeColor)
        };

        var playbackRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        playbackRow.Children.Add(_playButton);
        playbackRow.Children.Add(_timeText);
        playbackRow.Children.Add(new FontIcon
        {
            Glyph = "\uE767",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
            Foreground = TboxUi.Brush(Color.FromArgb(255, 220, 220, 220))
        });
        playbackRow.Children.Add(volume);

        var exportRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        exportRow.Children.Add(_formatButton);
        Grid.SetColumn(_formatDescriptionText, 1);
        exportRow.Children.Add(_formatDescriptionText);
        Grid.SetColumn(_rerecordButton, 2);
        exportRow.Children.Add(_rerecordButton);
        Grid.SetColumn(_saveButton, 3);
        exportRow.Children.Add(_saveButton);

        var controls = new StackPanel
        {
            Spacing = 7,
            Padding = new Thickness(16, 8, 16, 12),
            Background = TboxUi.Brush(Color.FromArgb(255, 32, 32, 32))
        };
        controls.Children.Add(_positionSlider);
        controls.Children.Add(playbackRow);
        controls.Children.Add(exportRow);
        controls.Children.Add(_statusText);
        controls.Children.Add(_exportProgress);

        var titleBar = new Grid
        {
            Height = 48,
            Padding = new Thickness(16, 0, 150, 0),
            Background = TboxUi.Brush(Color.FromArgb(255, 34, 34, 34)),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            }
        };
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = TboxUi.Brush(TboxUi.RecordingModeColor)
        });
        var titleText = Text("录屏预览");
        titleText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        title.Children.Add(titleText);
        titleBar.Children.Add(title);
        var metadata = Text(
            $"{result.Region.Width:N0} × {result.Region.Height:N0} · {FormatDuration(result.Duration)} · " +
            (result.HasSystemAudio ? "系统声音已开启" : "系统声音已关闭"),
            secondary: true);
        metadata.FontSize = TboxUi.CaptionFontSize;
        metadata.Margin = new Thickness(16, 0, 0, 0);
        metadata.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(metadata, 1);
        titleBar.Children.Add(metadata);

        var root = new Grid
        {
            RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(Colors.Black),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };
        root.Children.Add(titleBar);
        Grid.SetRow(_playerElement, 1);
        root.Children.Add(_playerElement);
        Grid.SetRow(controls, 2);
        root.Children.Add(controls);
        Content = root;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);

        var playAccelerator = new KeyboardAccelerator { Key = VirtualKey.Space };
        playAccelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            TogglePlayback();
        };
        root.KeyboardAccelerators.Add(playAccelerator);
        var saveAccelerator = new KeyboardAccelerator
        {
            Key = VirtualKey.S,
            Modifiers = VirtualKeyModifiers.Control
        };
        saveAccelerator.Invoked += async (_, args) =>
        {
            args.Handled = true;
            await SaveAsync();
        };
        root.KeyboardAccelerators.Add(saveAccelerator);
        var closeAccelerator = new KeyboardAccelerator { Key = VirtualKey.Escape };
        closeAccelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            Close();
        };
        root.KeyboardAccelerators.Add(closeAccelerator);

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
        TboxUi.ConfigureTitleBar(_appWindow, dark: true);
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
        SetFormatButtonContent(RecordingExportProfiles.Get(format).DisplayName);
        _formatDescriptionText.Text = FormatDescription(format);
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
            var progress = new Progress<double>(value =>
            {
                _exportProgress.Value = value;
                _statusText.Text = $"正在导出 {value:P0}";
            });
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
        SetLabeledButtonContent(
            _saveButton,
            exporting ? "\uE711" : "\uE74E",
            exporting ? "取消导出" : "保存");
        _rerecordButton.IsEnabled = !exporting;
        _statusText.Text = exporting ? "正在导出…" : _statusText.Text;
    }

    private void TogglePlayback()
    {
        if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _mediaPlayer.Pause();
            _playIcon.Glyph = "\uE768";
            UpdatePlaybackAccessibility("播放");
        }
        else
        {
            _mediaPlayer.Play();
            _playIcon.Glyph = "\uE769";
            UpdatePlaybackAccessibility("暂停");
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
        _timeText.Text = $"{FormatDuration(session.Position)} / {FormatDuration(duration)}";
        if (session.PlaybackState != MediaPlaybackState.Playing)
        {
            _playIcon.Glyph = "\uE768";
            UpdatePlaybackAccessibility("播放");
        }
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

    private static TextBlock Text(string value, bool secondary = false) => new()
    {
        Text = value,
        FontSize = TboxUi.BodyFontSize,
        Foreground = TboxUi.Brush(secondary
            ? Color.FromArgb(255, 170, 170, 170)
            : Colors.White),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static FontIcon FluentIcon(string glyph, double size) => new()
    {
        Glyph = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Button IconButton(UIElement icon, string tooltip)
    {
        var button = new Button
        {
            Content = icon,
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            RequestedTheme = ElementTheme.Dark,
            Foreground = TboxUi.Brush(Colors.White)
        };
        ApplyReviewButtonVisual(button, primary: false);
        AutomationProperties.SetName(button, tooltip);
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private static Button LabeledButton(
        string glyph,
        string text,
        string tooltip,
        bool primary = false)
    {
        var button = new Button
        {
            Height = 36,
            MinWidth = primary ? 104 : 116,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(4),
            RequestedTheme = ElementTheme.Dark
        };
        SetLabeledButtonContent(button, glyph, text);
        ApplyReviewButtonVisual(button, primary);
        AutomationProperties.SetName(button, tooltip);
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private static void SetLabeledButtonContent(Button button, string glyph, string text)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(FluentIcon(glyph, 15));
        content.Children.Add(Text(text));
        button.Content = content;
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, text);
    }

    private static Button FormatButton(string text, string tooltip)
    {
        var button = new Button
        {
            Height = 34,
            MinWidth = 92,
            Padding = new Thickness(12, 0, 10, 0),
            CornerRadius = new CornerRadius(4),
            RequestedTheme = ElementTheme.Dark
        };
        SetFormatButtonContent(button, text);
        ApplyReviewButtonVisual(button, primary: false);
        AutomationProperties.SetName(button, tooltip);
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private void SetFormatButtonContent(string text) => SetFormatButtonContent(_formatButton, text);

    private static void SetFormatButtonContent(Button button, string text)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(Text(text));
        content.Children.Add(FluentIcon("\uE70D", 10));
        button.Content = content;
    }

    private static void ApplyReviewButtonVisual(Button button, bool primary)
    {
        var normal = primary ? TboxUi.ScreenshotModeColor : Color.FromArgb(255, 44, 44, 44);
        var hover = primary ? Color.FromArgb(255, 20, 135, 230) : Color.FromArgb(255, 58, 58, 58);
        var pressed = primary ? Color.FromArgb(255, 0, 96, 170) : Color.FromArgb(255, 72, 72, 72);
        button.Foreground = TboxUi.Brush(Colors.White);
        button.Background = TboxUi.Brush(normal);
        button.BorderBrush = TboxUi.Brush(primary
            ? TboxUi.ScreenshotModeColor
            : Color.FromArgb(255, 82, 82, 82));
        button.BorderThickness = new Thickness(1);
        button.Resources["ButtonBackgroundPointerOver"] = TboxUi.Brush(hover);
        button.Resources["ButtonBackgroundPressed"] = TboxUi.Brush(pressed);
        button.Resources["ButtonForegroundDisabled"] = TboxUi.Brush(Color.FromArgb(255, 112, 112, 112));
    }

    private static void ApplyRecordingSliderVisual(Slider slider)
    {
        var normal = TboxUi.Brush(TboxUi.RecordingModeColor);
        var hover = TboxUi.Brush(Color.FromArgb(255, 255, 98, 89));
        var pressed = TboxUi.Brush(Color.FromArgb(255, 217, 45, 36));
        slider.Foreground = normal;
        slider.Resources["SliderThumbBackground"] = normal;
        slider.Resources["SliderThumbBackgroundPointerOver"] = hover;
        slider.Resources["SliderThumbBackgroundPressed"] = pressed;
        slider.Resources["SliderTrackValueFill"] = normal;
        slider.Resources["SliderTrackValueFillPointerOver"] = hover;
        slider.Resources["SliderTrackValueFillPressed"] = pressed;
    }

    private void UpdatePlaybackAccessibility(string name)
    {
        AutomationProperties.SetName(_playButton, name);
        ToolTipService.SetToolTip(_playButton, name);
    }

    private static string FormatDescription(RecordingExportFormat format)
    {
        var profile = RecordingExportProfiles.Get(format);
        return profile.Description + (profile.SupportsAudio ? string.Empty : " · 图像格式不包含声音");
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
