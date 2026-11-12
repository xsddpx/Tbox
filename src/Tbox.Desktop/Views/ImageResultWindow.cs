using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Tbox.Core;
using Tbox.Desktop.Models;

namespace Tbox.Desktop.Views;

// Image preview (spec ④): opened from a recent capture or a finished long capture. "复制" is the primary action.
// Recent captures can be stepped through in place with the arrows, the wheel or , / . without reopening the window.
internal sealed class ImageResultWindow : Window
{
    private readonly DesktopController _controller;
    private readonly TextBlock _status;
    private readonly TextBlock _subtitle;
    private readonly TextBlock _position;
    private readonly Button _older;
    private readonly Button _newer;
    private readonly Image _image;
    private readonly List<string> _paths = [];
    private Picture? _picture;
    private int _index = -1;
    private bool _loading;
    private int _loadRevision;

    public ImageResultWindow(DesktopController controller, Picture picture, bool fullScreen)
    {
        _controller = controller;
        Ui.Configure(this, "图片预览 · Tbox", 900, 680);
        MinWidth = 560; MinHeight = 420;
        Ui.ExtendIntoTitleBar(this);
        var brand = Ui.Brand();
        _subtitle = Ui.Description("");
        _position = new TextBlock { FontSize = DesignTokens.Caption, Foreground = Brush.Parse(DesignTokens.Hint), VerticalAlignment = VerticalAlignment.Center };
        var folder = Ui.IconButton(IconKind.Folder, OperatingSystem.IsMacOS() ? "在访达中显示" : "打开所在文件夹",
            () => controller.Run(() => { controller.OpenCache(); return Task.CompletedTask; }));

        _image = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        var frame = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = _image, Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 8 24 0 #2E101220") };
        var canvas = new Border
        {
            CornerRadius = new CornerRadius(10), Margin = new Thickness(12, 0), ClipToBounds = true, Background = Checkerboard(),
            Child = frame
        };

        _status = Ui.Description("");
        _status.FontSize = DesignTokens.Caption;
        void Copy() => controller.Run(CopyAsync);
        void Pin() => controller.Run(() => controller.PinPictureAsync(Current));
        void Ocr() => controller.ShowOcr(Current, false);
        void Save() => controller.Run(() => controller.SaveAsync(Current, this));

        _older = Ui.Button("上一张", () => _controller.Run(() => StepAsync(older: true)), "上一张 · ←");
        _newer = Ui.Button("下一张", () => _controller.Run(() => StepAsync(older: false)), "下一张 · →");
        SetNavigation(_older, "上一张", "上一张 · ←", pointsLeft: true);
        SetNavigation(_newer, "下一张", "下一张 · →", pointsLeft: false);
        var counter = new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 0), MinWidth = 48, Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center, Background = Brush.Parse("#F4F3F8"), Child = _position
        };
        var bar = Ui.ResultBar(_status,
            Ui.Button("识别文字", Ocr),
            Ui.Button("贴图", Pin),
            Ui.Button("保存…", Save, "保存"),
            Ui.Primary("复制", Copy));

        // Navigation sits beside the title so it reads as "which capture of how many", not as another action row.
        var navigator = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { _older, counter, _newer } };
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { navigator, folder } };
        var header = Ui.ResultHeader(IconKind.Image, ToolTone.Violet, "图片预览", _subtitle, trailing);

        var strip = Ui.TitleStrip("图片预览 · Tbox", brand);
        DockPanel.SetDock(strip, Dock.Top); DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom);
        Content = new DockPanel { Children = { strip, header, bar, canvas } };
        if (fullScreen) WindowState = WindowState.FullScreen;

        KeyDown += OnPreviewKeyDown;
        // Arrow keys are the primary way to step captures, so they are handled on the tunnel route:
        // otherwise Avalonia's focus navigation moves focus to a button first and never bubbles them here.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // The wheel steps captures like the arrow keys; trackpads and mice both land here.
        PointerWheelChanged += (_, e) =>
        {
            var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            if (delta == 0 || _loading) return;
            Step(older: delta < 0);
            e.Handled = true;
        };
        Opened += (_, _) => LoadRecent();
        Closed += (_, _) => { ReleasePicture(); brand.Dispose(); };

        Show(picture);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_loading && e.Key is Key.Left or Key.Right) { e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.C when (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0: _controller.Run(() => CopyAsync()); break;
            case Key.Left or Key.OemComma or Key.Subtract: Step(older: true); break;
            case Key.Right or Key.OemPeriod or Key.Add or Key.OemPlus: Step(older: false); break;
            case Key.Home: _controller.Run(() => ShowAsync(_paths.Count - 1)); break;
            case Key.End: _controller.Run(() => ShowAsync(0)); break;
            default: return;
        }
        e.Handled = true;
    }

    private async Task CopyAsync()
    {
        await _controller.CopyAsync(Current);
        _status.Text = "已复制，可直接粘贴。";
    }

    private Picture Current => _picture ?? throw new InvalidOperationException("图片预览尚未加载图片。");

    // Read-only state for tests and diagnostics: the browsable history and where the preview currently sits in it.
    internal int RecentCount => _paths.Count;
    internal int RecentIndex => _index;
    internal string PositionLabel => _position.Text ?? "";

    // Recent captures are browsed in the cache's own newest-first order, which is also what the main window lists.
    private void LoadRecent()
    {
        if (Current.CachePath is not { } currentPath) { SetNavigationEnabled(); return; }
        IReadOnlyList<FileInfo> files;
        try { files = new CaptureCache(_controller.CacheDirectory).GetRecentFiles(int.MaxValue); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { files = []; }
        _paths.Clear();
        _paths.AddRange(files.Select(file => file.FullName));
        _index = _paths.FindIndex(path => string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase));
        if (_index < 0) { _paths.Insert(0, currentPath); _index = 0; }
        SetNavigationEnabled();
    }

    private void SetNavigationEnabled()
    {
        var browsable = _paths.Count > 1;
        _older.IsEnabled = browsable && RecentCaptureNavigator.HasOlder(_index, _paths.Count);
        _newer.IsEnabled = browsable && RecentCaptureNavigator.HasNewer(_index, _paths.Count);
        _position.Text = RecentCaptureNavigator.Position(_index, _paths.Count);
        _position.IsVisible = browsable;
        ToolTip.SetTip(_older, browsable ? "上一张 · ←" : "没有更早的截图");
        ToolTip.SetTip(_newer, browsable ? "下一张 · →" : "没有更新的截图");
    }

    // Wraps at both ends, so holding the key keeps cycling instead of stopping on the oldest capture.
    private void Step(bool older)
    {
        if (_paths.Count < 2) return;
        _controller.Run(() => ShowAsync(RecentCaptureNavigator.Wrap(_index, _paths.Count, older)));
    }

    private async Task StepAsync(bool older)
    {
        if (_paths.Count < 2) return;
        await ShowAsync(RecentCaptureNavigator.Wrap(_index, _paths.Count, older));
    }

    // Loads one cached capture in place. The window keeps its size and position, and a failed load stays on the current image.
    internal async Task ShowAsync(int index)
    {
        if (index < 0 || index >= _paths.Count) return;
        var path = _paths[index];
        var revision = ++_loadRevision;
        _loading = true;
        _status.Text = "正在加载…";
        try
        {
            var picture = await _controller.LoadCachedPictureAsync(path);
            if (revision != _loadRevision || !IsVisible) { picture.Dispose(); return; }
            _index = index;
            Show(picture);
            _status.Text = "截图已保存在缓存文件夹";
        }
        catch (InvalidOperationException error) { if (revision == _loadRevision) _status.Text = error.Message; }
        catch (IOException) { if (revision == _loadRevision) _status.Text = "这张截图暂时无法读取。"; }
        finally { if (revision == _loadRevision) { _loading = false; SetNavigationEnabled(); } }
    }

    private void Show(Picture picture)
    {
        var previous = _picture;
        _picture = picture;
        _image.Source = picture.Bitmap;
        FitFrame(picture);
        previous?.Dispose();
        var subtitle = $"{picture.Bitmap.PixelSize.Width} × {picture.Bitmap.PixelSize.Height}";
        if (picture.CachePath is { } path && File.Exists(path))
        {
            var label = RecentCaptureLabel.Format(File.GetLastWriteTime(path), DateTime.Now);
            subtitle += " · " + (label.Contains(':') ? "今天 " + label : label);
        }
        _subtitle.Text = subtitle;
        if (picture.CachePath is null) { _paths.Clear(); _index = -1; SetNavigationEnabled(); }
    }

    // The frame tracks the image so a portrait capture is not letterboxed inside a wide box.
    private void FitFrame(Picture picture)
    {
        var size = picture.Bitmap.PixelSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        _image.Width = size.Width; _image.Height = size.Height;
    }

    private void ReleasePicture()
    {
        _picture?.Dispose();
        _picture = null;
    }

    // "‹ 上一张" / "下一张 ›": the shared chevron icon points right, so the left one is mirrored.
    private static void SetNavigation(Button button, string text, string label, bool pointsLeft)
    {
        var icon = new LineIcon(IconKind.Chevron, brush: Brush.Parse(DesignTokens.Hint)) { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        if (pointsLeft) icon.RenderTransform = new ScaleTransform(-1, 1);
        var textBlock = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { pointsLeft ? icon : textBlock, pointsLeft ? textBlock : icon } };
        button.Content = row;
        AutomationProperties.SetName(button, label);
        button.MinHeight = DesignTokens.ControlHeight;
    }

    // The transparent-image checkerboard behind the preview.
    private static IBrush Checkerboard()
    {
        var tile = new DrawingGroup { Children =
        {
            new GeometryDrawing { Brush = Brush.Parse("#F8F7FB"), Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)) },
            new GeometryDrawing { Brush = Brush.Parse("#EFEEF4"), Geometry = new RectangleGeometry(new Rect(0, 0, 8, 8)) },
            new GeometryDrawing { Brush = Brush.Parse("#EFEEF4"), Geometry = new RectangleGeometry(new Rect(8, 8, 8, 8)) }
        } };
        return new DrawingBrush(tile) { TileMode = TileMode.Tile, DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
            Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
    }
}
