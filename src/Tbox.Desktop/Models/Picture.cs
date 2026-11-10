using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Tbox.Core;

namespace Tbox.Desktop.Models;

public sealed class Picture : IDisposable
{
    private byte[]? _png;
    public byte[] Png
    {
        get
        {
            if (_png is not null) return _png;
            using var output = new MemoryStream();
            Bitmap.Save(output);
            return _png = output.ToArray();
        }
    }
    internal bool HasEncodedPng => _png is not null;
    public Bitmap Bitmap { get; }
    public string? CachePath { get; set; }
    private byte[]? _pixels;

    public Picture(byte[] png)
    {
        _png = png;
        Bitmap = new Bitmap(new MemoryStream(png, writable: false));
    }

    private Picture(Bitmap bitmap) => Bitmap = bitmap;

    internal Picture Clone() => new Picture(CopyBitmap(Bitmap)) { CachePath = CachePath };

    internal static Picture FromBitmap(Bitmap source) => new(CopyBitmap(source));

    private static Bitmap CopyBitmap(Bitmap source)
    {
        var bitmap = new WriteableBitmap(source.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using (var target = bitmap.Lock()) source.CopyPixels(target, AlphaFormat.Premul);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    public Picture Crop(PixelRegion region)
    {
        if (region.Left < 0 || region.Top < 0 || region.Width <= 0 || region.Height <= 0 ||
            (long)region.Left + region.Width > Bitmap.PixelSize.Width ||
            (long)region.Top + region.Height > Bitmap.PixelSize.Height)
            throw new ArgumentOutOfRangeException(nameof(region));
        // Decode to a known format before copying. The original capture is never resampled.
        using var decoded = new WriteableBitmap(Bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var source = decoded.Lock();
        Bitmap.CopyPixels(source, AlphaFormat.Premul);
        var target = new WriteableBitmap(new PixelSize(region.Width, region.Height), new Vector(96, 96), source.Format, AlphaFormat.Premul);
        try
        {
            using (var destination = target.Lock())
            {
                var bytesPerPixel = source.Format.BitsPerPixel / 8;
                var row = new byte[checked(region.Width * bytesPerPixel)];
                for (var y = 0; y < region.Height; y++)
                {
                    Marshal.Copy(source.Address + (region.Top + y) * source.RowBytes + region.Left * bytesPerPixel, row, 0, row.Length);
                    Marshal.Copy(row, 0, destination.Address + y * destination.RowBytes, row.Length);
                }
            }
            return new Picture(target);
        }
        catch { target.Dispose(); throw; }
    }

    public void Dispose() => Bitmap.Dispose();

    public static Picture FromPixels(byte[] pixels, int width, int height)
    {
        if (width < 1 || height < 1 || pixels.LongLength != (long)width * height * 4)
            throw new ArgumentException("图像尺寸与像素数据不匹配。");
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        try
        {
            using (var target = bitmap.Lock())
                for (var y = 0; y < height; y++) Marshal.Copy(pixels, y * width * 4, target.Address + y * target.RowBytes, width * 4);
            return new Picture(bitmap);
        }
        catch { bitmap.Dispose(); throw; }
    }

    public byte[] GetPixels()
    {
        if (_pixels is not null) return _pixels;
        using var decoded = new WriteableBitmap(Bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var frame = decoded.Lock();
        Bitmap.CopyPixels(frame, AlphaFormat.Unpremul);
        var stride = checked(Bitmap.PixelSize.Width * 4);
        _pixels = new byte[checked(stride * Bitmap.PixelSize.Height)];
        for (var y = 0; y < Bitmap.PixelSize.Height; y++)
            Marshal.Copy(frame.Address + y * frame.RowBytes, _pixels, y * stride, stride);
        return _pixels;
    }
}
