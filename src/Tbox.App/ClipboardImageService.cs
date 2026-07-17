using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Tbox.App;

internal static class ClipboardImageService
{
    internal static async Task<byte[]?> TryReadPngAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap)) return null;

        var reference = await content.GetBitmapAsync();
        if (reference is null) return null;

        using var input = await reference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        var length = checked((uint)output.Size);
        var bytes = new byte[checked((int)length)];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        var loaded = await reader.LoadAsync(length);
        if (loaded != length) throw new EndOfStreamException("无法完整读取剪贴板图像。");
        reader.ReadBytes(bytes);
        return bytes;
    }

    internal static async Task CopyFileAsync(string filePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        var content = new DataPackage();
        content.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Clipboard.SetContent(content);
        Clipboard.Flush();
    }
}
