using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace Tbox.App;

internal sealed class LongCaptureReviewWindow : Window
{
    private readonly CapturedScreen _screen;
    private readonly string _cacheFilePath;
    private readonly Func<Task> _copy;
    private readonly Func<Task> _pin;
    private readonly Action _recognizeText;
    private readonly Action<LongCaptureReviewWindow> _dismissed;
    private readonly AppWindow _appWindow;
    private bool _closed;

    private LongCaptureReviewWindow(
        CapturedScreen screen,
        string cacheFilePath,
        Func<Task> copy,
        Func<Task> pin,
        Action recognizeText,
        Action<LongCaptureReviewWindow> dismissed,
        Image image)
    {
        _screen = screen;
        _cacheFilePath = cacheFilePath;
        _copy = copy;
        _pin = pin;
        _recognizeText = recognizeText;
        _dismissed = dismissed;
        Title = "Tbox 长截图预览";

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8,
            Padding = new Thickness(12, 9, 12, 9)
        };
        actions.Children.Add(CreateButton("文字识别", () => { _recognizeText(); return Task.CompletedTask; }));
        actions.Children.Add(CreateButton("保存", SaveAsync));
        actions.Children.Add(CreateButton("复制", _copy));
        actions.Children.Add(CreateButton("贴图", _pin));
        actions.Children.Add(CreateButton("关闭", () => { Close(); return Task.CompletedTask; }));

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 24, 24, 24)),
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };
        var viewer = new ScrollViewer
        {
            Content = image,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = 0.1f,
            MaxZoomFactor = 4f
        };
        image.Stretch = Stretch.Uniform;
        image.HorizontalAlignment = HorizontalAlignment.Center;
        image.VerticalAlignment = VerticalAlignment.Top;
        root.Children.Add(viewer);
        Grid.SetRow(actions, 1);
        root.Children.Add(actions);
        Content = root;

        var hwnd = WindowNative.GetWindowHandle(this);
        NativeMethods.DisableWindowTransitions(hwnd);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }
        var center = new PointInt32(screen.Left + screen.Width / 2, screen.Top + Math.Min(screen.Height, 600) / 2);
        var work = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(820, Math.Max(480, work.Width - 80));
        var height = Math.Min(860, Math.Max(420, work.Height - 80));
        NativeMethods.MoveAndResizeWindow(hwnd, new RectInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2,
            width,
            height));

        Closed += (_, _) =>
        {
            if (_closed) return;
            _closed = true;
            _dismissed(this);
        };
    }

    internal string CacheFilePath => _cacheFilePath;

    internal Task SaveToFileAsync() => SaveAsync();

    internal void Maximize()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
    }

    internal static async Task<LongCaptureReviewWindow> CreateAsync(
        CapturedScreen screen,
        string cacheFilePath,
        Func<Task> copy,
        Func<Task> pin,
        Action recognizeText,
        Action<LongCaptureReviewWindow> dismissed)
    {
        var source = await screen.CreateBitmapSourceAsync(maximumDecodeDimension: 8192);
        return new LongCaptureReviewWindow(
            screen,
            cacheFilePath,
            copy,
            pin,
            recognizeText,
            dismissed,
            new Image { Source = source });
    }

    private async Task SaveAsync()
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(_cacheFilePath)
            };
            picker.FileTypeChoices.Add("PNG 图像", [".png"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var destination = await picker.PickSaveFileAsync();
            if (destination is null) return;
            await using var source = new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var output = await destination.OpenStreamForWriteAsync();
            output.SetLength(0);
            await source.CopyToAsync(output);
            await output.FlushAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write($"Save long screenshot failed: {exception}");
        }
    }

    private static Button CreateButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text, MinWidth = 72 };
        button.Click += async (_, _) => await action();
        return button;
    }
}
