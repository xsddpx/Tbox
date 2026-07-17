using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Tbox.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace Tbox.App;

internal sealed class OcrResultWindowManager(ITextRecognitionService recognitionService) : IDisposable
{
    private OcrResultWindow? _window;
    private bool _disposed;

    internal void Show(
        CapturedScreen screen,
        RectInt32 sourceBounds,
        bool keepAboveSource = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is null)
        {
            _window = new OcrResultWindow(recognitionService, sourceBounds);
            _window.Dismissed += OnWindowDismissed;
        }

        _window.BeginRecognition(screen);
        _window.BringToFront(keepAboveSource);
    }

    internal void CloseAll()
    {
        var window = _window;
        _window = null;
        if (window is null) return;
        window.Dismissed -= OnWindowDismissed;
        window.Close();
    }

    private void OnWindowDismissed(OcrResultWindow window)
    {
        window.Dismissed -= OnWindowDismissed;
        if (ReferenceEquals(_window, window)) _window = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        CloseAll();
        _disposed = true;
    }
}

internal sealed class OcrResultWindow : Window
{
    private const double DefaultWidth = 800;
    private const double DefaultHeight = 600;
    private const double MinimumWidth = 520;
    private const double MinimumHeight = 360;
    private readonly ITextRecognitionService _recognitionService;
    private readonly Grid _root;
    private readonly Grid _contentGrid;
    private readonly Image _previewImage;
    private readonly TextBox _editor;
    private readonly Canvas _lineNumberCanvas;
    private readonly TextBlock _statusText;
    private readonly ProgressRing _progressRing;
    private readonly Button _retryButton;
    private readonly ToggleButton _previewButton;
    private readonly ToggleButton _topmostButton;
    private readonly Button _copyButton;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private CancellationTokenSource? _recognitionCancellation;
    private CapturedScreen? _screen;
    private ScrollViewer? _editorScrollViewer;
    private bool _lineNumberUpdateQueued;
    private bool _isRecognizing;
    private bool _isClosed;

    internal event Action<OcrResultWindow>? Dismissed;

