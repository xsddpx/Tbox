using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    Save,
    RecognizeText
}

internal sealed record CaptureCompletion(
    CapturedScreen Screen,
    CaptureCompletionAction Action,
    StorageFile? SaveDestination = null);

internal enum CaptureModeKind
{
    LongCapture,
    Recording
}

internal readonly record struct CaptureModeRequest(CaptureModeKind Kind, RectInt32 Bounds);

internal sealed class CaptureOverlayWindow : Window
{
    private const double SelectionHandleSize = 8;
    private const double SelectionHandleHitTolerance = 7;
    private const double MinimumAdjustedSelectionSize = 8;
    private readonly CapturedScreen _screen;
    private readonly CaptureTargetDetector _targetDetector;
    private readonly Grid _root;
    private readonly CursorCanvas _canvas;
    private readonly AppWindow _appWindow;
    private readonly Rectangle _fullShade;
    private readonly Rectangle _topShade;
    private readonly Rectangle _leftShade;
    private readonly Rectangle _rightShade;
    private readonly Rectangle _bottomShade;
    private readonly Rectangle _selection;
    private readonly Canvas _annotationHost;
    private readonly CaptureAnnotationController _annotations;
    private readonly AnnotationToolbar _annotationToolbar;
    private readonly Border _toolbar;
    private readonly TextBlock _hintText;
    private readonly Dictionary<CaptureSelectionHandle, Rectangle> _selectionHandles = [];
    private Point? _start;
    private Rect _selectionBounds;
    private CapturedScreen? _selectedScreen;
    private PixelRegion? _selectedRegion;
    private PixelRegion? _detectedRegion;
    private PixelRegion? _pressedDetectedRegion;
    private CaptureDetectionMode _detectionMode = CaptureDetectionMode.Window;
    private bool _selectionFromDetectedTarget;
    private bool _drawingAnnotation;
    private CaptureSelectionHandle _adjustingSelection = CaptureSelectionHandle.None;
    private CaptureSelectionBounds _adjustmentStartBounds;
    private bool _toolbarVisibleBeforeAdjustment;
    private bool _completingSelection;
    private bool _completed;

    internal event Action<CaptureCompletion?>? Completed;
    internal event Action<CaptureModeRequest>? ModeRequested;
    internal event Action? SelectionStarted;
    internal event Action? RestoreLastRegionRequested;
    internal event Action<CaptureDetectionMode>? DetectionModeChangeRequested;

    private CaptureOverlayWindow(
        CapturedScreen screen,
        Image image,
        CaptureTargetDetector targetDetector)
    {
        _screen = screen;
        _targetDetector = targetDetector;
        Title = "Tbox 截图";

        _root = new Grid
        {
            Background = new SolidColorBrush(Colors.Black),
            IsTabStop = true,
            KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden
        };
        image.Stretch = Stretch.Fill;
        _root.Children.Add(image);

        _canvas = new CursorCanvas { Background = new SolidColorBrush(Colors.Transparent) };
        _canvas.SetCursor(InputSystemCursorShape.Cross);
        _fullShade = CreateShade();
        _root.Children.Add(_fullShade);
        _topShade = CreateShade(Visibility.Collapsed);
        _leftShade = CreateShade(Visibility.Collapsed);
        _rightShade = CreateShade(Visibility.Collapsed);
        _bottomShade = CreateShade(Visibility.Collapsed);
        _annotationHost = new Canvas { Visibility = Visibility.Collapsed };
        _annotations = new CaptureAnnotationController(_annotationHost);
        _annotationToolbar = new AnnotationToolbar(
            _annotations,
            new AnnotationToolbarActions(
                () => CompleteSelectionAsync(CaptureCompletionAction.RecognizeText),
                SaveSelectionAsync,
                () => CompleteSelectionAsync(CaptureCompletionAction.Copy),
                "复制并完成（Ctrl+C / Enter）",
                () => CompleteSelectionAsync(CaptureCompletionAction.Pin),
                "\uE718",
                "贴图固定（Ctrl+T / 鼠标中键）",
                StartLongCapture: StartLongCaptureAsync,
                StartRecording: StartRecordingAsync),
            Visibility.Collapsed);
        _selection = new Rectangle
        {
            Stroke = new SolidColorBrush(CaptureFramePalette.Screenshot),
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_topShade);
        _canvas.Children.Add(_leftShade);
        _canvas.Children.Add(_rightShade);
        _canvas.Children.Add(_bottomShade);
        _canvas.Children.Add(_annotationHost);
        _canvas.Children.Add(_selection);
        foreach (var handle in Enum.GetValues<CaptureSelectionHandle>().Where(handle => handle != CaptureSelectionHandle.None))
        {
            var visual = CreateSelectionHandle();
            _selectionHandles.Add(handle, visual);
            _canvas.Children.Add(visual);
        }
        _toolbar = _annotationToolbar.View;
        _canvas.Children.Add(_toolbar);
        _root.Children.Add(_canvas);

        _hintText = new TextBlock
        {
            Text = CreateReadyHint(),
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

        _canvas.SizeChanged += (_, _) =>
        {
            ResetShade();
            UpdateDetectedTargetFromCursor();
        };
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCanceled += OnPointerCanceled;
        _canvas.PointerExited += (_, _) =>
        {
            if (_adjustingSelection == CaptureSelectionHandle.None)
            {
                _canvas.SetCursor(InputSystemCursorShape.Cross);
            }
        };
        _canvas.RightTapped += (_, _) => _annotations.CancelCurrentOperation();
        _root.PreviewKeyDown += OnRootPreviewKeyDown;

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
            _annotationToolbar.Dispose();
            _annotations.Dispose();
            if (!_completed)
            {
                _completed = true;
                Completed?.Invoke(null);
            }
        };
    }

