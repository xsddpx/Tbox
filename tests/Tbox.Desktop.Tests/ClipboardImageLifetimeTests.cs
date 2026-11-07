using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Tbox.Desktop.Models;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class ClipboardImageLifetimeTests
{
    [AvaloniaFact]
    public void ReleasingClipboardOwnershipLeavesTheOriginalImageIntact()
    {
        using var picture = Picture.FromPixels([10, 20, 30, 255, 100, 110, 120, 255], 2, 1);
        var transfer = new ClipboardImageTransfer(picture.Png);
        var item = ((IDataTransfer)transfer).Items.Single();
        Assert.NotSame(picture.Bitmap, item.TryGetValue(DataFormat.Bitmap));
        transfer.Dispose(); transfer.Dispose();
        Assert.Null(item.TryGetValue(DataFormat.Bitmap));
        using var encoded = new MemoryStream(); picture.Bitmap.Save(encoded);
        Assert.True(encoded.Length > 0);
    }

    [AvaloniaFact]
    public async Task LaterCopyReplacesThePixelsWithoutDependingOnEitherSource()
    {
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller); controller.Main.Show();
        var clipboard = controller.Main.Clipboard!;
        try
        {
            using (var first = Picture.FromPixels([10, 20, 30, 255], 1, 1)) await controller.CopyAsync(first);
            byte[] expected = [50, 60, 70, 255, 80, 90, 100, 255];
            using (var second = Picture.FromPixels(expected, 2, 1)) await controller.CopyAsync(second);
            var delayed = await clipboard.TryGetBitmapAsync(); Assert.NotNull(delayed);
            using var encoded = new MemoryStream(); delayed.Save(encoded);
            using var result = new Picture(encoded.ToArray());
            Assert.Equal(expected, result.GetPixels());
        }
        finally { await clipboard.ClearAsync(); controller.Dispose(); controller.Main.Close(); }
    }

    [AvaloniaFact]
    public async Task CopiedImageRemainsReadableAfterTheSourceIsClosed()
    {
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller); controller.Main.Show();
        var clipboard = controller.Main.Clipboard!;
        try
        {
            var picture = DesktopController.RenderText("TBOX clipboard lifetime 12345");
            var size = picture.Bitmap.PixelSize;
            await controller.CopyAsync(picture);
            picture.Dispose();
            // Native clipboard providers serialize lazily when another app asks for pixels.
            // The headless clipboard exposes that delayed value without encoding it first.
            for (var request = 0; request < 3; request++)
            {
                var delayed = await clipboard.TryGetBitmapAsync();
                Assert.NotNull(delayed);
                using var encoded = new MemoryStream(); delayed.Save(encoded);
                Assert.True(encoded.Length > 0); Assert.Equal(size, delayed.PixelSize);
            }
        }
        finally { await clipboard.ClearAsync(); controller.Dispose(); controller.Main.Close(); }
    }
}
