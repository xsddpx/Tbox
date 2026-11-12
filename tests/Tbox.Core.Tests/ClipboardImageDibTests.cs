using Xunit;

namespace Tbox.Core.Tests;

public sealed class ClipboardImageDibTests
{
    [Fact]
    public void DescribesAnOpaqueBottomUpBitmap()
    {
        var pixels = Pixels(2, 2);
        var dib = ClipboardImageDib.Build(pixels, 2, 2);
        Assert.Equal(ClipboardImageDib.HeaderSize + 2 * 2 * 4, dib.Length);
        Assert.Equal(40, BitConverter.ToInt32(dib, 0));
        Assert.Equal(2, BitConverter.ToInt32(dib, 4));
        Assert.Equal(2, BitConverter.ToInt32(dib, 8)); // positive height: rows are stored bottom-up
        Assert.Equal(1, BitConverter.ToInt16(dib, 12));
        Assert.Equal(32, BitConverter.ToInt16(dib, 14));
        Assert.Equal(0, BitConverter.ToInt32(dib, 16));
        Assert.Equal(2 * 2 * 4, BitConverter.ToInt32(dib, 20));
    }

    [Fact]
    public void StoresTheLastSourceRowFirst()
    {
        var pixels = Pixels(2, 2);
        var dib = ClipboardImageDib.Build(pixels, 2, 2);
        // Source row 1 (B=20) becomes the first row of the bottom-up bitmap.
        Assert.Equal(20, dib[ClipboardImageDib.HeaderSize]);
        Assert.Equal(20, dib[ClipboardImageDib.HeaderSize + 4]);
        // Source row 0 (B=10) becomes the last row.
        var lastRow = ClipboardImageDib.HeaderSize + 2 * 4;
        Assert.Equal(10, dib[lastRow]);
        Assert.Equal(10, dib[lastRow + 4]);
    }

    [Fact]
    public void MakesEveryPixelOpaque()
    {
        var pixels = Pixels(2, 2);
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 0;
        var dib = ClipboardImageDib.Build(pixels, 2, 2);
        for (var index = ClipboardImageDib.HeaderSize + 3; index < dib.Length; index += 4) Assert.Equal(255, dib[index]);
    }

    [Fact]
    public void RejectsPixelsThatDoNotMatchTheSize()
    {
        Assert.Throws<ArgumentException>(() => ClipboardImageDib.Build(Pixels(2, 2), 3, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardImageDib.Build([], 0, 0));
    }

    private static byte[] Pixels(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            pixels[index] = (byte)(10 * (y + 1));
            pixels[index + 1] = (byte)(30 + x);
            pixels[index + 2] = (byte)(200 - x);
            pixels[index + 3] = 255;
        }
        return pixels;
    }
}
