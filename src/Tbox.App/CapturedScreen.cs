using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Media.Imaging;
using Tbox.Core;

namespace Tbox.App;

internal sealed record CapturedScreen(int Left, int Top, int Width, int Height, byte[] Pixels)
{
    internal CapturedScreen Crop(int left, int top, int width, int height)
    {
        left = Math.Clamp(left, 0, Width - 1);
        top = Math.Clamp(top, 0, Height - 1);
        width = Math.Clamp(width, 1, Width - left);
        height = Math.Clamp(height, 1, Height - top);

        var cropped = CaptureRegionPolicy.CropBgra32(Pixels, Width, Height, new PixelRegion(left, top, width, height));

        return new CapturedScreen(Left + left, Top + top, width, height, cropped);
    }

    internal async Task<InMemoryRandomAccessStream> CreatePngStreamAsync()
    {
        var stream = new InMemoryRandomAccessStream();
        try
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                (uint)Width,
                (uint)Height,
                96,
                96,
                Pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal async Task<BitmapImage> CreateBitmapSourceAsync()
    {
        using var stream = await CreatePngStreamAsync();
        var source = new BitmapImage();
        await source.SetSourceAsync(stream);
        return source;
    }

    internal async Task<byte[]> CreatePngBytesAsync()
    {
        using var stream = await CreatePngStreamAsync();
        var length = checked((uint)stream.Size);
        var bytes = new byte[checked((int)length)];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var loaded = await reader.LoadAsync(length);
        if (loaded != length) throw new EndOfStreamException("无法完整读取截图 PNG 数据。");
        reader.ReadBytes(bytes);
        return bytes;
    }
}
