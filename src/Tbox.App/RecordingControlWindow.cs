using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Tbox.Core;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace Tbox.App;

internal sealed record RecordingResult(
    RecordingWorkspace Workspace,
    RectInt32 Region,
    TimeSpan Duration,
    bool HasSystemAudio);

internal sealed class RecordingControlWindow : Window, IDisposable
{
    private readonly RectInt32 _region;
    private readonly CaptureRegionFrame _frame;
    private readonly RecordingWorkspace _workspace;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private readonly Border _root;
    private readonly Ellipse _recordingIndicator;
    private readonly TextBlock _timeText;
    private readonly TextBlock _pausedText;
    private readonly TextBlock _statusText;
    private readonly Button _startButton;
    private readonly Button _frameRateButton;
    private readonly TextBlock _frameRateText;
    private readonly Button _stopButton;
    private readonly Button _pauseButton;
    private readonly FontIcon _pauseIcon;
    private readonly ToggleButton _audioButton;
    private readonly Button _cancelButton;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _elapsed = new();
    private ScreenRegionRecorder? _recorder;
    private RecordingSessionState _state = RecordingSessionState.Ready;
    private bool _completed;
    private bool _disposed;
    private int _selectedFrameRate = RecordingFrameRatePolicy.DefaultFrameRate;

    internal event Action<RecordingResult?>? Completed;

    internal RecordingControlWindow(RectInt32 region)
    {
        _region = region;
        _workspace = new RecordingWorkspace();
        Title = "Tbox 区域录屏";

        _timeText = Label("00:00");
        _timeText.FontSize = TboxUi.BodyFontSize;
        _timeText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var timeMetric = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        _recordingIndicator = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = TboxUi.Brush(TboxUi.RecordingModeColor),
            VerticalAlignment = VerticalAlignment.Center
        };
        timeMetric.Children.Add(_recordingIndicator);
        timeMetric.Children.Add(_timeText);
        _pausedText = Label("已暂停");
        _pausedText.FontSize = TboxUi.CaptionFontSize;
        _pausedText.Foreground = TboxUi.Brush(Color.FromArgb(255, 255, 207, 112));
        _pausedText.Visibility = Visibility.Collapsed;
        timeMetric.Children.Add(_pausedText);

