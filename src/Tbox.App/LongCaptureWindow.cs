using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private readonly TextBlock _status;
    private readonly TextBlock _sizeText;
    private readonly Button _finishButton;
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

        _status = new TextBlock
        {
            Text = "正在准备长截图…",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 32, 32, 32)),
            Width = 280,
            TextWrapping = TextWrapping.Wrap
        };
        _sizeText = new TextBlock
        {
            Text = $"{region.Width:N0} × {region.Height:N0}",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 45, 45, 45)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 94,
            TextAlignment = TextAlignment.Center,
            FontSize = 13
        };

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Padding = new Thickness(7, 6, 7, 6)
        };
        tools.Children.Add(new TextBlock
        {
            Text = "⠿",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 17,
            Margin = new Thickness(0, 0, 2, 0)
        });
        tools.Children.Add(_sizeText);
        tools.Children.Add(new TextBlock
        {
            Text = "↕",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 45, 45, 45)),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 18,
            Margin = new Thickness(2, 0, 0, 0)
        });
        var direction = new ComboBox
        {
            Width = 72,
            Height = 32,
            Padding = new Thickness(8, 0, 4, 0)
        };
        direction.Items.Add(new ComboBoxItem { Content = "纵向" });
        direction.Items.Add(new ComboBoxItem { Content = "横向", IsEnabled = false });
        direction.SelectedIndex = 0;
        ToolTipService.SetToolTip(direction, "纵向手动滚动，支持向上和向下；横向暂不支持");
        AutomationProperties.SetName(direction, "滚动方向：纵向");
        tools.Children.Add(direction);
        tools.Children.Add(Separator());

        var info = IconButton("\uE946", "查看长截图状态与操作说明", null);
        info.Flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 6,
                Padding = new Thickness(4),
                Children =
                {
                    new TextBlock
                    {
                        Text = "在黄色框内手动向上或向下滚动；停止滚动后会自动拼接稳定画面。",
                        Width = 280,
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    },
                    _status
                }
            }
        };
        tools.Children.Add(info);
        _finishButton = IconButton("✂", "完成并预览", () => Finish(LongCaptureCompletionAction.Preview), useFluentIcon: false);
        _finishButton.IsEnabled = false;
        tools.Children.Add(_finishButton);
        tools.Children.Add(IconButton("\uE70F", "完成并打开预览工具", () => Finish(LongCaptureCompletionAction.Preview)));
        tools.Children.Add(IconButton("\uE718", "完成并贴图", () => Finish(LongCaptureCompletionAction.Pin)));
        tools.Children.Add(IconButton("\uE792", "完成并另存为", () => Finish(LongCaptureCompletionAction.Save)));
        tools.Children.Add(IconButton("\uE740", "完成并全屏预览", () => Finish(LongCaptureCompletionAction.Fullscreen)));
        tools.Children.Add(IconButton("\uE711", "取消长截图", Cancel));
        tools.Children.Add(IconButton("\uE8C8", "完成并复制", () => Finish(LongCaptureCompletionAction.Copy)));

        _root = new Border
        {
            RequestedTheme = ElementTheme.Light,
            Background = new SolidColorBrush(Color.FromArgb(252, 250, 250, 250)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 190, 190, 190)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = tools
        };
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
            _finishButton.IsEnabled = true;
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
        _status.Text = text;
        ToolTipService.SetToolTip(_root, text);
    }

    private static string DirectionText(ScrollCaptureDirection direction) => direction switch
    {
        ScrollCaptureDirection.Up => "刚刚向上滚动",
        ScrollCaptureDirection.Down => "刚刚向下滚动",
        _ => "画面已稳定"
    };

    private static Button IconButton(string glyph, string tooltip, Action? action, bool useFluentIcon = true)
    {
        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily(useFluentIcon ? "Segoe Fluent Icons" : "Segoe UI Symbol"),
                FontSize = 15
            },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 35, 35, 35)),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            CornerRadius = new CornerRadius(4)
        };
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 225, 225, 225));
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(255, 205, 205, 205));
        if (action is not null) button.Click += (_, _) => action();
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private static Border Separator() => new()
    {
        Width = 1,
        Height = 22,
        Margin = new Thickness(4, 5, 4, 5),
        Background = new SolidColorBrush(Color.FromArgb(255, 205, 205, 205))
    };

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