    internal static async Task<CaptureOverlayWindow> CreateAsync(
        CapturedScreen screen,
        CaptureTargetDetector targetDetector)
    {
        var source = await screen.CreateBitmapSourceAsync();
        return new CaptureOverlayWindow(screen, new Image { Source = source }, targetDetector);
    }

    internal void Show(bool activate) => _appWindow.Show(activateWindow: activate);

    internal CaptureDisplayBounds DisplayBounds => new(
        _screen.Left,
        _screen.Top,
        _screen.Width,
        _screen.Height);

    internal void SetDetectionMode(CaptureDetectionMode mode)
    {
        if (_completed) return;
        _detectionMode = mode;
        if (_selectedScreen is null)
        {
            _hintText.Text = CreateReadyHint();
            UpdateDetectedTargetFromCursor();
            return;
        }

        var cursor = ScreenCaptureService.GetCursorPosition();
        if (_selectionFromDetectedTarget &&
            !_annotations.HasAnnotations &&
            DisplayBounds.Contains(cursor.X, cursor.Y))
        {
            _annotations.Reset();
            _selectedScreen = null;
            _selectedRegion = null;
            _selectionFromDetectedTarget = false;
            _toolbar.Visibility = Visibility.Collapsed;
            UpdateDetectedTargetFromCursor();
            if (_detectedRegion is { } detected)
            {
                ApplySelectionRegion(detected, selectionFromDetectedTarget: true);
            }
        }
        _hintText.Text = _detectionMode == CaptureDetectionMode.Window
            ? "已切换为窗口检测"
            : "已切换为界面元素检测 · 无法识别时回退到窗口";
    }

    internal bool TryRestoreSelection(CaptureTargetBounds desktopBounds)
    {
        if (_completed) return false;
        var region = CaptureTargetPolicy.MapClippedToDisplay(DisplayBounds, desktopBounds);
        if (region is null) return false;
        ApplySelectionRegion(region.Value, selectionFromDetectedTarget: false);
        _hintText.Text = "已恢复上一次成功截图区域 · 拖动边框或控制点可调整";
        return true;
    }

    internal void ShowStatus(string message) => _hintText.Text = message;

