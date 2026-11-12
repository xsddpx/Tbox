namespace Tbox.Core;

/// <summary>
/// Builds the packed device-independent bitmap (CF_DIB) that Windows applications read from the clipboard.
/// Rows are stored bottom-up and every pixel is opaque: the source is a screen image, never a composited
/// layer, and a 32 bpp DIB whose alpha channel is not fully opaque is rendered as transparent or black by
/// several consumers.
/// </summary>
public static class ClipboardImageDib
{
    public const int HeaderSize = 40;
    public const int BytesPerPixel = 4;

    public static byte[] Build(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 1) throw new ArgumentOutOfRangeException(nameof(height));
        if (bgra.LongLength != (long)width * height * BytesPerPixel)
            throw new ArgumentException("像素数据长度与图像尺寸不匹配。", nameof(bgra));

        var imageSize = checked(width * height * BytesPerPixel);
        var dib = new byte[HeaderSize + imageSize];
        WriteInt32(dib, 0, HeaderSize);                 // biSize
        WriteInt32(dib, 4, width);                      // biWidth
        WriteInt32(dib, 8, height);                     // biHeight: positive, rows are stored bottom-up
        WriteUInt16(dib, 12, 1);                        // biPlanes
        WriteUInt16(dib, 14, 32);                       // biBitCount
        WriteInt32(dib, 16, 0);                         // biCompression = BI_RGB
        WriteInt32(dib, 20, imageSize);                 // biSizeImage

        for (var y = 0; y < height; y++)
        {
            var source = y * width * BytesPerPixel;
            var target = HeaderSize + (height - 1 - y) * width * BytesPerPixel;
            Buffer.BlockCopy(bgra, source, dib, target, width * BytesPerPixel);
            for (var x = 0; x < width; x++) dib[target + x * BytesPerPixel + 3] = 255;
        }
        return dib;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }
}
