using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tbox.Core;
using Tbox.Desktop.Models;

namespace Tbox.Desktop.Views;

// Image preview (spec ④): opened from a recent capture or a finished long capture. "复制" is the primary action.
internal sealed class ImageResultWindow : Window
{
    private readonly TextBlock _status;

    public ImageResultWindow(DesktopController controller, Picture picture, bool fullScreen)
    {
        Ui.Configure(this, "图片预览 · Tbox", 900, 680);
        MinWidth = 560; MinHeight = 420;
        Ui.ExtendIntoTitleBar(this);
        var brand = Ui.Brand();
        var subtitle = $"{picture.Bitmap.PixelSize.Width} × {picture.Bitmap.PixelSize.Height}";
        if (picture.CachePath is { } path && File.Exists(path))
        {
            var label = RecentCaptureLabel.Format(File.GetLastWriteTime(path), DateTime.Now);
            subtitle += " · " + (label.Contains(':') ? "今天 " + label : label);
        }
        var folder = Ui.IconButton(IconKind.Folder, OperatingSystem.IsMacOS() ? "在访达中显示" : "打开所在文件夹",
            () => controller.Run(() => { controller.OpenCache(); return Task.CompletedTask; }));
        folder.IsVisible = picture.CachePath is not null;
        var header = Ui.ResultHeader(IconKind.Image, ToolTone.Violet, "图片预览", Ui.Description(subtitle), folder);

        var image = new Image { Source = picture.Bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        var frame = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = image, Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 8 24 0 #2E101220") };
        var canvas = new Border
        {
            CornerRadius = new CornerRadius(10), Margin = new Thickness(12, 0), ClipToBounds = true, Background = Checkerboard(),
            Child = frame
        };

        _status = Ui.Description(picture.CachePath is null ? "" : "截图已保存在缓存文件夹");
        _status.FontSize = DesignTokens.Caption;
        void Copy() => controller.Run(async () => { await controller.CopyAsync(picture); _status.Text = "已复制，可直接粘贴。"; });
        var bar = Ui.ResultBar(_status,
            Ui.Button("识别文字", () => controller.ShowOcr(picture, false)),
            Ui.Button("贴图", () => controller.Run(() => controller.PinPictureAsync(picture))),
            Ui.Button("保存…", () => controller.Run(() => controller.SaveAsync(picture, this)), "保存"),
            Ui.Primary("复制", Copy));

        var strip = Ui.TitleStrip("图片预览 · Tbox", brand);
        DockPanel.SetDock(strip, Dock.Top); DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom);
        Content = new DockPanel { Children = { strip, header, bar, canvas } };
        if (fullScreen) WindowState = WindowState.FullScreen;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.C && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) Copy();
            else return;
            e.Handled = true;
        };
        Closed += (_, _) => { picture.Dispose(); brand.Dispose(); };
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
