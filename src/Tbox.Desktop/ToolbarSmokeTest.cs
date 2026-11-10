using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Tbox.Core;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;

namespace Tbox.Desktop;

// Installed-package verification: exercise real popup hosts without retaining the user's desktop pixels.
internal static class ToolbarSmokeTest
{
    public static async Task<object> RunAsync(DesktopController controller, IReadOnlyList<CapturedDisplay> displays, string directory)
    {
        var session = new CaptureSession(controller, displays, _ => { });
        var placements = new List<object>();
        try
        {
            session.Show(); await Task.Delay(150);
            var window = session.Windows.FirstOrDefault(w => w.IsActive) ?? session.Windows.First();
            foreach (var above in new[] { false, true })
            {
                var bottom = above ? window.ClientSize.Height - 100 : Math.Min(300, window.ClientSize.Height - 180);
                session.Select(window.Display.Id, new CaptureSelectionBounds(30, 60, Math.Min(480, window.ClientSize.Width - 60), bottom - 60));
                window.UpdateLayout();
                window.Editor.Select(AnnotationTool.Text); await Task.Delay(100); window.UpdateLayout();
                var toolbar = window.GetVisualDescendants().OfType<AnnotationToolbar>().Single();
                if (!toolbar.PropertiesPopup.IsOpen) throw new InvalidOperationException("截图属性条未打开。");
                var row = toolbar.PropertiesSurface;
                var barPoint = toolbar.TranslatePoint(default, window)!.Value;
                var propertyPoint = row.TranslatePoint(default, window)!.Value;
                var gap = above ? barPoint.Y - propertyPoint.Y - row.Bounds.Height : propertyPoint.Y - barPoint.Y - toolbar.Bounds.Height;
                if (Math.Abs(gap - 6) > 1 || propertyPoint.X < 0 || propertyPoint.X + row.Bounds.Width > window.ClientSize.Width + 1 || row.Bounds.Height > 48)
                    throw new InvalidOperationException("截图单排属性未按下方优先/上方回退规则摆放。");
                var slider = row.GetVisualDescendants().OfType<Slider>().Single(); slider.Value = 32;
                if (window.Editor.FontSize != 32) throw new InvalidOperationException("字号调节未更新标注模型。");
                Render(toolbar, Path.Combine(directory, "toolbar-main.png"));
                Render(row, Path.Combine(directory, above ? "toolbar-properties-above.png" : "toolbar-properties-below.png"));
                placements.Add(new { above, gap, width = toolbar.Bounds.Width, height = toolbar.Bounds.Height, propertyWidth = row.Bounds.Width, propertyHeight = row.Bounds.Height });
                slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                if (toolbar.PropertiesPopup.IsOpen || window.Editor.Tool != AnnotationTool.Text || !window.IsVisible)
                    throw new InvalidOperationException("Esc 未先关闭属性条并保留当前工具。");
                window.Editor.Select(null);
                toolbar.MainRow.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "更多操作")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(50);
                if (!toolbar.MorePopup.IsOpen || toolbar.MorePopup.Child!.GetVisualDescendants().OfType<Button>().Count() != 3)
                    throw new InvalidOperationException("截图更多菜单缺少长截图、录屏或重做。");
                toolbar.ClosePopovers();
            }
        }
        finally { session.Cancel(); }

        var pin = new PinnedWindow(controller, DesktopController.RenderText("Tbox toolbar verification"));
        try
        {
            pin.Show(); pin.Activate(); await Task.Delay(100);
            pin.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
            await Task.Delay(100);
            if (!pin.CanAnnotate) throw new InvalidOperationException("贴图空格编辑未打开主条。");
            pin.Editor.Select(AnnotationTool.Number); await Task.Delay(100);
            // Pin attributes use a native popup outside the small toolbar window, not an overlay clipped to it.
            var lifetime = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            var barWindow = lifetime.Windows.Single(w => w.Title == "Tbox 贴图工具条");
            var toolbar = (AnnotationToolbar)barWindow.Content!;
            if (!toolbar.PropertiesPopup.IsOpen || toolbar.PropertiesPopup.IsUsingOverlayLayer)
                throw new InvalidOperationException("贴图属性没有使用可伸出主条窗口的原生浮层。");
            var slider = toolbar.PropertiesSurface.GetVisualDescendants().OfType<Slider>().Single(); slider.Value = 36;
            if (pin.Editor.NumberSize != 36) throw new InvalidOperationException("贴图序号大小未更新。");
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            if (!pin.CanAnnotate || !toolbar.PropertiesPopup.IsOpen)
                throw new InvalidOperationException("属性滑条上的 Enter 不应结束贴图编辑。");
            Render(toolbar, Path.Combine(directory, "pin-toolbar-main.png"));
            Render(toolbar.PropertiesSurface, Path.Combine(directory, "pin-toolbar-properties.png"));
            toolbar.DismissPopover(); pin.FinishEditing();
            if (pin.CanAnnotate || toolbar.PropertiesPopup.IsOpen) throw new InvalidOperationException("贴图结束编辑后仍有工具条残留。");
        }
        finally { pin.Close(); }
        return new { success = true, capturePlacements = placements, nativePinProperties = true, contextualSize = true, layeredEscape = true, moreMenu = true };
    }

    private static void Render(Control control, string path)
    {
        control.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(control.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(control.Bounds.Height))), new Vector(96, 96));
        bitmap.Render(control); bitmap.Save(path);
    }
}