    internal void ResetForPeerSelection()
    {
        if (_completed) return;
        _canvas.ReleasePointerCaptures();
        CancelSelection();
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
        AddKeyboardAccelerator(VirtualKey.R, VirtualKeyModifiers.None, () =>
            RestoreLastRegionRequested?.Invoke());
        AddKeyboardAccelerator(VirtualKey.Space, VirtualKeyModifiers.None, ToggleToolbar);
        AddKeyboardAccelerator(VirtualKey.Number1, VirtualKeyModifiers.None, () => _annotations.AdjustWidth(-1));
        AddKeyboardAccelerator(VirtualKey.Number2, VirtualKeyModifiers.None, () => _annotations.AdjustWidth(1));
        AddKeyboardAccelerator(VirtualKey.Enter, VirtualKeyModifiers.None,
            () => _ = CompleteSelectionAsync(CaptureCompletionAction.Copy));
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

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || _annotations.IsEditingText) return;
        e.Handled = true;
        DetectionModeChangeRequested?.Invoke(CaptureTargetPolicy.Toggle(_detectionMode));
    }

    private static Rectangle CreateShade(Visibility visibility = Visibility.Visible) => new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
        Visibility = visibility
    };

    private static Rectangle CreateSelectionHandle() => new()
    {
        Width = SelectionHandleSize,
        Height = SelectionHandleSize,
        Fill = new SolidColorBrush(Colors.White),
        Stroke = new SolidColorBrush(CaptureFramePalette.Screenshot),
        StrokeThickness = 1.5,
        RadiusX = 1,
        RadiusY = 1,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };

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

        if (_selectedScreen is not null && TryBeginSelectionAdjustment(point.Position))
        {
            _canvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (_selectedScreen is not null && _annotations.CurrentTool is not null)
        {
            if (!_selectionBounds.Contains(point.Position)) return;
            var localPoint = ToSelectionPoint(point.Position);
            _drawingAnnotation = _annotations.PointerPressed(localPoint, IsShiftDown());
            if (_drawingAnnotation) _canvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        _pressedDetectedRegion = _detectedRegion;
        _annotations.Reset();
        _selectedScreen = null;
        _selectedRegion = null;
        _toolbar.Visibility = Visibility.Collapsed;
        HideSelectionHandles();
        _hintText.Text = CreateReadyHint();
        SelectionStarted?.Invoke();
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

    private bool TryBeginSelectionAdjustment(Point point)
    {
        var handle = CaptureSelectionAdjustmentPolicy.HitTest(
            ToAdjustmentBounds(_selectionBounds),
            point.X,
            point.Y,
            SelectionHandleHitTolerance);
        if (handle == CaptureSelectionHandle.None) return false;

        _annotations.PrepareForRegionAdjustment();
        _adjustingSelection = handle;
        _adjustmentStartBounds = ToAdjustmentBounds(_selectionBounds);
        _selectionFromDetectedTarget = false;
        _toolbarVisibleBeforeAdjustment = _toolbar.Visibility == Visibility.Visible;
        _toolbar.Visibility = Visibility.Collapsed;
        _canvas.SetCursor(CursorForHandle(handle));
        return true;
    }

    private void UpdateSelectionAdjustment(Point point, bool finalize)
    {
        var adjusted = CaptureSelectionAdjustmentPolicy.Resize(
            _adjustmentStartBounds,
            _adjustingSelection,
            point.X,
            point.Y,
            _canvas.ActualWidth,
            _canvas.ActualHeight,
            MinimumAdjustedSelectionSize,
            MinimumAdjustedSelectionSize);
        if (finalize)
        {
            CommitAdjustedSelection(adjusted);
        }
        else
        {
            PreviewAdjustedSelection(adjusted);
        }
    }

    private void PreviewAdjustedSelection(CaptureSelectionBounds bounds)
    {
        var region = CaptureRegionPolicy.MapToPixels(
            _canvas.ActualWidth,
            _canvas.ActualHeight,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            _screen.Width,
            _screen.Height);
        _selectedRegion = region;
        _selectionBounds = new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        UpdateSelection(
            new Point(_selectionBounds.Left, _selectionBounds.Top),
            new Point(_selectionBounds.Right, _selectionBounds.Bottom));
        Canvas.SetLeft(_annotationHost, _selectionBounds.Left);
        Canvas.SetTop(_annotationHost, _selectionBounds.Top);
        _annotations.UpdateRegion(
            _screen,
            region,
            _selectionBounds.Width,
            _selectionBounds.Height);
        PositionSelectionHandles();
    }

    private void CommitAdjustedSelection(CaptureSelectionBounds bounds)
    {
        var region = CaptureRegionPolicy.MapToPixels(
            _canvas.ActualWidth,
            _canvas.ActualHeight,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            _screen.Width,
            _screen.Height);
        _selectedRegion = region;
        _selectedScreen = _screen.Crop(region.Left, region.Top, region.Width, region.Height);
        _selectionBounds = PixelRegionToCanvasBounds(region);
        UpdateSelection(
            new Point(_selectionBounds.Left, _selectionBounds.Top),
            new Point(_selectionBounds.Right, _selectionBounds.Bottom));
        Canvas.SetLeft(_annotationHost, _selectionBounds.Left);
        Canvas.SetTop(_annotationHost, _selectionBounds.Top);
        _annotations.UpdateRegion(
            _screen,
            region,
            _selectionBounds.Width,
            _selectionBounds.Height);
        PositionSelectionHandles();
    }

    private void UpdateSelectionCursor(Point point)
    {
        var handle = CaptureSelectionAdjustmentPolicy.HitTest(
            ToAdjustmentBounds(_selectionBounds),
            point.X,
            point.Y,
            SelectionHandleHitTolerance);
        _canvas.SetCursor(handle == CaptureSelectionHandle.None
            ? InputSystemCursorShape.Cross
            : CursorForHandle(handle));
    }

    private static InputSystemCursorShape CursorForHandle(CaptureSelectionHandle handle) => handle switch
    {
        CaptureSelectionHandle.Left or CaptureSelectionHandle.Right => InputSystemCursorShape.SizeWestEast,
        CaptureSelectionHandle.Top or CaptureSelectionHandle.Bottom => InputSystemCursorShape.SizeNorthSouth,
        CaptureSelectionHandle.TopLeft or CaptureSelectionHandle.BottomRight =>
            InputSystemCursorShape.SizeNorthwestSoutheast,
        CaptureSelectionHandle.TopRight or CaptureSelectionHandle.BottomLeft =>
            InputSystemCursorShape.SizeNortheastSouthwest,
        _ => InputSystemCursorShape.Cross
    };

    private static CaptureSelectionBounds ToAdjustmentBounds(Rect bounds) => new(
        bounds.Left,
        bounds.Top,
        bounds.Width,
        bounds.Height);

    private void PositionSelectionHandles()
    {
        var centerX = _selectionBounds.Left + (_selectionBounds.Width / 2);
        var centerY = _selectionBounds.Top + (_selectionBounds.Height / 2);
        SetSelectionHandle(CaptureSelectionHandle.TopLeft, _selectionBounds.Left, _selectionBounds.Top);
        SetSelectionHandle(CaptureSelectionHandle.Top, centerX, _selectionBounds.Top);
        SetSelectionHandle(CaptureSelectionHandle.TopRight, _selectionBounds.Right, _selectionBounds.Top);
        SetSelectionHandle(CaptureSelectionHandle.Right, _selectionBounds.Right, centerY);
        SetSelectionHandle(CaptureSelectionHandle.BottomRight, _selectionBounds.Right, _selectionBounds.Bottom);
        SetSelectionHandle(CaptureSelectionHandle.Bottom, centerX, _selectionBounds.Bottom);
        SetSelectionHandle(CaptureSelectionHandle.BottomLeft, _selectionBounds.Left, _selectionBounds.Bottom);
        SetSelectionHandle(CaptureSelectionHandle.Left, _selectionBounds.Left, centerY);
    }

    private void SetSelectionHandle(CaptureSelectionHandle handle, double centerX, double centerY)
    {
        var visual = _selectionHandles[handle];
        visual.Visibility = Visibility.Visible;
        Canvas.SetLeft(visual, centerX - (SelectionHandleSize / 2));
        Canvas.SetTop(visual, centerY - (SelectionHandleSize / 2));
    }

    private void HideSelectionHandles()
    {
        foreach (var visual in _selectionHandles.Values)
        {
            visual.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_adjustingSelection != CaptureSelectionHandle.None)
        {
            UpdateSelectionAdjustment(e.GetCurrentPoint(_canvas).Position, finalize: false);
            e.Handled = true;
            return;
        }
        if (_drawingAnnotation)
        {
            _annotations.PointerMoved(
                ToSelectionPoint(ClampToSelection(e.GetCurrentPoint(_canvas).Position)),
                IsShiftDown());
            e.Handled = true;
            return;
        }
        var point = e.GetCurrentPoint(_canvas);
        if (_start is null || !point.Properties.IsLeftButtonPressed)
        {
            if (_selectedScreen is null)
            {
                UpdateDetectedTarget(point.Position);
                _canvas.SetCursor(InputSystemCursorShape.Cross);
            }
            else
            {
                UpdateSelectionCursor(point.Position);
            }
            return;
        }
        var current = Clamp(point.Position);
        if (Math.Abs(current.X - _start.Value.X) >= 2 || Math.Abs(current.Y - _start.Value.Y) >= 2)
        {
            _pressedDetectedRegion = null;
        }
        if (_pressedDetectedRegion is null) UpdateSelection(_start.Value, current);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_adjustingSelection != CaptureSelectionHandle.None)
        {
            UpdateSelectionAdjustment(e.GetCurrentPoint(_canvas).Position, finalize: true);
            _canvas.ReleasePointerCapture(e.Pointer);
            _adjustingSelection = CaptureSelectionHandle.None;
            if (_toolbarVisibleBeforeAdjustment)
            {
                ShowToolbar(
                    _selectionBounds.Left,
                    _selectionBounds.Top,
                    _selectionBounds.Width,
                    _selectionBounds.Height);
            }
            _hintText.Text = "选区已调整 · 可继续拖动边框或控制点 · 可标注、保存、复制或贴图";
            UpdateSelectionCursor(e.GetCurrentPoint(_canvas).Position);
            e.Handled = true;
            return;
        }
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
            if (_pressedDetectedRegion is { } detected)
            {
                ApplySelectionRegion(detected, selectionFromDetectedTarget: true);
                _hintText.Text = "已按检测目标创建选区 · 拖动边框或控制点可调整";
            }
            else
            {
                CancelSelection();
            }
            _pressedDetectedRegion = null;
            e.Handled = true;
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
        ApplySelectionRegion(region, selectionFromDetectedTarget: false);
        _hintText.Text = "拖动边框或控制点可调整 · 可标注、保存、复制或贴图 · Esc 逐级取消";
        e.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_adjustingSelection != CaptureSelectionHandle.None)
        {
            CommitAdjustedSelection(_adjustmentStartBounds);
            _adjustingSelection = CaptureSelectionHandle.None;
            if (_toolbarVisibleBeforeAdjustment)
            {
                ShowToolbar(
                    _selectionBounds.Left,
                    _selectionBounds.Top,
                    _selectionBounds.Width,
                    _selectionBounds.Height);
            }
            _canvas.SetCursor(InputSystemCursorShape.Cross);
            e.Handled = true;
            return;
        }
        switch (CaptureInteractionPolicy.ResolvePointerCancellation(
                    _drawingAnnotation,
                    _start is not null))
        {
            case CapturePointerCancellationAction.CancelAnnotationGesture:
                _annotations.CancelPointerGesture();
                _drawingAnnotation = false;
                e.Handled = true;
                break;
            case CapturePointerCancellationAction.CancelRegionSelection:
                CancelSelection();
                e.Handled = true;
                break;
            case CapturePointerCancellationAction.PreserveSelection:
                break;
        }
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
        _annotationToolbar.Refresh();
    }

    private void ToggleToolbar()
    {
        if (_selectedScreen is null) return;
        if (_toolbar.Visibility == Visibility.Visible)
        {
            _toolbar.Visibility = Visibility.Collapsed;
            _hintText.Text = "标注工具条已隐藏 · 按空格重新显示";
            return;
        }

        ShowToolbar(
            _selectionBounds.Left,
            _selectionBounds.Top,
            _selectionBounds.Width,
            _selectionBounds.Height);
        _hintText.Text = "标注工具条已显示 · 按空格可隐藏";
    }

    private void ApplySelectionRegion(PixelRegion region, bool selectionFromDetectedTarget)
    {
        _detectedRegion = null;
        _pressedDetectedRegion = null;
        _selectedScreen = _screen.Crop(region.Left, region.Top, region.Width, region.Height);
        _selectedRegion = region;
        _selectionFromDetectedTarget = selectionFromDetectedTarget;
        _selectionBounds = PixelRegionToCanvasBounds(region);
        UpdateSelection(
            new Point(_selectionBounds.Left, _selectionBounds.Top),
            new Point(_selectionBounds.Right, _selectionBounds.Bottom));
        Canvas.SetLeft(_annotationHost, _selectionBounds.Left);
        Canvas.SetTop(_annotationHost, _selectionBounds.Top);
        _annotations.Begin(
            _screen,
            region,
            _selectionBounds.Width,
            _selectionBounds.Height);
        PositionSelectionHandles();
        ShowToolbar(
            _selectionBounds.Left,
            _selectionBounds.Top,
            _selectionBounds.Width,
            _selectionBounds.Height);
    }

    private Rect PixelRegionToCanvasBounds(PixelRegion region) => new(
        region.Left / (double)_screen.Width * _canvas.ActualWidth,
        region.Top / (double)_screen.Height * _canvas.ActualHeight,
        region.Width / (double)_screen.Width * _canvas.ActualWidth,
        region.Height / (double)_screen.Height * _canvas.ActualHeight);

    private void UpdateDetectedTargetFromCursor()
    {
        var cursor = ScreenCaptureService.GetCursorPosition();
        if (!DisplayBounds.Contains(cursor.X, cursor.Y) || _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
        {
            return;
        }

        UpdateDetectedTarget(new Point(
            (cursor.X - _screen.Left) / (double)_screen.Width * _canvas.ActualWidth,
            (cursor.Y - _screen.Top) / (double)_screen.Height * _canvas.ActualHeight));
    }

    private void UpdateDetectedTarget(Point canvasPoint)
    {
        if (_selectedScreen is not null || _start is not null ||
            _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
        {
            return;
        }

        var desktopPoint = new PointInt32(
            _screen.Left + Math.Clamp(
                (int)Math.Floor(canvasPoint.X / _canvas.ActualWidth * _screen.Width),
                0,
                _screen.Width - 1),
            _screen.Top + Math.Clamp(
                (int)Math.Floor(canvasPoint.Y / _canvas.ActualHeight * _screen.Height),
                0,
                _screen.Height - 1));
        var target = _targetDetector.FindTarget(_detectionMode, desktopPoint);
        _detectedRegion = target is { } bounds
            ? CaptureTargetPolicy.MapClippedToDisplay(DisplayBounds, bounds)
            : null;
        if (_detectedRegion is { } region)
        {
            var canvasBounds = PixelRegionToCanvasBounds(region);
            UpdateSelection(
                new Point(canvasBounds.Left, canvasBounds.Top),
                new Point(canvasBounds.Right, canvasBounds.Bottom));
        }
        else
        {
            ResetDetectionPreview();
        }
    }

    private string CreateReadyHint() => _detectionMode == CaptureDetectionMode.Window
        ? "窗口检测 · 单击选中，拖动自由选择 · Tab 切换界面元素 · R 恢复上次区域"
        : "界面元素检测 · 单击选中，拖动自由选择 · Tab 切换窗口 · R 恢复上次区域";

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
        ResetDetectionPreview();
        _annotations.Reset();
        _toolbar.Visibility = Visibility.Collapsed;
        _selectedScreen = null;
        _selectedRegion = null;
        _selectionFromDetectedTarget = false;
        _detectedRegion = null;
        _pressedDetectedRegion = null;
        _drawingAnnotation = false;
        _adjustingSelection = CaptureSelectionHandle.None;
        _canvas.SetCursor(InputSystemCursorShape.Cross);
        _hintText.Text = CreateReadyHint();
    }

    private void ResetDetectionPreview()
    {
        _fullShade.Visibility = Visibility.Visible;
        _topShade.Visibility = Visibility.Collapsed;
        _leftShade.Visibility = Visibility.Collapsed;
        _rightShade.Visibility = Visibility.Collapsed;
        _bottomShade.Visibility = Visibility.Collapsed;
        _selection.Visibility = Visibility.Collapsed;
        HideSelectionHandles();
        _detectedRegion = null;
    }

    private void CancelSelection()
    {
        _start = null;
        ResetShade();
        UpdateDetectedTargetFromCursor();
    }

    private async Task SaveSelectionAsync()
    {
        if (_selectedScreen is null || _completingSelection) return;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}"
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

    private Task StartLongCaptureAsync() => StartCaptureModeAsync(CaptureModeKind.LongCapture);

    private Task StartRecordingAsync() => StartCaptureModeAsync(CaptureModeKind.Recording);

    private async Task StartCaptureModeAsync(CaptureModeKind kind)
    {
        if (_selectedScreen is null || _completingSelection) return;
        if (_annotations.HasAnnotations)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                Title = kind == CaptureModeKind.LongCapture ? "进入长截图" : "进入区域录屏",
                Content = "进入新模式将丢弃当前标注，但会保留截图选区。",
                PrimaryButtonText = "继续",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        var bounds = new RectInt32(
            _selectedScreen.Left,
            _selectedScreen.Top,
            _selectedScreen.Width,
            _selectedScreen.Height);
        _completed = true;
        ModeRequested?.Invoke(new CaptureModeRequest(kind, bounds));
        Close();
    }

    private async Task CompleteSelectionAsync(
        CaptureCompletionAction action,
        StorageFile? saveDestination = null)
    {
        if (_selectedScreen is null || _selectedRegion is null || _completingSelection) return;
        _completingSelection = true;
        try
        {
            var output = action == CaptureCompletionAction.RecognizeText
                ? _selectedScreen
                : await _annotations.RenderAsync();
            Complete(new CaptureCompletion(output, action, saveDestination));
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

    private sealed class CursorCanvas : Canvas
    {
        private InputSystemCursorShape? _shape;

        internal void SetCursor(InputSystemCursorShape shape)
        {
            if (_shape == shape) return;
            ProtectedCursor = InputSystemCursor.Create(shape);
            _shape = shape;
        }
    }
}
