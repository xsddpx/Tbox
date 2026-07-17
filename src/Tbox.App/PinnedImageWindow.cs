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
    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private readonly Action<string> _notify;
    private readonly RectInt32 _initialBounds;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly double _minimumScale;
    private readonly double _maximumScale;
    private PointInt32 _dragStartCursor;
    private PointInt32 _dragStartWindow;
    private double _scale;
    private bool _dragging;
    private bool _hasBeenShown;

    internal string CacheFilePath { get; }
    internal bool IsVisible { get; private set; }
    internal event Action<PinnedImageWindow>? Dismissed;

    private PinnedImageWindow(
        BitmapImage source,
        string cacheFilePath,
        Action<string> notify,
        RectInt32? initialBounds)
    {
        CacheFilePath = cacheFilePath;
        _notify = notify;
        Title = "Tbox 贴图";
        _root = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
            IsTabStop = true,
            KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden
        };
        _root.Children.Add(new Image { Source = source, Stretch = Stretch.Fill });
        Content = _root;

        var menu = new MenuFlyout();
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
            Close();
        };
        _root.KeyboardAccelerators.Add(escape);

        var copy = new KeyboardAccelerator
        {
            Key = VirtualKey.C,
            Modifiers = VirtualKeyModifiers.Control
        };
        copy.Invoked += async (_, args) =>
        {
            args.Handled = true;
            await CopyImageAsync();
        };
        _root.KeyboardAccelerators.Add(copy);

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

        _sourceWidth = Math.Max(1, source.PixelWidth);
        _sourceHeight = Math.Max(1, source.PixelHeight);
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

        Activated += (_, _) => _root.Focus(FocusState.Programmatic);
        Closed += (_, _) =>
        {
            IsVisible = false;
            Dismissed?.Invoke(this);
        };
    }

    internal static async Task<PinnedImageWindow> CreateAsync(
        string cacheFilePath,
        Action<string> notify,
        RectInt32? initialBounds = null)
    {
        var file = await StorageFile.GetFileFromPathAsync(cacheFilePath);
        using var stream = await file.OpenReadAsync();
        var source = new BitmapImage();
        await source.SetSourceAsync(stream);
        return new PinnedImageWindow(source, cacheFilePath, notify, initialBounds);
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
    }

    internal void Hide()
    {
        _appWindow.Hide();
        IsVisible = false;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;
        _dragStartCursor = ScreenCaptureService.GetCursorPosition();
        _dragStartWindow = _appWindow.Position;
        _dragging = true;
        _root.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;
        var cursor = ScreenCaptureService.GetCursorPosition();
        NativeMethods.MoveWindow(_windowHandle, new PointInt32(
            _dragStartWindow.X + cursor.X - _dragStartCursor.X,
            _dragStartWindow.Y + cursor.Y - _dragStartCursor.Y));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => EndDrag(e);

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e) => EndDrag(e);

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(_root).Properties.MouseWheelDelta;
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
        NativeMethods.MoveAndResizeWindow(_windowHandle, new RectInt32(
            resized.X,
            resized.Y,
            resized.Width,
            resized.Height));
        _scale = nextScale;
        e.Handled = true;
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _root.ReleasePointerCapture(e.Pointer);
    }

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
            await ClipboardImageService.CopyFileAsync(CacheFilePath);
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
            File.Copy(CacheFilePath, destination.Path, overwrite: true);
        }
        catch (Exception exception)
        {
            AppLog.Write($"Save pinned image failed: {exception}");
            _notify("无法保存贴图图像。");
        }
    }
}
