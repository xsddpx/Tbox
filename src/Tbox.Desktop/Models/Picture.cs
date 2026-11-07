using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Tbox.Core;

namespace Tbox.Desktop.Models;

public sealed class Picture : IDisposable
{
    public byte[] Png { get; }
    public Bitmap Bitmap { get; }
    public string? CachePath { get; set; }
    private byte[]? _pixels;

    public Picture(byte[] png)
    {
        Png = png;
        Bitmap = new Bitmap(new MemoryStream(png, writable: false));
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
        using var target = new WriteableBitmap(new PixelSize(region.Width, region.Height), new Vector(96, 96), source.Format, AlphaFormat.Premul);
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
        using var output = new MemoryStream();
        target.Save(output);
        return new Picture(output.ToArray());
    }

    public void Dispose() => Bitmap.Dispose();

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
