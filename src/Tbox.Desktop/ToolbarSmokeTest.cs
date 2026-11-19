using System.Diagnostics;
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
        object pinZoom;
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
            pinZoom = await VerifyPinZoomAsync(controller.Platform, pin, directory);
        }
        finally { pin.Close(); }
        return new { success = true, capturePlacements = placements, nativePinProperties = true, contextualSize = true, layeredEscape = true, moreMenu = true, pinZoom };
    }

    private static async Task<object> VerifyPinZoomAsync(IPlatformServices platform, PinnedWindow pin, string directory)
    {
        var handle = pin.TryGetPlatformHandle()!.Handle;
        var surface = pin.GetVisualDescendants().OfType<PinSurface>().Single();
        var pointer = new Pointer(1, PointerType.Mouse, true);
        var point = new Point(16, 12);
        surface.RaiseEvent(new PointerPressedEventArgs(surface, pointer, pin, point, 0,
            new PointerPointProperties(RawInputModifiers.MiddleMouseButton, PointerUpdateKind.MiddleButtonPressed), KeyModifiers.None, 1));
        await Task.Delay(50);
        var original = platform.GetWindowPlacement(handle);
        var pixelsPerUnit = pin.RenderScaling / platform.DesktopUnitsPerDip(handle);
        if (Math.Abs(original.Width * pixelsPerUnit - pin.Region.Width) > 1 ||
            Math.Abs(original.Height * pixelsPerUnit - pin.Region.Height) > 1)
            throw new InvalidOperationException("中键未以原始像素尺寸恢复贴图。");
        var badge = pin.GetVisualDescendants().OfType<TextBlock>().Single(label => label.Text == "100%");
        if (!badge.IsEffectivelyVisible) throw new InvalidOperationException("恢复尺寸后没有显示缩放比例。");
        Render(pin, Path.Combine(directory, "pin-zoom-badge.png"));

        var samples = new List<double>();
        var unitsPerDip = platform.DesktopUnitsPerDip(handle);
        var anchor = new Point(original.Left + point.X * unitsPerDip, original.Top + point.Y * unitsPerDip);
        for (var i = 0; i < 20; i++)
        {
            var bounds = platform.GetWindowPlacement(handle);
            var position = new Point((anchor.X - bounds.Left) / unitsPerDip, (anchor.Y - bounds.Top) / unitsPerDip);
            var watch = Stopwatch.StartNew();
            var resized = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnSizeChanged(object? sender, SizeChangedEventArgs args) => resized.TrySetResult(watch.Elapsed.TotalMilliseconds);
            pin.SizeChanged += OnSizeChanged;
            try
            {
                var delta = i % 2 == 0 ? 0.5 : -0.5;
                for (var portion = 0; portion < 2; portion++)
                    surface.RaiseEvent(new PointerWheelEventArgs(surface, pointer, pin, position, 0,
                        new PointerPointProperties(), KeyModifiers.None, new Vector(0, delta)));
                samples.Add(await resized.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            finally { pin.SizeChanged -= OnSizeChanged; }
        }
        var final = platform.GetWindowPlacement(handle);
        if (Math.Abs(final.Width - original.Width) * pixelsPerUnit > 1 || Math.Abs(final.Left - original.Left) * pixelsPerUnit > 1 ||
            Math.Abs(final.Height - original.Height) * pixelsPerUnit > 1 || Math.Abs(final.Top - original.Top) * pixelsPerUnit > 1)
            throw new InvalidOperationException("贴图往返缩放后未回到原尺寸和位置。");
        await Task.Delay(1100);
        if (badge.IsEffectivelyVisible) throw new InvalidOperationException("缩放比例提示未自动隐藏。");
        samples.Sort();
        return new { samples = samples.Count, medianMs = (samples[9] + samples[10]) / 2, p95Ms = samples[18] };
    }

    private static void Render(Control control, string path)
    {
        control.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(control.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(control.Bounds.Height))), new Vector(96, 96));
        bitmap.Render(control); bitmap.Save(path);
    }
}
