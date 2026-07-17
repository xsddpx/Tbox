using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Tbox.Core;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace Tbox.App;

internal sealed class PinnedImageWindow : Window
{
    private const double CropDragThreshold = 4;
    private const double MinimumCropDisplaySize = 8;
    private readonly Grid _root;
    private readonly CapturedScreen _screen;
    private readonly Image _image;
    private readonly Canvas _annotationHost;
    private readonly Canvas _cropOverlay;
    private readonly Rectangle _cropSelection;
    private readonly CaptureAnnotationController _annotations;
    private readonly AnnotationToolbar _toolbar;
    private readonly PinnedAnnotationToolbarWindow _toolbarWindow;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private readonly Action<string> _notify;
    private readonly Action<CapturedScreen, RectInt32> _recognizeText;
    private readonly RectInt32 _initialBounds;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly double _minimumScale;
    private readonly double _maximumScale;
    private readonly MenuFlyout _contextMenu;
    private readonly MenuFlyoutItem _restoreCropItem;
    private PointInt32 _dragStartCursor;
    private PointInt32 _dragStartWindow;
    private PixelRegion _visibleRegion;
    private Point? _cropStart;
    private double _scale;
    private bool _dragging;
    private bool _drawingAnnotation;
    private bool _cropDragging;
    private bool _applyingVisibleRegion;
    private bool _annotationsInitialized;
    private bool _toolbarRequestedVisible;
    private bool _hasBeenShown;

    internal string CacheFilePath { get; }
    internal bool IsVisible { get; private set; }
    internal event Action<PinnedImageWindow>? Dismissed;