    internal OcrResultWindow(ITextRecognitionService recognitionService, RectInt32 sourceBounds)
    {
        _recognitionService = recognitionService;
        Title = "Tbox 文字识别";

        var palette = TboxUi.Palette;
        var dark = palette.IsDark;
        var background = palette.Background;
        var card = palette.SurfacePrimary;
        var border = palette.BorderDefault;
        var subtle = palette.TextSecondary;

        _root = new Grid
        {
            RequestedTheme = TboxUi.RequestedTheme,
            Background = new SolidColorBrush(background),
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(52) },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            }
        };

        var titleBar = new Grid
        {
            Height = 52,
            Padding = new Thickness(8, 0, 140, 0),
            Background = new SolidColorBrush(background),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            }
        };
        _topmostButton = CreateTitleToggle("\uE718", "窗口置顶");
        _topmostButton.Checked += (_, _) => SetTopmost(true);
        _topmostButton.Unchecked += (_, _) => SetTopmost(false);
        titleBar.Children.Add(_topmostButton);
        var title = new TextBlock
        {
            Text = "文字识别",
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = TboxUi.SectionTitleFontSize
        };
        Grid.SetColumn(title, 1);
        titleBar.Children.Add(title);
        _root.Children.Add(titleBar);

        var body = new Grid
        {
            Padding = new Thickness(12, 4, 12, 12),
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = new GridLength(48) }
            }
        };
        Grid.SetRow(body, 1);
        _root.Children.Add(body);

        _contentGrid = new Grid
        {
            RowSpacing = 8,
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(0) },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            }
        };
        body.Children.Add(_contentGrid);

        _previewImage = new Image { Stretch = Stretch.Uniform };
        var previewBorder = new Border
        {
            Child = _previewImage,
            Background = new SolidColorBrush(card),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8)
        };
        _contentGrid.Children.Add(previewBorder);

        var editorBorder = new Border
        {
            Background = new SolidColorBrush(card),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0)
        };
        Grid.SetRow(editorBorder, 1);
        _contentGrid.Children.Add(editorBorder);

        var editorGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(48) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            }
        };
        editorBorder.Child = editorGrid;
        var gutter = new Border
        {
            Background = new SolidColorBrush(palette.SurfaceSecondary),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(0)
        };
        _lineNumberCanvas = new Canvas { IsHitTestVisible = false };
        gutter.Child = _lineNumberCanvas;
        editorGrid.Children.Add(gutter);

        _editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = true,
            FontSize = TboxUi.BodyFontSize,
            Padding = new Thickness(12, 8, 12, 8),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsEnabled = false
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_editor, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(_editor, "文字识别结果编辑器");
        Grid.SetColumn(_editor, 1);
        editorGrid.Children.Add(_editor);
        _editor.TextChanged += OnEditorTextChanged;
        _editor.SizeChanged += (_, _) => ScheduleLineNumberUpdate();
        _editor.Loaded += (_, _) => AttachEditorScrollViewer();

        var bottomBar = new Grid
        {
            Margin = new Thickness(0, 8, 0, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        Grid.SetRow(bottomBar, 1);
        body.Children.Add(bottomBar);

        _previewButton = CreateBottomToggle("\uEB9F", "原图", "显示原图");
        _previewButton.Checked += (_, _) => SetPreviewVisible(true);
        _previewButton.Unchecked += (_, _) => SetPreviewVisible(false);
        bottomBar.Children.Add(_previewButton);

        _retryButton = new Button
        {
            Content = "重试",
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };
        TboxUi.StyleStandardButton(_retryButton, "重新识别当前原图");
        AutomationProperties.SetName(_retryButton, "重新识别");
        ToolTipService.SetToolTip(_retryButton, "重新识别当前原图");
        _retryButton.Click += (_, _) => RetryRecognition();
        Grid.SetColumn(_retryButton, 1);
        bottomBar.Children.Add(_retryButton);

        var statusHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _progressRing = new ProgressRing { Width = 18, Height = 18, IsActive = false };
        _statusText = new TextBlock
        {
            Text = "等待识别",
            Foreground = new SolidColorBrush(subtle),
            FontSize = TboxUi.CaptionFontSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 430,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusHost.Children.Add(_progressRing);
        statusHost.Children.Add(_statusText);
        Grid.SetColumn(statusHost, 2);
        bottomBar.Children.Add(statusHost);

        _copyButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE8C8", FontSize = 15 },
                    new TextBlock { Text = "复制全部" }
                }
            },
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        TboxUi.StyleStandardButton(_copyButton, "复制编辑器中的全部文字", primary: true);
        AutomationProperties.SetName(_copyButton, "复制全部文字");
        ToolTipService.SetToolTip(_copyButton, "复制编辑器中的全部文字");
        _copyButton.Click += (_, _) => CopyAllText();
        Grid.SetColumn(_copyButton, 3);
        bottomBar.Children.Add(_copyButton);

        Content = _root;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);

        _windowHandle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Tbox.ico"));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.SetBorderAndTitleBar(true, true);
        }
        TboxUi.ConfigureTitleBar(_appWindow, dark);
        PlaceNearSource(sourceBounds);

        var copyAccelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = VirtualKey.C,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift
        };
        copyAccelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            CopyAllText();
        };
        _root.KeyboardAccelerators.Add(copyAccelerator);

        Closed += (_, _) =>
        {
            if (_isClosed) return;
            _isClosed = true;
            _recognitionCancellation?.Cancel();
            _recognitionCancellation?.Dispose();
            Dismissed?.Invoke(this);
        };
    }

    internal void BringToFront(bool keepAboveSource)
    {
        if (keepAboveSource)
        {
            _topmostButton.IsChecked = true;
            SetTopmost(true);
        }
        _appWindow.Show(activateWindow: true);
        Activate();
        _ = NativeMethods.SetForegroundWindow(_windowHandle);
    }

    internal void BeginRecognition(CapturedScreen screen)
    {
        if (_isClosed) return;
        _screen = screen;
        _recognitionCancellation?.Cancel();
        _recognitionCancellation?.Dispose();
        _recognitionCancellation = new CancellationTokenSource();
        _ = RecognizeAsync(screen, _recognitionCancellation.Token);
    }

    private async Task RecognizeAsync(CapturedScreen screen, CancellationToken cancellationToken)
    {
        _isRecognizing = true;
        _editor.IsEnabled = false;
        _copyButton.IsEnabled = false;
        _retryButton.Visibility = Visibility.Collapsed;
        _progressRing.IsActive = true;
        _progressRing.Visibility = Visibility.Visible;
        _statusText.Text = "识别中…";
        SetEditorText(string.Empty);
        ScheduleLineNumberUpdate();

        try
        {
            _previewImage.Source = await screen.CreateBitmapSourceAsync(maximumDecodeDimension: 8192);
            var result = await _recognitionService.RecognizeAsync(
                TextRecognitionImage.FromCapturedScreen(screen),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            SetEditorText(result.Text);
            _editor.IsEnabled = true;
            _copyButton.IsEnabled = result.Text.Length > 0;
            _statusText.Text = result.Text.Length == 0
                ? $"Windows OCR · {result.LanguageTag} · 未识别到文字"
                : $"Windows OCR · {result.LanguageTag} · {result.Lines.Count} 行";
            _retryButton.Visibility = result.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (result.Text.Length > 0)
            {
                _editor.Focus(FocusState.Programmatic);
                _editor.SelectAll();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (TextRecognitionUnavailableException exception)
        {
            ShowRecognitionError(exception.Message);
        }
        catch (Exception exception)
        {
            AppLog.Write($"OCR failed: {exception}");
            ShowRecognitionError("文字识别失败。请确认原图清晰且 Windows 语言组件可用，然后重试。");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _isRecognizing = false;
                _progressRing.IsActive = false;
                _progressRing.Visibility = Visibility.Collapsed;
                ScheduleLineNumberUpdate();
            }
        }
    }

    private void ShowRecognitionError(string message)
    {
        _editor.IsEnabled = true;
        _copyButton.IsEnabled = false;
        _statusText.Text = message;
        _retryButton.Visibility = Visibility.Visible;
    }

    private void RetryRecognition()
    {
        if (_screen is not null && !_isRecognizing) BeginRecognition(_screen);
    }

    private void CopyAllText()
    {
        if (_editor.Text.Length == 0) return;
        var package = new DataPackage();
        package.SetText(_editor.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        _statusText.Text = "已复制全部文字";
    }

    private void SetEditorText(string value)
    {
        _editor.Text = value;
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        _copyButton.IsEnabled = _editor.Text.Length > 0;
        ScheduleLineNumberUpdate();
    }

    private void AttachEditorScrollViewer()
    {
        _editorScrollViewer = FindDescendant<ScrollViewer>(_editor);
        if (_editorScrollViewer is not null)
        {
            _editorScrollViewer.ViewChanged += (_, _) => ScheduleLineNumberUpdate();
        }
        ScheduleLineNumberUpdate();
    }

    private void ScheduleLineNumberUpdate()
    {
        if (_lineNumberUpdateQueued) return;
        _lineNumberUpdateQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _lineNumberUpdateQueued = false;
            if (_isClosed) return;
            _editor.UpdateLayout();
            UpdateLineNumbers();
        });
    }

    private void UpdateLineNumbers()
    {
        if (!_editor.IsLoaded || _lineNumberCanvas.ActualHeight <= 0) return;
        _lineNumberCanvas.Children.Clear();
        var starts = OcrLineNumberPolicy.GetLogicalLineStarts(_editor.Text);
        for (var lineIndex = 0; lineIndex < starts.Count; lineIndex++)
        {
            var y = GetLineY(starts[lineIndex], lineIndex);
            if (y < -28 || y > _lineNumberCanvas.ActualHeight + 8) continue;
            var number = new TextBlock
            {
                Text = (lineIndex + 1).ToString(),
                Width = 38,
                TextAlignment = TextAlignment.Right,
                FontSize = TboxUi.CaptionFontSize,
                Opacity = 0.42,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(number, 0);
            Canvas.SetTop(number, y);
            _lineNumberCanvas.Children.Add(number);
        }
    }

    private double GetLineY(int characterIndex, int lineIndex)
    {
        try
        {
            if (_editor.Text.Length > 0)
            {
                var safeIndex = Math.Min(characterIndex, _editor.Text.Length - 1);
                var rect = _editor.GetRectFromCharacterIndex(safeIndex, false);
                if (characterIndex == _editor.Text.Length && _editor.Text.EndsWith('\n'))
                {
                    return rect.Y + Math.Max(24, rect.Height);
                }
                return rect.Y;
            }
        }
        catch (ArgumentException)
        {
        }
        return _editor.Padding.Top + lineIndex * 27;
    }

    private void SetPreviewVisible(bool visible)
    {
        _contentGrid.RowDefinitions[0].Height = visible
            ? new GridLength(35, GridUnitType.Star)
            : new GridLength(0);
        _contentGrid.RowDefinitions[1].Height = visible
            ? new GridLength(65, GridUnitType.Star)
            : new GridLength(1, GridUnitType.Star);
    }

    private void SetTopmost(bool value)
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = value;
        }
    }

    private void PlaceNearSource(RectInt32 sourceBounds)
    {
        var sourceCenter = new PointInt32(
            sourceBounds.X + Math.Max(1, sourceBounds.Width) / 2,
            sourceBounds.Y + Math.Max(1, sourceBounds.Height) / 2);
        var display = DisplayArea.GetFromPoint(sourceCenter, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var width = Math.Min((int)DefaultWidth, Math.Max(1, work.Width - 32));
        var height = Math.Min((int)DefaultHeight, Math.Max(1, work.Height - 32));
        var x = Math.Clamp(sourceCenter.X - width / 2, work.X, work.X + work.Width - width);
        var y = Math.Clamp(sourceCenter.Y - height / 2, work.Y, work.Y + work.Height - height);
        NativeMethods.MoveAndResizeWindow(_windowHandle, new RectInt32(x, y, width, height));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)MinimumWidth;
            presenter.PreferredMinimumHeight = (int)MinimumHeight;
        }
    }

    private static ToggleButton CreateTitleToggle(string glyph, string toolTip)
    {
        var button = new ToggleButton
        {
            RequestedTheme = TboxUi.RequestedTheme,
            Width = TboxUi.TaskButtonWidth,
            Height = TboxUi.StandardControlHeight,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Content = new FontIcon { Glyph = glyph, FontSize = 16 },
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, toolTip);
        ToolTipService.SetToolTip(button, toolTip);
        return button;
    }

    private static ToggleButton CreateBottomToggle(string glyph, string text, string toolTip)
    {
        var button = new ToggleButton
        {
            RequestedTheme = TboxUi.RequestedTheme,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = glyph, FontSize = 15 },
                    new TextBlock { Text = text }
                }
            },
            MinHeight = TboxUi.StandardControlHeight,
            FontSize = TboxUi.BodyFontSize,
            CornerRadius = new CornerRadius(TboxUi.ControlCornerRadius),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, toolTip);
        ToolTipService.SetToolTip(button, toolTip);
        return button;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