        _frameRateText = Label($"{_selectedFrameRate} FPS");
        _frameRateText.FontSize = TboxUi.CaptionFontSize;
        var frameRateContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        frameRateContent.Children.Add(_frameRateText);
        frameRateContent.Children.Add(new FontIcon
        {
            Glyph = "\uE70D",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 10
        });
        _frameRateButton = new Button
        {
            Content = frameRateContent,
            Height = TboxUi.StandardControlHeight,
            MinWidth = 76,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(TboxUi.ControlCornerRadius),
            Flyout = CreateFrameRateFlyout()
        };
        TboxUi.ApplyTaskButtonVisual(_frameRateButton, false);
        AutomationProperties.SetName(_frameRateButton, "选择录制帧率");
        ToolTipService.SetToolTip(_frameRateButton, "选择录制帧率；默认 10 FPS");
        _startButton = ShapeButton(new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = TboxUi.Brush(TboxUi.RecordingModeColor)
        }, "开始录制");
        _startButton.Click += async (_, _) => await StartRecordingAsync();
        _stopButton = ShapeButton(new Rectangle
        {
            Width = 12,
            Height = 12,
            RadiusX = 1,
            RadiusY = 1,
            Fill = TboxUi.Brush(TboxUi.RecordingModeColor)
        }, "停止并进入预览");
        _stopButton.Visibility = Visibility.Collapsed;
        _stopButton.Click += async (_, _) => await StopRecordingAsync();
        _pauseIcon = new FontIcon
        {
            Glyph = "\uE769",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16
        };
        _pauseButton = IconButton(_pauseIcon, "暂停录制");
        _pauseButton.Visibility = Visibility.Collapsed;
        _pauseButton.Click += (_, _) => TogglePause();
        _audioButton = new ToggleButton
        {
            Content = new FontIcon { Glyph = "\uE767", FontSize = 16 },
            Width = TboxUi.TaskButtonWidth,
            Height = TboxUi.TaskButtonHeight,
            IsChecked = false,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4)
        };
        TboxUi.ApplyTaskToggleVisual(_audioButton, false);
        UpdateAudioAccessibility(false);
        _audioButton.Checked += (_, _) =>
        {
            TboxUi.ApplyTaskToggleVisual(_audioButton, true);
            UpdateAudioAccessibility(true);
        };
        _audioButton.Unchecked += (_, _) =>
        {
            TboxUi.ApplyTaskToggleVisual(_audioButton, false);
            UpdateAudioAccessibility(false);
        };
        _cancelButton = GlyphButton("\uE711", "取消录屏");
        _cancelButton.Click += async (_, _) => await CancelAsync();
        _statusText = Label(string.Empty);
        _statusText.MaxWidth = 210;
        _statusText.FontSize = TboxUi.CaptionFontSize;
        _statusText.Foreground = TboxUi.Brush(Color.FromArgb(255, 224, 224, 224));
        _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
        _statusText.Visibility = Visibility.Collapsed;

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2
        };
        tools.Children.Add(timeMetric);
        tools.Children.Add(_statusText);
        tools.Children.Add(_frameRateButton);
        tools.Children.Add(_audioButton);
        tools.Children.Add(Separator());
        tools.Children.Add(_startButton);
        tools.Children.Add(_pauseButton);
        tools.Children.Add(_stopButton);
        tools.Children.Add(_cancelButton);
        _root = TboxUi.CreateTaskSurface(tools);
        Content = _root;

        _windowHandle = WindowNative.GetWindowHandle(this);
        NativeMethods.DisableWindowTransitions(_windowHandle);
        NativeMethods.ExcludeWindowFromCapture(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        _appWindow.IsShownInSwitchers = false;
        CaptureModeWindowPlacement.PrepareOnTargetDisplay(_windowHandle, region);

        _frame = new CaptureRegionFrame(region, CaptureFramePalette.Recording);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => _timeText.Text = FormatDuration(_elapsed.Elapsed);

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += async (_, args) =>
        {
            args.Handled = true;
            await CancelAsync();
        };
        _root.KeyboardAccelerators.Add(escape);

        Closed += (_, _) =>
        {
            if (!_completed)
            {
                _completed = true;
                _recorder?.Dispose();
                _workspace.Dispose();
                Completed?.Invoke(null);
            }
            Dispose();
        };
    }

    internal RecordingSessionState State => _state;

    internal void Show()
    {
        _frame.Show();
        _appWindow.Show(activateWindow: true);
        RefreshPlacement();
    }

    private async Task StartRecordingAsync()
    {
        if (_state is not RecordingSessionState.Ready and not RecordingSessionState.Failed) return;
        SetStartingState();
        var requestedAudio = _audioButton.IsChecked == true;
        try
        {
            await StartRecorderAsync(requestedAudio);
        }
        catch (Exception firstException) when (requestedAudio)
        {
            AppLog.Write($"Start recording with system audio failed: {firstException}");
            _recorder?.Dispose();
            _recorder = null;
            _workspace.ResetMasterFile();
            _audioButton.IsChecked = false;
            _statusText.Text = "系统声音不可用，已静音录制";
            try
            {
                await StartRecorderAsync(includeSystemAudio: false);
            }
            catch (Exception fallbackException)
            {
                FailStart(fallbackException);
            }
        }
        catch (Exception exception)
        {
            FailStart(exception);
        }
    }

    private async Task StartRecorderAsync(bool includeSystemAudio)
    {
        _workspace.ResetMasterFile();
        _recorder = new ScreenRegionRecorder(_region, includeSystemAudio, _selectedFrameRate);
        await _recorder.StartAsync(_workspace.MasterFilePath);
        _state = RecordingSessionState.Recording;
        _startButton.Visibility = Visibility.Collapsed;
        _stopButton.Visibility = Visibility.Visible;
        _pauseButton.Visibility = Visibility.Visible;
        _frameRateButton.Visibility = Visibility.Collapsed;
        _audioButton.Visibility = Visibility.Collapsed;
        _audioButton.IsEnabled = false;
        _frameRateButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _pausedText.Visibility = Visibility.Collapsed;
        _recordingIndicator.Fill = TboxUi.Brush(TboxUi.RecordingModeColor);
        _pauseIcon.Glyph = "\uE769";
        UpdatePauseAccessibility("暂停录制");
        if (string.IsNullOrWhiteSpace(_statusText.Text) || _statusText.Text == "正在启动…")
        {
            HideStatus();
        }
        else
        {
            _statusText.Visibility = Visibility.Visible;
        }
        _elapsed.Restart();
        _timer.Start();
        QueuePlacementRefresh();
    }

    private void SetStartingState()
    {
        ShowStatus("正在启动…");
        _startButton.IsEnabled = false;
        _audioButton.IsEnabled = false;
        _frameRateButton.IsEnabled = false;
        _cancelButton.IsEnabled = false;
        QueuePlacementRefresh();
    }

    private void FailStart(Exception exception)
    {
        AppLog.Write($"Start recording failed: {exception}");
        _recorder?.Dispose();
        _recorder = null;
        _state = RecordingSessionState.Failed;
        ShowStatus("无法开始录制，请检查 Windows 媒体功能");
        _startButton.IsEnabled = true;
        _startButton.Visibility = Visibility.Visible;
        _stopButton.Visibility = Visibility.Collapsed;
        _pauseButton.Visibility = Visibility.Collapsed;
        _pausedText.Visibility = Visibility.Collapsed;
        _frameRateButton.Visibility = Visibility.Visible;
        _audioButton.Visibility = Visibility.Visible;
        _audioButton.IsEnabled = true;
        _frameRateButton.IsEnabled = true;
        _cancelButton.IsEnabled = true;
        QueuePlacementRefresh();
    }

    private void TogglePause()
    {
        if (_recorder is null) return;
        try
        {
            if (_state == RecordingSessionState.Recording)
            {
                _recorder.Pause();
                _elapsed.Stop();
                _state = RecordingSessionState.Paused;
                _pauseIcon.Glyph = "\uE768";
                UpdatePauseAccessibility("继续录制");
                _pausedText.Visibility = Visibility.Visible;
                _recordingIndicator.Fill = TboxUi.Brush(Color.FromArgb(255, 255, 176, 32));
                HideStatus();
            }
            else if (_state == RecordingSessionState.Paused)
            {
                _recorder.Resume();
                _elapsed.Start();
                _state = RecordingSessionState.Recording;
                _pauseIcon.Glyph = "\uE769";
                UpdatePauseAccessibility("暂停录制");
                _pausedText.Visibility = Visibility.Collapsed;
                _recordingIndicator.Fill = TboxUi.Brush(TboxUi.RecordingModeColor);
                HideStatus();
            }
            QueuePlacementRefresh();
        }
        catch (Exception exception)
        {
            AppLog.Write($"Pause or resume recording failed: {exception}");
            ShowStatus("无法切换暂停状态");
        }
    }

    private async Task StopRecordingAsync()
    {
        if (_recorder is null || _state is not RecordingSessionState.Recording and not RecordingSessionState.Paused) return;
        _state = RecordingSessionState.Reviewing;
        _elapsed.Stop();
        _timer.Stop();
        _stopButton.IsEnabled = false;
        _pauseButton.IsEnabled = false;
        _cancelButton.IsEnabled = false;
        ShowStatus("正在完成录制…");
        QueuePlacementRefresh();
        try
        {
            var path = await _recorder.StopAsync();
            _recorder.Dispose();
            _recorder = null;
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                throw new InvalidOperationException("录屏文件为空。");
            }
            Complete(new RecordingResult(
                _workspace,
                _region,
                _elapsed.Elapsed,
                _audioButton.IsChecked == true));
        }
        catch (Exception exception)
        {
            AppLog.Write($"Stop recording failed: {exception}");
            _state = RecordingSessionState.Failed;
            ShowStatus("录制未能完成，请取消后重试");
            _cancelButton.IsEnabled = true;
            QueuePlacementRefresh();
        }
    }

    private async Task CancelAsync()
    {
        if (_completed) return;
        _timer.Stop();
        _elapsed.Stop();
        try
        {
            if (_recorder is not null && _state is RecordingSessionState.Recording or RecordingSessionState.Paused)
            {
                await _recorder.StopAsync();
            }
        }
        catch (Exception exception)
        {
            AppLog.Write($"Cancel recording stop failed: {exception}");
        }
        _recorder?.Dispose();
        _recorder = null;
        _workspace.Dispose();
        Complete(null);
    }

    private void Complete(RecordingResult? result)
    {
        if (_completed) return;
        _completed = true;
        Completed?.Invoke(result);
        Close();
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Colors.White),
        FontSize = TboxUi.BodyFontSize,
        VerticalAlignment = VerticalAlignment.Center
    };

    private void UpdateAudioAccessibility(bool enabled)
    {
        var name = enabled ? "录制系统声音（已开启）" : "录制系统声音（已关闭）";
        AutomationProperties.SetName(_audioButton, name);
        ToolTipService.SetToolTip(_audioButton, name);
    }

    private void QueuePlacementRefresh()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_completed && !_disposed) RefreshPlacement();
        });
    }

    private void RefreshPlacement()
    {
        _root.UpdateLayout();
        CaptureModeWindowPlacement.PositionAdjacent(_windowHandle, _root, _region);
    }

    private MenuFlyout CreateFrameRateFlyout()
    {
        var flyout = new MenuFlyout();
        foreach (var framesPerSecond in RecordingFrameRatePolicy.AllowedFrameRates)
        {
            var item = new MenuFlyoutItem
            {
                Text = $"{framesPerSecond} FPS",
                Tag = framesPerSecond
            };
            item.Click += (_, _) =>
            {
                _selectedFrameRate = (int)item.Tag;
                _frameRateText.Text = $"{_selectedFrameRate} FPS";
            };
            flyout.Items.Add(item);
        }
        return flyout;
    }

    private void UpdatePauseAccessibility(string name)
    {
        AutomationProperties.SetName(_pauseButton, name);
        ToolTipService.SetToolTip(_pauseButton, name);
    }

    private void ShowStatus(string text)
    {
        _statusText.Text = text;
        _statusText.Visibility = Visibility.Visible;
    }

    private void HideStatus()
    {
        _statusText.Text = string.Empty;
        _statusText.Visibility = Visibility.Collapsed;
    }

    private static Button GlyphButton(string glyph, string tooltip) => IconButton(new FontIcon
    {
        Glyph = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = 16
    }, tooltip);

    private static Button ShapeButton(Shape shape, string tooltip) => IconButton(shape, tooltip);

    private static Button IconButton(UIElement content, string tooltip)
    {
        var button = new Button
        {
            Content = content,
            Width = TboxUi.TaskButtonWidth,
            Height = TboxUi.TaskButtonHeight,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Foreground = TboxUi.Brush(Colors.White)
        };
        TboxUi.ApplyTaskButtonVisual(button, false);
        AutomationProperties.SetName(button, tooltip);
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private static Border Separator() => new()
    {
        Width = 1,
        Height = 22,
        Margin = new Thickness(4),
        Background = TboxUi.Brush(TboxUi.TaskDividerColor)
    };

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"hh\:mm\:ss")
        : duration.ToString(@"mm\:ss");

    public void Dispose()
    {
        if (_disposed) return;
        _timer.Stop();
        _frame.Dispose();
        _recorder?.Dispose();
        _disposed = true;
    }
}
