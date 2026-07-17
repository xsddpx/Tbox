using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Media.Imaging;
using Tbox.Core;

namespace Tbox.App;

internal sealed record CapturedScreen(int Left, int Top, int Width, int Height, byte[] Pixels)
{
    internal static async Task<CapturedScreen> LoadAsync(string filePath)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        return new CapturedScreen(
            0,
            0,
            checked((int)decoder.PixelWidth),
            checked((int)decoder.PixelHeight),
            pixelData.DetachPixelData());
    }

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

    internal async Task<BitmapImage> CreateBitmapSourceAsync(int? maximumDecodeDimension = null)
    {
        using var stream = await CreatePngStreamAsync();
        var decodeWidth = Width;
        var decodeHeight = Height;
        if (maximumDecodeDimension is > 0 && Math.Max(Width, Height) > maximumDecodeDimension.Value)
        {
            var scale = maximumDecodeDimension.Value / (double)Math.Max(Width, Height);
            decodeWidth = Math.Max(1, checked((int)Math.Round(Width * scale)));
            decodeHeight = Math.Max(1, checked((int)Math.Round(Height * scale)));
        }
        var source = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Physical,
            DecodePixelWidth = decodeWidth,
            DecodePixelHeight = decodeHeight
        };
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
