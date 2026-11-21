using Avalonia.Controls;
using Avalonia.Media;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop;

internal static class ScreenshotBenchmark
{
    public static async Task<object> RunAsync(DesktopController controller)
    {
        var platform = controller.Platform;
        if (!platform.HasScreenPermission) throw new InvalidOperationException("截图性能验证需要现有屏幕录制权限。");
        var displays = await platform.CaptureAsync(CancellationToken.None);
        var fixtures = new List<(CapturedDisplay Display, Window Window)>();
        try
        {
            foreach (var display in displays)
            {
                var fixture = new Window
                {
                    Width = 128, Height = 96, SystemDecorations = SystemDecorations.None,
                    ShowInTaskbar = false, Topmost = true, CanResize = false,
                    Background = new SolidColorBrush(Color.FromRgb(240, 24, 32))
                };
                fixtures.Add((display, fixture));
                fixture.Show();
                platform.PlaceWindow(fixture.TryGetPlatformHandle()!.Handle,
                    new(display.Left + display.Width / 3, display.Top + display.Height / 3, 128, 96), overlay: false);
            }
            await Task.Delay(300);
            var first = await controller.MeasureScreenshotAsync();
            foreach (var (display, fixture) in fixtures)
            {
                var frame = first.Displays.Single(item => item.Id == display.Id);
                if (frame.Left != display.Left || frame.Top != display.Top || frame.Width != display.Width ||
                    frame.Height != display.Height || frame.PixelWidth != display.PixelWidth || frame.PixelHeight != display.PixelHeight)
                    throw new InvalidOperationException("截图显示器的坐标或像素尺寸变化。");
                var fixtureBounds = platform.GetWindowPlacement(fixture.TryGetPlatformHandle()!.Handle);
                var x = (int)((fixtureBounds.Left + fixtureBounds.Width / 2 - frame.Left) * frame.PixelWidth / frame.Width);
                var y = (int)((fixtureBounds.Top + fixtureBounds.Height / 2 - frame.Top) * frame.PixelHeight / frame.Height);
                if (frame.BgraPixels is not { } pixels || x < 0 || x >= frame.PixelWidth || y < 0 || y >= frame.PixelHeight)
                    throw new InvalidOperationException("色块坐标超出截图范围。");
                var offset = checked((y * frame.PixelWidth + x) * 4);
                if (pixels[offset] > 85 || pixels[offset + 1] > 85 || pixels[offset + 2] < 180 || pixels[offset + 3] != 255)
                    throw new InvalidOperationException("截图中色块的坐标或颜色通道不符。");
            }
            var continuous = new double[30];
            for (var i = 0; i < continuous.Length; i++)
            {
                await Task.Delay(75);
                continuous[i] = (await controller.MeasureScreenshotAsync()).Milliseconds;
            }
            var ordered = continuous.Order().ToArray();
            return new { success = true, colorsVerified = true, displayCount = displays.Count,
                firstMs = first.Milliseconds, continuousMs = continuous, p95Ms = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1] };
        }
        finally
        {
            foreach (var (_, fixture) in fixtures) fixture.Close();
        }
    }
}
