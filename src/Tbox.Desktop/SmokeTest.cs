using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Tbox.Core;

namespace Tbox.Desktop;

internal static class SmokeTest
{
    public static async Task RunAsync(DesktopController controller, string[] args)
    {
        var index = Array.IndexOf(args, "--smoke-test");
        var directory = index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(Path.GetTempPath(), "tbox-smoke");
        Directory.CreateDirectory(directory);
        try
        {
            await Task.Delay(300);
            using var picture = DesktopController.RenderText("TBOX 12345\n跨平台截图与文字识别");
            await File.WriteAllBytesAsync(Path.Combine(directory, "ocr-fixture.png"), picture.Png);
            var ocr = await controller.Platform.RecognizeAsync(picture.Png, CancellationToken.None);
            if (!ocr.Text.Contains("12345")) throw new InvalidOperationException("OCR 没有识别出测试数字。");
            using var cropped = picture.Crop(new PixelRegion(10, 10, 200, 50));
            if (cropped.Bitmap.PixelSize != new PixelSize(200, 50)) throw new InvalidOperationException("裁剪像素尺寸错误。");
            var captureVerified = false;
            var displayCount = 0;
            if (args.Contains("--verify-capture") && controller.Platform.HasScreenPermission)
            {
                var displays = await controller.Platform.CaptureAsync(CancellationToken.None);
                displayCount = displays.Count;
                foreach (var display in displays)
                {
                    using var frame = new Models.Picture(display.Png);
                    if (display.Width <= 0 || display.Height <= 0 || frame.Bitmap.PixelSize.Width < display.Width)
                        throw new InvalidOperationException("截图未保留屏幕原始像素。");
                }
                captureVerified = displayCount > 0;
            }
            using var preview = new RenderTargetBitmap(new PixelSize(760, 660), new Vector(96, 96));
            preview.Render(controller.Main);
            preview.Save(Path.Combine(directory, "main-window.png"));
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                success = true, nativeOcr = true, pixelCrop = true, uiRendered = true,
                screenPermission = controller.Platform.HasScreenPermission, captureVerified, displayCount, recognizedText = ocr.Text
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { success = false, error = error.ToString() }));
            Environment.ExitCode = 1;
        }
        finally { controller.Quit(); }
    }
}
