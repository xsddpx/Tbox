namespace Tbox.Core;

public readonly record struct PixelRegion(int Left, int Top, int Width, int Height);

public static class CaptureRegionPolicy
{
    public static PixelRegion MapToPixels(
        double surfaceWidth,
        double surfaceHeight,
        double left,
        double top,
        double width,
        double height,
        int pixelWidth,
        int pixelHeight)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(surfaceWidth));
        if (pixelWidth <= 0 || pixelHeight <= 0) throw new ArgumentOutOfRangeException(nameof(pixelWidth));

        left = Math.Clamp(left, 0, surfaceWidth);
        top = Math.Clamp(top, 0, surfaceHeight);
        var right = Math.Clamp(left + Math.Max(0, width), left, surfaceWidth);
        var bottom = Math.Clamp(top + Math.Max(0, height), top, surfaceHeight);

        var pixelLeft = Math.Clamp((int)Math.Floor(left / surfaceWidth * pixelWidth), 0, pixelWidth - 1);
        var pixelTop = Math.Clamp((int)Math.Floor(top / surfaceHeight * pixelHeight), 0, pixelHeight - 1);
        var pixelRight = Math.Clamp((int)Math.Ceiling(right / surfaceWidth * pixelWidth), pixelLeft + 1, pixelWidth);
        var pixelBottom = Math.Clamp((int)Math.Ceiling(bottom / surfaceHeight * pixelHeight), pixelTop + 1, pixelHeight);
        return new PixelRegion(pixelLeft, pixelTop, pixelRight - pixelLeft, pixelBottom - pixelTop);
    }

    public static byte[] CropBgra32(byte[] source, int sourceWidth, int sourceHeight, PixelRegion region)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (source.Length != checked(sourceWidth * sourceHeight * 4)) throw new ArgumentException("像素缓冲区大小不正确。", nameof(source));
        if (region.Left < 0 || region.Top < 0 || region.Width <= 0 || region.Height <= 0 ||
            region.Left + region.Width > sourceWidth || region.Top + region.Height > sourceHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        var result = new byte[checked(region.Width * region.Height * 4)];
        var sourceStride = sourceWidth * 4;
        var targetStride = region.Width * 4;
        for (var row = 0; row < region.Height; row++)
        {
            System.Buffer.BlockCopy(
                source,
                ((region.Top + row) * sourceStride) + (region.Left * 4),
                result,
                row * targetStride,
                targetStride);
        }
        return result;
    }
}
