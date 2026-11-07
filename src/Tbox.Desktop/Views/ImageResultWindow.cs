using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Tbox.Desktop.Models;

namespace Tbox.Desktop.Views;

internal sealed class ImageResultWindow : Window
{
    public ImageResultWindow(DesktopController controller, Picture picture, bool fullScreen)
    {
        Ui.Configure(this, "Tbox 图片预览", 900, 700);
        var actions = Ui.Row(Ui.Button("识别文字", () => controller.ShowOcr(picture, false)),
            Ui.Button("贴图", () => controller.Run(() => controller.PinPictureAsync(picture))),
            Ui.Button("保存", () => controller.Run(() => controller.SaveAsync(picture, this))),
            Ui.Button("复制", () => controller.Run(() => controller.CopyAsync(picture))), Ui.Button("关闭", Close));
        actions.Margin = new Thickness(16);
        DockPanel.SetDock(actions, Dock.Top);
        var image = new Image { Source = picture.Bitmap, Width = Math.Min(1200, picture.Bitmap.PixelSize.Width),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        Content = new DockPanel { Children = { actions, new ScrollViewer { Content = image,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto } } };
        if (fullScreen) WindowState = WindowState.FullScreen;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => picture.Dispose();
    }
}