    private PinnedImageWindow(
        CapturedScreen screen,
        BitmapImage source,
        string cacheFilePath,
        Action<string> notify,
        Action<CapturedScreen, RectInt32> recognizeText,
        RectInt32? initialBounds)
    {
        _screen = screen;
        CacheFilePath = cacheFilePath;
        _notify = notify;
        _recognizeText = recognizeText;
        Title = "Tbox 贴图";
        _root = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
            IsTabStop = true,
            KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden
        };
        _image = new Image { Source = source, Stretch = Stretch.Fill };
        _root.Children.Add(_image);
        _annotationHost = new Canvas { Visibility = Visibility.Collapsed };
        _annotations = new CaptureAnnotationController(_annotationHost);
        _annotations.FinishRequested += FinishAnnotationEditing;
        _root.Children.Add(_annotationHost);
        _toolbar = new AnnotationToolbar(
            _annotations,
            new AnnotationToolbarActions(
                RecognizeTextAsync,
                SaveImageAsAsync,
                CopyImageAsync,
                "复制图像（Ctrl+C）",
                FinishAnnotationEditingAsync,
                "\uE73E",
                "完成绘制（Esc）"),
            Visibility.Visible);
        _cropSelection = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(56, 0, 120, 212)),
            Stroke = new SolidColorBrush(CaptureFramePalette.Screenshot),
            StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
        _cropOverlay = new Canvas
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _cropOverlay.Children.Add(_cropSelection);
        _root.Children.Add(_cropOverlay);
        Content = _root;

        _contextMenu = new MenuFlyout();
        var toolbarItem = new MenuFlyoutItem { Text = "显示工具条" };
        toolbarItem.Click += (_, _) => ToggleToolbar();
        _contextMenu.Items.Add(toolbarItem);
        _restoreCropItem = new MenuFlyoutItem { Text = "恢复完整贴图" };
        _restoreCropItem.Click += async (_, _) => await RestoreFullImageAsync();
        _contextMenu.Items.Add(_restoreCropItem);
        _contextMenu.Items.Add(new MenuFlyoutSeparator());
        var recognizeItem = new MenuFlyoutItem { Text = "文字识别" };
        recognizeItem.Click += (_, _) => RecognizeVisibleText();
        _contextMenu.Items.Add(recognizeItem);
        var copyItem = new MenuFlyoutItem { Text = "复制图像" };
        copyItem.Click += async (_, _) => await CopyImageAsync();
        _contextMenu.Items.Add(copyItem);
        var saveItem = new MenuFlyoutItem { Text = "图像另存为…" };
        saveItem.Click += async (_, _) => await SaveImageAsAsync();
        _contextMenu.Items.Add(saveItem);
        _contextMenu.Items.Add(new MenuFlyoutSeparator());
        var closeItem = new MenuFlyoutItem { Text = "关闭" };
        closeItem.Click += (_, _) => Close();
        _contextMenu.Items.Add(closeItem);
        _contextMenu.Opening += (_, _) =>
        {
            toolbarItem.Text = _toolbarRequestedVisible ? "隐藏工具条" : "显示工具条";
            _restoreCropItem.IsEnabled = !PinCropPolicy.IsFullImage(
                _visibleRegion,
                _sourceWidth,
                _sourceHeight) && !_applyingVisibleRegion;
        };

        _root.PointerPressed += OnPointerPressed;
        _root.PointerMoved += OnPointerMoved;
        _root.PointerReleased += OnPointerReleased;
        _root.PointerCanceled += OnPointerCanceled;
        _root.PointerCaptureLost += OnPointerCaptureLost;
        _root.PointerWheelChanged += OnPointerWheelChanged;

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            if (_toolbarRequestedVisible)
            {
                FinishAnnotationEditing();
                return;
            }
            Close();
        };
        _root.KeyboardAccelerators.Add(escape);

        AddKeyboardAccelerator(VirtualKey.Space, VirtualKeyModifiers.None, ToggleToolbar);
        AddKeyboardAccelerator(VirtualKey.B, VirtualKeyModifiers.None,
            () => _annotations.SetTool(AnnotationTool.Pen));
        AddKeyboardAccelerator(VirtualKey.T, VirtualKeyModifiers.None,
            () => _annotations.SetTool(AnnotationTool.Text));
        AddKeyboardAccelerator(VirtualKey.Number1, VirtualKeyModifiers.None,
            () => _annotations.AdjustWidth(-1));
        AddKeyboardAccelerator(VirtualKey.Number2, VirtualKeyModifiers.None,
            () => _annotations.AdjustWidth(1));
        AddKeyboardAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control, _annotations.Undo);
        AddKeyboardAccelerator(VirtualKey.Y, VirtualKeyModifiers.Control, _annotations.Redo);
        AddKeyboardAccelerator(VirtualKey.C, VirtualKeyModifiers.Control,
            () => _ = CopyImageAsync());
        AddKeyboardAccelerator(VirtualKey.S, VirtualKeyModifiers.Control,
            () => _ = SaveImageAsAsync());

        _windowHandle = WindowNative.GetWindowHandle(this);
        NativeMethods.DisableWindowTransitions(_windowHandle);
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
        _toolbarWindow = new PinnedAnnotationToolbarWindow(
            _toolbar,
            _windowHandle,
            FinishAnnotationEditing);

        _sourceWidth = screen.Width;
        _sourceHeight = screen.Height;
        _visibleRegion = new PixelRegion(0, 0, _sourceWidth, _sourceHeight);
        var bounds = initialBounds is { } requested
            ? new RectInt32(
                requested.X,
                requested.Y,
                Math.Max(1, requested.Width),
                Math.Max(1, requested.Height))
            : CreateCursorPlacement(_sourceWidth, _sourceHeight);
        _initialBounds = bounds;
        NativeMethods.MoveAndResizeWindow(_windowHandle, bounds);

        _scale = Math.Min(bounds.Width / (double)_sourceWidth, bounds.Height / (double)_sourceHeight);
        var scaleForMinimumDimension = 32d / Math.Min(_sourceWidth, _sourceHeight);
        _minimumScale = Math.Min(_scale, Math.Max(0.1, scaleForMinimumDimension));
        var scaleForMaximumDimension = 8192d / Math.Max(_sourceWidth, _sourceHeight);
        _maximumScale = Math.Max(_scale, Math.Min(8, scaleForMaximumDimension));

        _root.Loaded += (_, _) => InitializeAnnotationSurface();
        _root.SizeChanged += (_, _) =>
        {
            InitializeAnnotationSurface();
            _annotations.ResizeSurface(_root.ActualWidth, _root.ActualHeight);
        };
        Activated += (_, _) => _root.Focus(FocusState.Programmatic);
        Closed += (_, _) =>
        {
            IsVisible = false;
            _toolbarWindow.Close();
            _toolbar.Dispose();
            _annotations.FinishRequested -= FinishAnnotationEditing;
            _annotations.Dispose();
            Dismissed?.Invoke(this);
        };
    }

    internal static async Task<PinnedImageWindow> CreateAsync(
        string cacheFilePath,
        Action<string> notify,
        Action<CapturedScreen, RectInt32> recognizeText,
        RectInt32? initialBounds = null)
    {
        var screen = await CapturedScreen.LoadAsync(cacheFilePath);
        var source = await screen.CreateBitmapSourceAsync(maximumDecodeDimension: 8192);
        return new PinnedImageWindow(screen, source, cacheFilePath, notify, recognizeText, initialBounds);
    }

    internal void Show(bool activate)
    {
        if (!_hasBeenShown)
        {
            _appWindow.Show(activateWindow: false);
            NativeMethods.MoveAndResizeWindow(_windowHandle, _initialBounds);
            _hasBeenShown = true;
            if (activate) Activate();
        }
        else
        {
            _appWindow.Show(activate);
        }
        IsVisible = true;
        if (_toolbarRequestedVisible) _toolbarWindow.ShowAdjacent(CurrentBounds());
    }

    internal void Hide()
    {
        _toolbarWindow.Hide();
        _appWindow.Hide();
        IsVisible = false;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (_annotations.IsTextEditorElement(source)) return;
        var point = e.GetCurrentPoint(_root);
        if (point.Properties.IsRightButtonPressed && !_applyingVisibleRegion)
        {
            _cropStart = ClampToSurface(point.Position);
            _cropDragging = false;
            _root.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        if (_annotations.CurrentTool is not null)
        {
            _drawingAnnotation = _annotations.PointerPressed(
                e.GetCurrentPoint(_annotationHost).Position,
                IsShiftDown());
            if (_drawingAnnotation) _root.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
        _dragStartCursor = ScreenCaptureService.GetCursorPosition();
        _dragStartWindow = _appWindow.Position;
        _dragging = true;
        _root.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_cropStart is { } cropStart)
        {
            var point = e.GetCurrentPoint(_root);
            var current = ClampToSurface(point.Position);
            if (!_cropDragging &&
                (Math.Abs(current.X - cropStart.X) >= CropDragThreshold ||
                 Math.Abs(current.Y - cropStart.Y) >= CropDragThreshold))
            {
                InitializeAnnotationSurface();
                _annotations.PrepareForRegionAdjustment();
                _cropDragging = true;
            }
            if (_cropDragging) UpdateCropSelection(cropStart, current);
            e.Handled = true;
            return;
        }
        if (_drawingAnnotation)
        {
            _annotations.PointerMoved(e.GetCurrentPoint(_annotationHost).Position, IsShiftDown());
            e.Handled = true;
            return;
        }
        if (!_dragging || !e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;
        var cursor = ScreenCaptureService.GetCursorPosition();
        var position = new PointInt32(
            _dragStartWindow.X + cursor.X - _dragStartCursor.X,
            _dragStartWindow.Y + cursor.Y - _dragStartCursor.Y);
        NativeMethods.MoveWindow(_windowHandle, position);
        _toolbarWindow.Reposition(new RectInt32(
            position.X,
            position.Y,
            _appWindow.Size.Width,
            _appWindow.Size.Height));
        e.Handled = true;
    }

    private async void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_cropStart is { } cropStart)
        {
            var current = ClampToSurface(e.GetCurrentPoint(_root).Position);
            var shouldCrop = _cropDragging;
            ResetCropGesture();
            _root.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            if (shouldCrop)
            {
                await ApplyCropAsync(cropStart, current);
            }
            else
            {
                _contextMenu.ShowAt(_root, current);
            }
            return;
        }
        if (_drawingAnnotation)
        {
            _annotations.PointerReleased(e.GetCurrentPoint(_annotationHost).Position, IsShiftDown());
            _drawingAnnotation = false;
            _root.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }
        EndDrag(e);
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_cropStart is not null)
        {
            ResetCropGesture();
            e.Handled = true;
            return;
        }
        if (_drawingAnnotation)
        {
            _annotations.CancelPointerGesture();
            _drawingAnnotation = false;
            e.Handled = true;
            return;
        }
        EndDrag(e);
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_cropStart is not null) ResetCropGesture();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(_root).Properties.MouseWheelDelta;
        if (_annotations.CurrentTool is not null)
        {
            _annotations.AdjustWidth(delta > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }
        var nextScale = PinWindowScalePolicy.ApplyWheelDelta(
            _scale,
            delta,
            _minimumScale,
            _maximumScale);
        if (Math.Abs(nextScale - _scale) < 0.000001) return;

        var targetWidth = Math.Max(1, (int)Math.Round(_visibleRegion.Width * nextScale));
        var targetHeight = Math.Max(1, (int)Math.Round(_visibleRegion.Height * nextScale));
        var currentPosition = _appWindow.Position;
        var currentSize = _appWindow.Size;
        var cursor = ScreenCaptureService.GetCursorPosition();
        var resized = PinWindowScalePolicy.ResizeAroundPoint(
            new PinWindowBounds(
                currentPosition.X,
                currentPosition.Y,
                currentSize.Width,
                currentSize.Height),
            targetWidth,
            targetHeight,
            cursor.X,
            cursor.Y);
        var resizedBounds = new RectInt32(
            resized.X,
            resized.Y,
            resized.Width,
            resized.Height);
        NativeMethods.MoveAndResizeWindow(_windowHandle, resizedBounds);
        _toolbarWindow.Reposition(resizedBounds);
        _scale = nextScale;
        e.Handled = true;
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _root.ReleasePointerCapture(e.Pointer);
    }

    private void InitializeAnnotationSurface()
    {
        if (_annotationsInitialized || _root.ActualWidth <= 0 || _root.ActualHeight <= 0) return;
        _annotations.Begin(
            _screen,
            _visibleRegion,
            _root.ActualWidth,
            _root.ActualHeight);
        _annotationsInitialized = true;
    }

    private void ToggleToolbar()
    {
        InitializeAnnotationSurface();
        if (_toolbarRequestedVisible)
        {
            HideToolbar();
            return;
        }

        _toolbarRequestedVisible = true;
        if (IsVisible) _toolbarWindow.ShowAdjacent(CurrentBounds());
    }

    private void HideToolbar()
    {
        _toolbarRequestedVisible = false;
        _toolbarWindow.Hide();
        if (IsVisible) Activate();
    }

    private void FinishAnnotationEditing()
    {
        _root.ReleasePointerCaptures();
        ResetCropGesture();
        _drawingAnnotation = false;
        _dragging = false;
        _annotations.CompleteEditing();
        HideToolbar();
    }

    private Task FinishAnnotationEditingAsync()
    {
        FinishAnnotationEditing();
        return Task.CompletedTask;
    }

    private Task RecognizeTextAsync()
    {
        RecognizeVisibleText();
        return Task.CompletedTask;
    }

    private void RecognizeVisibleText()
    {
        _recognizeText(
            _screen.Crop(
                _visibleRegion.Left,
                _visibleRegion.Top,
                _visibleRegion.Width,
                _visibleRegion.Height),
            CurrentBounds());
    }

    private async Task ApplyCropAsync(Point start, Point end)
    {
        if (!PinCropPolicy.TryCreate(
                _visibleRegion,
                CurrentWindowBounds(),
                _root.ActualWidth,
                _root.ActualHeight,
                start.X,
                start.Y,
                end.X,
                end.Y,
                MinimumCropDisplaySize,
                out var crop))
        {
            return;
        }
        await ApplyVisibleRegionAsync(crop, "无法裁剪贴图，请重试。");
    }

    private async Task RestoreFullImageAsync()
    {
        if (PinCropPolicy.IsFullImage(_visibleRegion, _sourceWidth, _sourceHeight)) return;
        var restored = PinCropPolicy.Restore(
            _visibleRegion,
            CurrentWindowBounds(),
            _sourceWidth,
            _sourceHeight);
        await ApplyVisibleRegionAsync(restored, "无法恢复完整贴图，请重试。");
    }

    private async Task ApplyVisibleRegionAsync(PinCropResult target, string failureMessage)
    {
        if (_applyingVisibleRegion) return;
        _applyingVisibleRegion = true;
        try
        {
            var visibleScreen = _screen.Crop(
                target.Region.Left,
                target.Region.Top,
                target.Region.Width,
                target.Region.Height);
            var source = await visibleScreen.CreateBitmapSourceAsync(maximumDecodeDimension: 8192);

            _annotations.PrepareForRegionAdjustment();
            _visibleRegion = target.Region;
            _image.Source = source;
            if (_annotationsInitialized)
            {
                _annotations.UpdateRegion(
                    _screen,
                    _visibleRegion,
                    Math.Max(1, _root.ActualWidth),
                    Math.Max(1, _root.ActualHeight));
            }

            var bounds = new RectInt32(
                target.WindowBounds.X,
                target.WindowBounds.Y,
                target.WindowBounds.Width,
                target.WindowBounds.Height);
            NativeMethods.MoveAndResizeWindow(_windowHandle, bounds);
            _toolbarWindow.Reposition(bounds);
            _scale = Math.Min(
                target.WindowBounds.Width / (double)target.Region.Width,
                target.WindowBounds.Height / (double)target.Region.Height);
        }
        catch (Exception exception)
        {
            AppLog.Write($"Change pinned image crop failed: {exception}");
            _notify(failureMessage);
        }
        finally
        {
            _applyingVisibleRegion = false;
        }
    }

    private void UpdateCropSelection(Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        _cropSelection.Width = Math.Abs(end.X - start.X);
        _cropSelection.Height = Math.Abs(end.Y - start.Y);
        Canvas.SetLeft(_cropSelection, left);
        Canvas.SetTop(_cropSelection, top);
        _cropOverlay.Visibility = Visibility.Visible;
    }

    private void ResetCropGesture()
    {
        _cropStart = null;
        _cropDragging = false;
        _cropOverlay.Visibility = Visibility.Collapsed;
    }

    private Point ClampToSurface(Point point) => new(
        Math.Clamp(point.X, 0, Math.Max(0, _root.ActualWidth)),
        Math.Clamp(point.Y, 0, Math.Max(0, _root.ActualHeight)));

    private void AddKeyboardAccelerator(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            if (_annotations.IsEditingText) return;
            args.Handled = true;
            action();
        };
        _root.KeyboardAccelerators.Add(accelerator);
    }

    private static bool IsShiftDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VkShift) & 0x8000) != 0;

    private RectInt32 CurrentBounds() => new(
        _appWindow.Position.X,
        _appWindow.Position.Y,
        _appWindow.Size.Width,
        _appWindow.Size.Height);

    private PinWindowBounds CurrentWindowBounds() => new(
        _appWindow.Position.X,
        _appWindow.Position.Y,
        _appWindow.Size.Width,
        _appWindow.Size.Height);

    private static RectInt32 CreateCursorPlacement(int pixelWidth, int pixelHeight)
    {
        var cursor = ScreenCaptureService.GetCursorPosition();
        var display = DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var scale = Math.Min(1d, Math.Min(work.Width * 0.65 / pixelWidth, work.Height * 0.65 / pixelHeight));
        var width = Math.Max(48, (int)Math.Round(pixelWidth * scale));
        var height = Math.Max(48, (int)Math.Round(pixelHeight * scale));
        var x = Math.Clamp(cursor.X + 18, work.X, work.X + work.Width - width);
        var y = Math.Clamp(cursor.Y + 18, work.Y, work.Y + work.Height - height);
        return new RectInt32(x, y, width, height);
    }

    private async Task CopyImageAsync()
    {
        try
        {
            InitializeAnnotationSurface();
            var rendered = await _annotations.RenderAsync();
            await ClipboardImageService.CopyPngAsync(await rendered.CreatePngBytesAsync());
        }
        catch (Exception exception)
        {
            AppLog.Write($"Copy pinned image failed: {exception}");
            _notify("无法将贴图复制到剪贴板。");
        }
    }

    private async Task SaveImageAsAsync()
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(CacheFilePath)
            };
            picker.FileTypeChoices.Add("PNG 图像", [".png"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var destination = await picker.PickSaveFileAsync();
            if (destination is null) return;
            InitializeAnnotationSurface();
            var rendered = await _annotations.RenderAsync();
            await FileIO.WriteBytesAsync(destination, await rendered.CreatePngBytesAsync());
        }
        catch (Exception exception)
        {
            AppLog.Write($"Save pinned image failed: {exception}");
            _notify("无法保存贴图图像。");
        }
    }
}
