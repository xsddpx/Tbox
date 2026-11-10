using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Views;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Tbox.Desktop.Tests.TestApplication))]

namespace Tbox.Desktop.Tests;

public sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(DesignTokens.CreateTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class PictureTests
{
    [AvaloniaFact]
    public void RawPixelsDisplayAndCropWithoutEncodingAndEncodeOnlyOnDemand()
    {
        byte[] pixels = [10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255, 100, 110, 120, 255];
        using var picture = Picture.FromPixels(pixels, 2, 2);
        Assert.False(picture.HasEncodedPng);
        Assert.Equal(new PixelSize(2, 2), picture.Bitmap.PixelSize);
        Assert.Equal(pixels, picture.GetPixels());
        using var crop = picture.Crop(new PixelRegion(1, 0, 1, 2));
        Assert.False(picture.HasEncodedPng);
        Assert.False(crop.HasEncodedPng);
        Assert.Equal(new byte[] { 40, 50, 60, 255, 100, 110, 120, 255 }, crop.GetPixels());
        var png = crop.Png;
        Assert.True(crop.HasEncodedPng);
        Assert.Same(png, crop.Png);
        using var decoded = new Picture(png);
        Assert.Equal(crop.GetPixels(), decoded.GetPixels());
    }

    [AvaloniaFact]
    public void PixelCloneSurvivesSourceDisposalAndDoesNotEncodeEitherImage()
    {
        var source = Picture.FromPixels([10, 20, 30, 255], 1, 1);
        using var clone = source.Clone();
        Assert.False(source.HasEncodedPng);
        Assert.False(clone.HasEncodedPng);
        source.Dispose();
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, clone.GetPixels());
    }

    [AvaloniaFact]
    public void CapturedPixelsSupportInteractiveUseAndLegacyPngConsumers()
    {
        var display = new Platform.CapturedDisplay(1, -100, 0, 1, 1, [])
        { BgraPixels = [10, 20, 30, 255], PixelWidth = 1, PixelHeight = 1 };
        using var picture = display.CreatePicture();
        Assert.False(picture.HasEncodedPng);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, picture.GetPixels());
        using var legacy = new Picture(display.Png);
        Assert.Equal(picture.GetPixels(), legacy.GetPixels());
        Assert.Same(display.Png, display.Png);
    }

    [AvaloniaFact]
    public void CroppingRejectsOutOfBoundsBeforeAccessingPixels()
    {
        using var picture = DesktopController.RenderText("测试");
        Assert.Throws<ArgumentOutOfRangeException>(() => picture.Crop(new PixelRegion(-1, 0, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => picture.Crop(new PixelRegion(0, 0, int.MaxValue, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => picture.Crop(new PixelRegion(0, 0, 0, 1)));
    }
    [AvaloniaFact]
    public void CroppingKeepsExactPixelsWithoutScalingOrOverlayGraphics()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(6, 4), new Vector(192, 192), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var pixels = bitmap.Lock())
        {
            for (var y = 0; y < 4; y++)
            for (var x = 0; x < 6; x++)
                Marshal.Copy(new byte[] { (byte)(x * 30), (byte)(y * 40), 50, 255 }, 0, pixels.Address + y * pixels.RowBytes + x * 4, 4);
        }
        using var output = new MemoryStream(); bitmap.Save(output);
        using var original = new Picture(output.ToArray());
        using var cropped = original.Crop(new PixelRegion(2, 1, 3, 2));
        Assert.Equal(new PixelSize(3, 2), cropped.Bitmap.PixelSize);
        using var result = new WriteableBitmap(new PixelSize(3, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = result.Lock(); cropped.Bitmap.CopyPixels(frame, AlphaFormat.Premul);
        var firstPixel = new byte[4]; Marshal.Copy(frame.Address, firstPixel, 0, 4);
        Assert.Equal(new byte[] { 60, 40, 50, 255 }, firstPixel);
        var lastPixel = new byte[4]; Marshal.Copy(frame.Address + frame.RowBytes + 8, lastPixel, 0, 4);
        Assert.Equal(new byte[] { 120, 80, 50, 255 }, lastPixel);
    }

    [AvaloniaFact]
    public void TextCardsPreserveNewlinesAndProduceHighResolutionPng()
    {
        using var oneLine = DesktopController.RenderText("一行文字");
        using var twoLines = DesktopController.RenderText("一行文字\n第二行文字");
        Assert.Equal(1280, twoLines.Bitmap.PixelSize.Width);
        Assert.True(twoLines.Bitmap.PixelSize.Height > oneLine.Bitmap.PixelSize.Height);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, twoLines.Png[..4]);
    }
}
