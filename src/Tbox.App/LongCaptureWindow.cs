using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Tbox.Core;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace Tbox.App;

internal enum LongCaptureCompletionAction
{
    Preview,
    Copy,
    Pin,
    Save,
    Fullscreen
}

internal sealed record LongCaptureResult(CapturedScreen Screen, LongCaptureCompletionAction Action);

internal sealed class LongCaptureWindow : Window, IDisposable
{
    private readonly RectInt32 _region;
    private readonly CaptureRegionFrame _frame;
    private readonly LongCapturePreviewWindow _preview;
    private readonly ScrollCaptureStitcher _stitcher;
    private readonly DispatcherTimer _timer;
    private readonly Border _root;
    private readonly TextBlock _sizeText;
    private readonly Button _pinButton;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private byte[]? _pendingFrame;
    private bool _capturing;
    private bool _hasAppended;
    private bool _completed;
    private bool _disposed;
    private bool _previewRefreshRunning;
    private bool _previewRefreshRequested;
    private int _consecutiveDuplicates;
    private int _consecutiveNoMatches;
    private ScrollCaptureDirection _direction;

    internal event Action<LongCaptureResult?>? Completed;

    internal LongCaptureWindow(RectInt32 region)
    {
        _region = region;
        _stitcher = new ScrollCaptureStitcher(region.Width, region.Height);
        Title = "Tbox 长截图";

        _sizeText = new TextBlock
        {
            Text = $"{region.Width:N0} × {region.Height:N0}",
            Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 94,
            TextAlignment = TextAlignment.Center,
            FontSize = TboxUi.CaptionFontSize
        };

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2
        };
        var sizeMetric = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        sizeMetric.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = TboxUi.Brush(TboxUi.LongCaptureModeColor),
            VerticalAlignment = VerticalAlignment.Center
        });
        sizeMetric.Children.Add(_sizeText);
        tools.Children.Add(sizeMetric);
        tools.Children.Add(AnnotationToolbar.CreateSeparator());

        _pinButton = AddActionButton(tools, "\uE718", "完成并贴图", LongCaptureCompletionAction.Pin);
        _pinButton.IsEnabled = false;
        AddActionButton(tools, "\uE792", "完成并另存为", LongCaptureCompletionAction.Save);
        AddActionButton(tools, "\uE740", "完成并全屏预览", LongCaptureCompletionAction.Fullscreen);
        var cancelButton = AnnotationToolbar.CreateButton("\uE711", "取消长截图", useFluentIcon: true);
        cancelButton.Click += (_, _) => Cancel();
        tools.Children.Add(cancelButton);
        AddActionButton(tools, "\uE8C8", "完成并复制", LongCaptureCompletionAction.Copy);

        _root = AnnotationToolbar.CreateToolbarRoot(Visibility.Visible);
        _root.Child = tools;
        ScrollViewer.SetHorizontalScrollBarVisibility(_root, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_root, ScrollBarVisibility.Disabled);
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

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            Cancel();
        };
        _root.KeyboardAccelerators.Add(escape);

        _frame = new CaptureRegionFrame(region, CaptureFramePalette.LongCapture);
        _preview = new LongCapturePreviewWindow(region);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += OnCaptureTick;
        Closed += (_, _) =>
        {
            if (!_completed) Complete(null);
            Dispose();
        };
    }

    internal async Task StartAsync()
    {
        _frame.Show();
        _preview.Show();
        _appWindow.Show(activateWindow: false);
        CaptureModeWindowPlacement.PositionAdjacent(_windowHandle, _root, _region);
        await Task.Delay(200);
        if (_completed) return;
        try
        {
            var first = ScreenCaptureService.CaptureRegion(_region);
            _stitcher.AddFrame(first.Pixels);
            _pinButton.IsEnabled = true;
            UpdateSize();
            SetStatus($"已捕获 {_stitcher.Height:N0}px；请在黄色框内缓慢向上或向下滚动。");
            await _preview.UpdateImageAsync(first, 0, _region.Height, ScrollCaptureDirection.None);
            _timer.Start();
        }
        catch (Exception exception)
        {
            AppLog.Write($"Start long capture failed: {exception}");
            SetStatus("无法读取选区画面，请取消后重试。");
        }
    }

    private async void OnCaptureTick(object? sender, object e)
    {
        if (_capturing || _completed) return;
        _capturing = true;
        try
        {
            await Task.Yield();
            var current = ScreenCaptureService.CaptureRegion(_region).Pixels;
            if (_pendingFrame is null)
            {
                _pendingFrame = current;
                return;
            }

            if (!ScrollCaptureStitcher.AreFramesVisuallyStable(_pendingFrame, current))
            {
                _pendingFrame = current;
                return;
            }

            _pendingFrame = null;
            var result = _stitcher.AddFrame(current);
            switch (result.Decision)
            {
                case ScrollCaptureFrameDecision.Appended:
                    _hasAppended = true;
                    _consecutiveDuplicates = 0;
                    _consecutiveNoMatches = 0;
                    _direction = result.Direction;
                    UpdateSize();
                    _preview.UpdateViewport(result.ViewportTop, _region.Height, result.TotalHeight, result.Direction);
                    QueuePreviewRefresh();
                    SetStatus($"已拼接 {result.TotalHeight:N0}px；{DirectionText(result.Direction)}，可继续双向滚动或完成。");
                    break;
                case ScrollCaptureFrameDecision.Moved:
                    _consecutiveDuplicates = 0;
                    _consecutiveNoMatches = 0;
                    _direction = result.Direction;
                    _preview.UpdateViewport(result.ViewportTop, _region.Height, result.TotalHeight, result.Direction);
                    SetStatus($"已在现有内容中{DirectionText(result.Direction)}；可继续滚动或完成。");
                    break;
                case ScrollCaptureFrameDecision.NoMatch:
                    _consecutiveDuplicates = 0;
                    _consecutiveNoMatches++;
                    if (_consecutiveNoMatches >= 3)
                    {
                        SetStatus("暂时未匹配到重叠内容，请反向回退少量并放慢滚动，或完成当前结果。");
                    }
                    break;
                case ScrollCaptureFrameDecision.Duplicate:
                    _consecutiveNoMatches = 0;
                    _consecutiveDuplicates++;
                    if (_hasAppended && _consecutiveDuplicates >= 8)
                    {
                        SetStatus($"已拼接 {_stitcher.Height:N0}px；画面已稳定，可完成或继续向上/向下滚动。");
                    }
                    break;
                case ScrollCaptureFrameDecision.LimitReached:
                    SetStatus("已达到长截图尺寸上限，正在完成…");
                    Finish(LongCaptureCompletionAction.Preview);
                    break;
            }
        }
        catch (Exception exception)
        {
            AppLog.Write($"Capture long screenshot frame failed: {exception}");
            SetStatus("本次取帧失败，请停止滚动后继续或完成当前结果。");
        }
        finally
        {
            _capturing = false;
        }
    }

    private void QueuePreviewRefresh()
    {
        _previewRefreshRequested = true;
        if (!_previewRefreshRunning) _ = RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        _previewRefreshRunning = true;
        try
        {
            do
            {
                _previewRefreshRequested = false;
                var screen = BuildCurrentScreen();
                var viewportTop = _stitcher.ViewportTop;
                var direction = _direction;
                await _preview.UpdateImageAsync(screen, viewportTop, _region.Height, direction);
            }
            while (_previewRefreshRequested && !_completed);
        }
        catch (Exception exception)
        {
            AppLog.Write($"Refresh long capture preview failed: {exception}");
        }
        finally
        {
            _previewRefreshRunning = false;
        }
    }

    private void Finish(LongCaptureCompletionAction action)
    {
        if (_completed || !_stitcher.HasFrame) return;
        try
        {
            Complete(new LongCaptureResult(BuildCurrentScreen(), action));
        }
        catch (Exception exception)
        {
            AppLog.Write($"Complete long screenshot failed: {exception}");
            SetStatus("无法生成长截图，请取消后重试。");
        }
    }

    private CapturedScreen BuildCurrentScreen() => new(
        _region.X,
        _region.Y,
        _stitcher.Width,
        _stitcher.Height,
        _stitcher.Complete());

    private void Cancel() => Complete(null);

    private void Complete(LongCaptureResult? result)
    {
        if (_completed) return;
        _completed = true;
        _timer.Stop();
        Completed?.Invoke(result);
        Close();
    }

    private void UpdateSize() => _sizeText.Text = $"{_stitcher.Width:N0} × {_stitcher.Height:N0}";

    private void SetStatus(string text)
    {
        ToolTipService.SetToolTip(_root, text);
        AutomationProperties.SetHelpText(_root, text);
    }

    private static string DirectionText(ScrollCaptureDirection direction) => direction switch
    {
        ScrollCaptureDirection.Up => "刚刚向上滚动",
        ScrollCaptureDirection.Down => "刚刚向下滚动",
        _ => "画面已稳定"
    };

    private Button AddActionButton(
        StackPanel tools,
        string glyph,
        string tooltip,
        LongCaptureCompletionAction action)
    {
        var button = AnnotationToolbar.CreateButton(glyph, tooltip, useFluentIcon: true);
        button.Click += (_, _) => Finish(action);
        tools.Children.Add(button);
        return button;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _timer.Stop();
        _timer.Tick -= OnCaptureTick;
        _frame.Dispose();
        _preview.Dispose();
        _disposed = true;
    }
}
