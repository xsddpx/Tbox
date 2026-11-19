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
        if (Bitmap.Format is { } format && (format == PixelFormat.Bgra8888 || format == PixelFormat.Rgba8888) &&
            Bitmap.AlphaFormat is { } alpha)
        {
            try { return CopyRegion(Bitmap, region, format, alpha); }
            catch (NotSupportedException) { /* A transcoded bitmap may only support full-frame conversion. */ }
        }
        using var converted = CopyBitmap(Bitmap);
        return CopyRegion(converted, region, PixelFormat.Bgra8888, AlphaFormat.Premul);
    }

    private static Picture CopyRegion(Bitmap bitmap, PixelRegion region, PixelFormat format, AlphaFormat alpha)
    {
        var target = new WriteableBitmap(new PixelSize(region.Width, region.Height), new Vector(96, 96), format, alpha);
        try
        {
            using (var frame = target.Lock())
                bitmap.CopyPixels(new PixelRect(region.Left, region.Top, region.Width, region.Height),
                    frame.Address, checked(frame.RowBytes * region.Height), frame.RowBytes);
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

    public byte[] GetPixels() => _pixels ??= CopyPixels();

    internal byte[] CopyPixels()
    {
        using var decoded = new WriteableBitmap(Bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var frame = decoded.Lock();
        Bitmap.CopyPixels(frame, AlphaFormat.Unpremul);
        var stride = checked(Bitmap.PixelSize.Width * 4);
        var pixels = new byte[checked(stride * Bitmap.PixelSize.Height)];
        for (var y = 0; y < Bitmap.PixelSize.Height; y++)
            Marshal.Copy(frame.Address + y * frame.RowBytes, pixels, y * stride, stride);
        return pixels;
    }
}
