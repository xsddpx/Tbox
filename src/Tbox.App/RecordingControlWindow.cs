using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private readonly Border _root;
    private readonly TextBlock _timeText;
    private readonly TextBlock _statusText;
    private readonly Button _startButton;
    private readonly Button _frameRateButton;
    private readonly Button _stopButton;
    private readonly Button _pauseButton;
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
        _timeText.FontSize = 17;
        _frameRateButton = new Button
        {
            Content = $"{_selectedFrameRate} FPS",
            Height = 32,
            MinWidth = 72,
            Flyout = CreateFrameRateFlyout()
        };
        ToolTipService.SetToolTip(_frameRateButton, "选择录制帧率；默认 10 FPS");
        _startButton = IconButton("●", "开始录制");
        _startButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 59, 48));
        _startButton.Click += async (_, _) => await StartRecordingAsync();
        _stopButton = IconButton("■", "停止录制");
        _stopButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 59, 48));
        _stopButton.Visibility = Visibility.Collapsed;
        _stopButton.Click += async (_, _) => await StopRecordingAsync();
        _pauseButton = IconButton("Ⅱ", "暂停录制");
        _pauseButton.Visibility = Visibility.Collapsed;
        _pauseButton.Click += (_, _) => TogglePause();
        _audioButton = new ToggleButton
        {
            Content = "声音关",
            Height = 32,
            MinWidth = 72,
            IsChecked = false
        };
        ToolTipService.SetToolTip(_audioButton, "录制系统播放声音；默认关闭");
        _audioButton.Checked += (_, _) => _audioButton.Content = "声音开";
        _audioButton.Unchecked += (_, _) => _audioButton.Content = "声音关";
        _cancelButton = IconButton("×", "取消录屏");
        _cancelButton.Click += async (_, _) => await CancelAsync();
        _statusText = Label("点击红色圆点开始");
        _statusText.MinWidth = 120;

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Padding = new Thickness(10, 8, 10, 8)
        };
        tools.Children.Add(_timeText);
        tools.Children.Add(_frameRateButton);
        tools.Children.Add(_startButton);
        tools.Children.Add(_stopButton);
        tools.Children.Add(_pauseButton);
        tools.Children.Add(_audioButton);
        tools.Children.Add(_statusText);
        tools.Children.Add(_cancelButton);
        _root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(248, 32, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 112, 112, 112)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = tools
        };
        Content = _root;

        var hwnd = WindowNative.GetWindowHandle(this);
        NativeMethods.DisableWindowTransitions(hwnd);
        NativeMethods.ExcludeWindowFromCapture(hwnd);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        _appWindow.IsShownInSwitchers = false;
        CaptureModeWindowPlacement.PrepareOnTargetDisplay(hwnd, region);

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
        CaptureModeWindowPlacement.PositionAdjacent(
            WindowNative.GetWindowHandle(this),
            _root,
            _region);
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
        _audioButton.IsEnabled = false;
        _frameRateButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        if (string.IsNullOrWhiteSpace(_statusText.Text) || _statusText.Text == "正在启动…")
        {
            _statusText.Text = "录制中";
        }
        _elapsed.Restart();
        _timer.Start();
    }

    private void SetStartingState()
    {
        _statusText.Text = "正在启动…";
        _startButton.IsEnabled = false;
        _audioButton.IsEnabled = false;
        _frameRateButton.IsEnabled = false;
        _cancelButton.IsEnabled = false;
    }

    private void FailStart(Exception exception)
    {
        AppLog.Write($"Start recording failed: {exception}");
        _recorder?.Dispose();
        _recorder = null;
        _state = RecordingSessionState.Failed;
        _statusText.Text = "无法开始录制，请检查 Windows 媒体功能";
        _startButton.IsEnabled = true;
        _startButton.Visibility = Visibility.Visible;
        _audioButton.IsEnabled = true;
        _frameRateButton.IsEnabled = true;
        _cancelButton.IsEnabled = true;
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
                _pauseButton.Content = "▶";
                ToolTipService.SetToolTip(_pauseButton, "继续录制");
                _statusText.Text = "已暂停";
            }
            else if (_state == RecordingSessionState.Paused)
            {
                _recorder.Resume();
                _elapsed.Start();
                _state = RecordingSessionState.Recording;
                _pauseButton.Content = "Ⅱ";
                ToolTipService.SetToolTip(_pauseButton, "暂停录制");
                _statusText.Text = "录制中";
            }
        }
        catch (Exception exception)
        {
            AppLog.Write($"Pause or resume recording failed: {exception}");
            _statusText.Text = "无法切换暂停状态";
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
        _statusText.Text = "正在完成录制…";
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
            _statusText.Text = "录制未能完成，请取消后重试";
            _cancelButton.IsEnabled = true;
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
        VerticalAlignment = VerticalAlignment.Center
    };

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
                _frameRateButton.Content = $"{_selectedFrameRate} FPS";
            };
            flyout.Items.Add(item);
        }
        return flyout;
    }

    private static Button IconButton(string content, string tooltip)
    {
        var button = new Button
        {
            Content = content,
            Width = 34,
            Height = 32,
            Padding = new Thickness(0)
        };
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

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
