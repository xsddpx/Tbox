using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Tbox.Core;

namespace Tbox.App;

internal enum CaptureCompletionAction
{
    Pin,
    Copy,
    Save
}

internal sealed record CaptureCompletion(
    CapturedScreen Screen,
    CaptureCompletionAction Action,
    StorageFile? SaveDestination = null);

internal sealed class CaptureOverlayWindow : Window
{
    private readonly CapturedScreen _screen;
    private readonly Grid _root;
    private readonly Canvas _canvas;
    private readonly AppWindow _appWindow;
    private readonly Rectangle _fullShade;
    private readonly Rectangle _topShade;
    private readonly Rectangle _leftShade;
    private readonly Rectangle _rightShade;
    private readonly Rectangle _bottomShade;
    private readonly Rectangle _selection;
    private readonly Canvas _annotationHost;
    private readonly CaptureAnnotationController _annotations;
    private readonly Border _toolbar;
    private readonly TextBlock _hintText;
    private readonly Dictionary<AnnotationTool, Button> _toolButtons = [];
    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private Button _colorButton = null!;
    private TextBlock _widthText = null!;
    private Point? _start;
    private Rect _selectionBounds;
    private CapturedScreen? _selectedScreen;
    private PixelRegion? _selectedRegion;
    private bool _drawingAnnotation;
    private bool _completingSelection;
    private bool _completed;

    internal event Action<CaptureCompletion?>? Completed;

