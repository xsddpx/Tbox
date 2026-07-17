using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Tbox.Core;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Tbox.App;

internal sealed class PinnedImageWindow : Window
{
    private readonly Grid _root;
    private readonly CapturedScreen _screen;
    private readonly Canvas _annotationHost;
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
    private PointInt32 _dragStartCursor;
    private PointInt32 _dragStartWindow;
    private double _scale;
    private bool _dragging;
    private bool _drawingAnnotation;
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
        _root.Children.Add(new Image { Source = source, Stretch = Stretch.Fill });
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
        Content = _root;

        var menu = new MenuFlyout();
        var toolbarItem = new MenuFlyoutItem { Text = "显示工具条" };
        toolbarItem.Click += (_, _) => ToggleToolbar();
        menu.Items.Add(toolbarItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        var recognizeItem = new MenuFlyoutItem { Text = "文字识别" };
        recognizeItem.Click += (_, _) => _recognizeText(_screen, CurrentBounds());
        menu.Items.Add(recognizeItem);
        var copyItem = new MenuFlyoutItem { Text = "复制图像" };
        copyItem.Click += async (_, _) => await CopyImageAsync();
        menu.Items.Add(copyItem);
        var saveItem = new MenuFlyoutItem { Text = "图像另存为…" };
        saveItem.Click += async (_, _) => await SaveImageAsAsync();
        menu.Items.Add(saveItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        var closeItem = new MenuFlyoutItem { Text = "关闭" };
        closeItem.Click += (_, _) => Close();
        menu.Items.Add(closeItem);
        menu.Opening += (_, _) => toolbarItem.Text = _toolbarRequestedVisible
            ? "隐藏工具条"
            : "显示工具条";
        _root.ContextFlyout = menu;

        _root.PointerPressed += OnPointerPressed;
        _root.PointerMoved += OnPointerMoved;
        _root.PointerReleased += OnPointerReleased;
        _root.PointerCanceled += OnPointerCanceled;
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
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;
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

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
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
        if (_drawingAnnotation)
        {
            _annotations.CancelPointerGesture();
            _drawingAnnotation = false;
            e.Handled = true;
            return;
        }
        EndDrag(e);
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

        var targetWidth = Math.Max(1, (int)Math.Round(_sourceWidth * nextScale));
        var targetHeight = Math.Max(1, (int)Math.Round(_sourceHeight * nextScale));
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
            new PixelRegion(0, 0, _screen.Width, _screen.Height),
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
        _recognizeText(_screen, CurrentBounds());
        return Task.CompletedTask;
    }

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