    private CaptureOverlayWindow(CapturedScreen screen, Image image)
    {
        _screen = screen;
        Title = "Tbox 截图";

        _root = new Grid { Background = new SolidColorBrush(Colors.Black), IsTabStop = true };
        image.Stretch = Stretch.Fill;
        _root.Children.Add(image);

        _canvas = new Canvas { Background = new SolidColorBrush(Colors.Transparent) };
        _fullShade = CreateShade();
        _root.Children.Add(_fullShade);
        _topShade = CreateShade(Visibility.Collapsed);
        _leftShade = CreateShade(Visibility.Collapsed);
        _rightShade = CreateShade(Visibility.Collapsed);
        _bottomShade = CreateShade(Visibility.Collapsed);
        _annotationHost = new Canvas { Visibility = Visibility.Collapsed };
        _annotations = new CaptureAnnotationController(_annotationHost);
        _annotations.StateChanged += UpdateAnnotationToolbarState;
        _selection = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212)),
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_topShade);
        _canvas.Children.Add(_leftShade);
        _canvas.Children.Add(_rightShade);
        _canvas.Children.Add(_bottomShade);
        _canvas.Children.Add(_annotationHost);
        _canvas.Children.Add(_selection);
        _toolbar = CreateToolbar();
        _canvas.Children.Add(_toolbar);
        _root.Children.Add(_canvas);

        _hintText = new TextBlock
        {
            Text = "拖动选择截图区域 · Esc 取消",
            Foreground = new SolidColorBrush(Colors.White)
        };
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 25, 25, 25)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 22, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _hintText
        };
        _root.Children.Add(hint);
        Content = _root;

        _canvas.SizeChanged += (_, _) => ResetShade();
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCanceled += OnPointerCanceled;
        _canvas.DoubleTapped += (_, _) => _annotations.FinishPolyline();
        _canvas.RightTapped += (_, _) =>
        {
            if (!_annotations.FinishPolyline()) _annotations.CancelCurrentOperation();
        };

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            if (!_annotations.CancelCurrentOperation()) Complete(null);
        };
        _root.KeyboardAccelerators.Add(escape);
        AddKeyboardAccelerators();

        var hwnd = WindowNative.GetWindowHandle(this);
        NativeMethods.DisableWindowTransitions(hwnd);
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
        NativeMethods.MoveAndResizeWindow(
            hwnd,
            new RectInt32(screen.Left, screen.Top, screen.Width, screen.Height));

        Activated += (_, _) => _root.Focus(FocusState.Programmatic);
        Closed += (_, _) =>
        {
            _annotations.Dispose();
            if (!_completed)
            {
                _completed = true;
                Completed?.Invoke(null);
            }
        };
    }

    internal static async Task<CaptureOverlayWindow> CreateAsync(CapturedScreen screen)
    {
        var source = await screen.CreateBitmapSourceAsync();
        return new CaptureOverlayWindow(screen, new Image { Source = source });
    }

    internal void Show() => _appWindow.Show(activateWindow: true);

    private Border CreateToolbar()
    {
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        AddToolButton(tools, AnnotationTool.Rectangle, "□", "矩形（Shift：正方形）");
        AddToolButton(tools, AnnotationTool.Ellipse, "○", "椭圆（Shift：圆）");
        AddToolButton(tools, AnnotationTool.Line, "╱", "直线（Shift：吸附 45°）");
        AddToolButton(tools, AnnotationTool.Polyline, "⌁", "折线（双击或 Enter 完成）");
        AddToolButton(tools, AnnotationTool.Arrow, "➜", "箭头（Shift：吸附 45°）");
        AddToolButton(tools, AnnotationTool.Pen, "✎", "画笔（B）");
        AddToolButton(tools, AnnotationTool.Highlighter, "▰", "马克笔");
        AddToolButton(tools, AnnotationTool.Mosaic, "▦", "马赛克");
        AddToolButton(tools, AnnotationTool.Text, "T", "文字（T，Ctrl+Enter 提交）");
        AddToolButton(tools, AnnotationTool.Eraser, "⌫", "橡皮擦（只擦除标注）");
        tools.Children.Add(CreateSeparator());

        _colorButton = CreateToolbarButton("●", "标注颜色");
        _colorButton.Foreground = new SolidColorBrush(ToColor(_annotations.ColorArgb));
        var colorFlyout = new Flyout();
        var colors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Padding = new Thickness(8) };
        foreach (var choice in new (uint Value, string Name)[]
                 {
                     (0xFFFF3B30, "红色"),
                     (0xFFFFCC00, "黄色"),
                     (0xFF34C759, "绿色"),
                     (0xFF007AFF, "蓝色"),
                     (0xFF000000, "黑色"),
                     (0xFFFFFFFF, "白色")
                 })
        {
            var swatch = CreateToolbarButton("●", choice.Name);
            swatch.Foreground = new SolidColorBrush(ToColor(choice.Value));
            swatch.Click += (_, _) =>
            {
                _annotations.SetColor(choice.Value);
                colorFlyout.Hide();
            };
            colors.Children.Add(swatch);
        }
        colorFlyout.Content = colors;
        _colorButton.Flyout = colorFlyout;
        tools.Children.Add(_colorButton);

        var thinner = CreateToolbarButton("−", "减小粗细（1）");
        thinner.Click += (_, _) => _annotations.AdjustWidth(-1);
        tools.Children.Add(thinner);
        _widthText = new TextBlock
        {
            Text = "3",
            Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 24,
            TextAlignment = TextAlignment.Center
        };
        tools.Children.Add(_widthText);
        var thicker = CreateToolbarButton("+", "增大粗细（2）");
        thicker.Click += (_, _) => _annotations.AdjustWidth(1);
        tools.Children.Add(thicker);
        tools.Children.Add(CreateSeparator());

        _undoButton = CreateToolbarButton("↶", "撤销（Ctrl+Z）");
        _undoButton.Click += (_, _) => _annotations.Undo();
        tools.Children.Add(_undoButton);
        _redoButton = CreateToolbarButton("↷", "重做（Ctrl+Y）");
        _redoButton.Click += (_, _) => _annotations.Redo();
        tools.Children.Add(_redoButton);
        tools.Children.Add(CreateSeparator());

        var saveButton = CreateToolbarButton("💾", "另存为（Ctrl+S）");
        saveButton.Click += async (_, _) => await SaveSelectionAsync();
        tools.Children.Add(saveButton);
        var copyButton = CreateToolbarButton("复制", "复制并完成（Ctrl+C / Enter）", 50);
        copyButton.Click += async (_, _) => await CompleteSelectionAsync(CaptureCompletionAction.Copy);
        tools.Children.Add(copyButton);
        var pinButton = CreateToolbarButton("贴图", "贴图固定（Ctrl+T / 鼠标中键）", 50);
        pinButton.Click += async (_, _) => await CompleteSelectionAsync(CaptureCompletionAction.Pin);
        tools.Children.Add(pinButton);

        var toolScroller = new ScrollViewer
        {
            Content = tools,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 32, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 72, 72, 72)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5),
            Visibility = Visibility.Collapsed,
            Child = toolScroller
        };
    }

    private void AddToolButton(StackPanel panel, AnnotationTool tool, string content, string toolTip)
    {
        var button = CreateToolbarButton(content, toolTip);
        button.Click += (_, _) => _annotations.SetTool(tool);
        _toolButtons[tool] = button;
        panel.Children.Add(button);
    }

    private static Button CreateToolbarButton(string content, string toolTip, double width = 34)
    {
        var button = new Button
        {
            Content = content,
            Width = width,
            Height = 34,
            Padding = new Thickness(3),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(button, toolTip);
        return button;
    }

    private static Border CreateSeparator() => new()
    {
        Width = 1,
        Height = 26,
        Margin = new Thickness(3, 4, 3, 4),
        Background = new SolidColorBrush(Color.FromArgb(255, 90, 90, 90))
    };

    private void UpdateAnnotationToolbarState()
    {
        foreach (var pair in _toolButtons)
        {
            pair.Value.Background = pair.Key == _annotations.CurrentTool
                ? new SolidColorBrush(Color.FromArgb(255, 0, 120, 212))
                : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }
        _colorButton.Foreground = new SolidColorBrush(ToColor(_annotations.ColorArgb));
        _widthText.Text = Math.Round(_annotations.CurrentWidth).ToString();
        _undoButton.IsEnabled = _annotations.CanUndo;
        _redoButton.IsEnabled = _annotations.CanRedo;
    }

    private void AddKeyboardAccelerators()
    {
        AddKeyboardAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, _annotations.Redo);
        AddKeyboardAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control, _annotations.Undo);
        AddKeyboardAccelerator(VirtualKey.Y, VirtualKeyModifiers.Control, _annotations.Redo);
        AddKeyboardAccelerator(VirtualKey.C, VirtualKeyModifiers.Control,
            () => _ = CompleteSelectionAsync(CaptureCompletionAction.Copy));
        AddKeyboardAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, () => _ = SaveSelectionAsync());
        AddKeyboardAccelerator(VirtualKey.T, VirtualKeyModifiers.Control,
            () => _ = CompleteSelectionAsync(CaptureCompletionAction.Pin));
        AddKeyboardAccelerator(VirtualKey.B, VirtualKeyModifiers.None,
            () => _annotations.SetTool(AnnotationTool.Pen));
        AddKeyboardAccelerator(VirtualKey.T, VirtualKeyModifiers.None,
            () => _annotations.SetTool(AnnotationTool.Text));
        AddKeyboardAccelerator(VirtualKey.Tab, VirtualKeyModifiers.None, _annotations.TogglePairedTool);
        AddKeyboardAccelerator(VirtualKey.Number1, VirtualKeyModifiers.None, () => _annotations.AdjustWidth(-1));
        AddKeyboardAccelerator(VirtualKey.Number2, VirtualKeyModifiers.None, () => _annotations.AdjustWidth(1));
        AddKeyboardAccelerator(VirtualKey.Enter, VirtualKeyModifiers.None, () =>
        {
            if (!_annotations.FinishPolyline()) _ = CompleteSelectionAsync(CaptureCompletionAction.Copy);
        });
    }

    private void AddKeyboardAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            // Let the TextBox own typing, editing, and clipboard shortcuts.
            // Its scoped Ctrl+Enter accelerator commits the annotation.
            if (_annotations.IsEditingText) return;
            args.Handled = true;
            action();
        };
        _root.KeyboardAccelerators.Add(accelerator);
    }

    private static Rectangle CreateShade(Visibility visibility = Visibility.Visible) => new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
        Visibility = visibility
    };

    private static Color ToColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (IsInsideToolbar(source) || _annotations.IsTextEditorElement(source)) return;
        var point = e.GetCurrentPoint(_canvas);
        if (point.Properties.IsMiddleButtonPressed && _selectedScreen is not null)
        {
            _ = CompleteSelectionAsync(CaptureCompletionAction.Pin);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        if (_selectedScreen is not null && _annotations.CurrentTool is not null)
        {
            if (!_selectionBounds.Contains(point.Position)) return;
            var localPoint = ToSelectionPoint(point.Position);
            _drawingAnnotation = _annotations.PointerPressed(localPoint, IsShiftDown());
            if (_drawingAnnotation) _canvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        _annotations.Reset();
        _selectedScreen = null;
        _selectedRegion = null;
        _toolbar.Visibility = Visibility.Collapsed;
        _hintText.Text = "拖动选择截图区域 · Esc 取消";
        _start = Clamp(point.Position);
        _canvas.CapturePointer(e.Pointer);
        UpdateSelection(_start.Value, _start.Value);
        e.Handled = true;
    }

    private bool IsInsideToolbar(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, _toolbar)) return true;
            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_selectedScreen is not null && _annotations.CurrentTool is AnnotationTool.Polyline)
        {
            var position = e.GetCurrentPoint(_canvas).Position;
            if (_selectionBounds.Contains(position))
            {
                _annotations.PointerMoved(ToSelectionPoint(position), IsShiftDown());
            }
        }
        if (_drawingAnnotation)
        {
            _annotations.PointerMoved(
                ToSelectionPoint(ClampToSelection(e.GetCurrentPoint(_canvas).Position)),
                IsShiftDown());
            e.Handled = true;
            return;
        }
        if (_start is null || !e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        UpdateSelection(_start.Value, Clamp(e.GetCurrentPoint(_canvas).Position));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drawingAnnotation)
        {
            _annotations.PointerReleased(
                ToSelectionPoint(ClampToSelection(e.GetCurrentPoint(_canvas).Position)),
                IsShiftDown());
            _canvas.ReleasePointerCapture(e.Pointer);
            _drawingAnnotation = false;
            e.Handled = true;
            return;
        }
        if (_start is null) return;
        var end = Clamp(e.GetCurrentPoint(_canvas).Position);
        var start = _start.Value;
        _canvas.ReleasePointerCapture(e.Pointer);
        _start = null;

        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);
        if (width < 2 || height < 2)
        {
            CancelSelection();
            return;
        }

        var region = CaptureRegionPolicy.MapToPixels(
            _canvas.ActualWidth,
            _canvas.ActualHeight,
            left,
            top,
            width,
            height,
            _screen.Width,
            _screen.Height);
        _selectedScreen = _screen.Crop(region.Left, region.Top, region.Width, region.Height);
        _selectedRegion = region;
        _selectionBounds = new Rect(left, top, width, height);
        Canvas.SetLeft(_annotationHost, left);
        Canvas.SetTop(_annotationHost, top);
        _annotations.Begin(_screen, region, width, height);
        ShowToolbar(left, top, width, height);
        _hintText.Text = "可标注、保存、复制或贴图 · 鼠标中键快速贴图 · Esc 逐级取消";
        e.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_drawingAnnotation)
        {
            _annotations.CancelPointerGesture();
            _drawingAnnotation = false;
            return;
        }
        CancelSelection();
    }

    private void ShowToolbar(double left, double top, double width, double height)
    {
        const double gap = 8;
        _toolbar.Visibility = Visibility.Visible;
        _toolbar.MaxWidth = Math.Max(120, _canvas.ActualWidth - (gap * 2));
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarWidth = Math.Min(_toolbar.DesiredSize.Width, _canvas.ActualWidth);
        var toolbarHeight = _toolbar.DesiredSize.Height;
        var toolbarLeft = Math.Clamp(left + width - toolbarWidth, 0, Math.Max(0, _canvas.ActualWidth - toolbarWidth));
        var below = top + height + gap;
        var toolbarTop = below + toolbarHeight <= _canvas.ActualHeight
            ? below
            : Math.Max(0, top - toolbarHeight - gap);
        Canvas.SetLeft(_toolbar, toolbarLeft);
        Canvas.SetTop(_toolbar, toolbarTop);
        UpdateAnnotationToolbarState();
    }

    private Point Clamp(Point point) => new(
        Math.Clamp(point.X, 0, _canvas.ActualWidth),
        Math.Clamp(point.Y, 0, _canvas.ActualHeight));

    private Point ClampToSelection(Point point) => new(
        Math.Clamp(point.X, _selectionBounds.Left, _selectionBounds.Right),
        Math.Clamp(point.Y, _selectionBounds.Top, _selectionBounds.Bottom));

    private Point ToSelectionPoint(Point point) => new(
        point.X - _selectionBounds.Left,
        point.Y - _selectionBounds.Top);

    private static bool IsShiftDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VkShift) & 0x8000) != 0;

    private void UpdateSelection(Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var right = Math.Max(start.X, end.X);
        var bottom = Math.Max(start.Y, end.Y);
        var width = _canvas.ActualWidth;
        var height = _canvas.ActualHeight;

        _fullShade.Visibility = Visibility.Collapsed;
        SetRectangle(_topShade, 0, 0, width, top);
        SetRectangle(_leftShade, 0, top, left, bottom - top);
        SetRectangle(_rightShade, right, top, width - right, bottom - top);
        SetRectangle(_bottomShade, 0, bottom, width, height - bottom);
        SetRectangle(_selection, left, top, right - left, bottom - top);
    }

    private static void SetRectangle(Rectangle rectangle, double left, double top, double width, double height)
    {
        rectangle.Visibility = Visibility.Visible;
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        rectangle.Width = Math.Max(0, width);
        rectangle.Height = Math.Max(0, height);
    }

    private void ResetShade()
    {
        _fullShade.Visibility = Visibility.Visible;
        _topShade.Visibility = Visibility.Collapsed;
        _leftShade.Visibility = Visibility.Collapsed;
        _rightShade.Visibility = Visibility.Collapsed;
        _bottomShade.Visibility = Visibility.Collapsed;
        _selection.Visibility = Visibility.Collapsed;
        _annotations.Reset();
        _toolbar.Visibility = Visibility.Collapsed;
        _selectedScreen = null;
        _selectedRegion = null;
        _drawingAnnotation = false;
        _hintText.Text = "拖动选择截图区域 · Esc 取消";
    }

    private void CancelSelection()
    {
        _start = null;
        ResetShade();
    }

    private async Task SaveSelectionAsync()
    {
        if (_selectedScreen is null || _completingSelection) return;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = $"Tbox-{DateTime.Now:yyyyMMdd-HHmmss}"
            };
            picker.FileTypeChoices.Add("PNG 图像", [".png"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var destination = await picker.PickSaveFileAsync();
            if (destination is not null)
            {
                await CompleteSelectionAsync(CaptureCompletionAction.Save, destination);
            }
        }
        catch (Exception exception)
        {
            AppLog.Write($"Choose capture save destination failed: {exception}");
            _hintText.Text = "无法打开保存窗口，请重试。";
        }
    }

    private async Task CompleteSelectionAsync(
        CaptureCompletionAction action,
        StorageFile? saveDestination = null)
    {
        if (_selectedScreen is null || _selectedRegion is null || _completingSelection) return;
        _completingSelection = true;
        try
        {
            var rendered = await _annotations.RenderAsync();
            Complete(new CaptureCompletion(rendered, action, saveDestination));
        }
        catch (Exception exception)
        {
            AppLog.Write($"Render capture annotations failed: {exception}");
            _hintText.Text = "无法合成标注，请撤销最近操作后重试。";
            _completingSelection = false;
        }
    }

    private void Complete(CaptureCompletion? result)
    {
        if (_completed) return;
        _completed = true;
        Completed?.Invoke(result);
        Close();
    }
}
