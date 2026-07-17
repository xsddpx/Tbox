using System.Runtime.InteropServices;
using Tbox.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Tbox.App;

internal static class ClipboardImageService
{
    private const int ClipboardBusyHResult = unchecked((int)0x800401D0);
    private const int ClipboardReadAttempts = 3;

    internal static async Task<ClipboardPinContent?> TryReadPinContentAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryReadPinContentOnceAsync();
            }
            catch (Exception exception) when (
                IsClipboardBusy(exception) && attempt < ClipboardReadAttempts)
            {
                await Task.Delay(40 * attempt);
            }
        }
    }

    private static async Task<ClipboardPinContent?> TryReadPinContentOnceAsync()
    {
        var content = Clipboard.GetContent();
        var sources = ClipboardPinContentPolicy.GetPreferredSources(
            content.Contains(StandardDataFormats.Bitmap),
            content.Contains(StandardDataFormats.StorageItems),
            content.Contains(StandardDataFormats.Text));
        Exception? lastFailure = null;
        foreach (var source in sources)
        {
            try
            {
                var pngBytes = source switch
                {
                    ClipboardPinSource.Bitmap => await TryReadBitmapAsync(content),
                    ClipboardPinSource.ImageFile => await TryReadImageFileAsync(content),
                    ClipboardPinSource.Text => await TryRenderTextAsync(content),
                    _ => null
                };
                if (pngBytes is not null)
                {
                    return new ClipboardPinContent(source, pngBytes);
                }
            }
            catch (Exception exception) when (!IsClipboardBusy(exception))
            {
                lastFailure = exception;
                AppLog.Write($"Clipboard {source} read failed, trying the next format: {exception}");
            }
        }

        if (lastFailure is not null)
        {
            throw new InvalidOperationException("剪贴板包含可贴内容，但所有读取方式均失败。", lastFailure);
        }
        return null;
    }

    private static async Task<byte[]?> TryReadBitmapAsync(DataPackageView content)
    {
        var reference = await content.GetBitmapAsync();
        if (reference is null) return null;

        using var input = await reference.OpenReadAsync();
        return await TranscodeToPngAsync(input);
    }

    private static async Task<byte[]?> TryReadImageFileAsync(DataPackageView content)
    {
        var items = await content.GetStorageItemsAsync();
        foreach (var file in items.OfType<StorageFile>())
        {
            try
            {
                using var input = await file.OpenReadAsync();
                return await TranscodeToPngAsync(input);
            }
            catch (Exception exception) when (!IsClipboardBusy(exception))
            {
                AppLog.Write($"Clipboard storage item is not a readable image ({file.Name}): {exception.Message}");
            }
        }
        return null;
    }

    private static async Task<byte[]?> TryRenderTextAsync(DataPackageView content)
    {
        var text = ClipboardPinContentPolicy.NormalizeText(await content.GetTextAsync());
        return text is null ? null : await ClipboardTextCardRenderer.RenderPngAsync(text);
    }

    private static async Task<byte[]> TranscodeToPngAsync(IRandomAccessStream input)
    {
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        return await ReadAllBytesAsync(output, "无法完整读取剪贴板图像。");
    }

    private static async Task<byte[]> ReadAllBytesAsync(
        IRandomAccessStream stream,
        string incompleteReadMessage)
    {
        stream.Seek(0);
        var length = checked((uint)stream.Size);
        var bytes = new byte[checked((int)length)];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var loaded = await reader.LoadAsync(length);
        if (loaded != length) throw new EndOfStreamException(incompleteReadMessage);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static bool IsClipboardBusy(Exception exception) =>
        exception.HResult == ClipboardBusyHResult ||
        exception is COMException { HResult: ClipboardBusyHResult } ||
        exception.InnerException is not null && IsClipboardBusy(exception.InnerException);

    internal static async Task CopyFileAsync(string filePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        var content = new DataPackage();
        content.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Clipboard.SetContent(content);
        Clipboard.Flush();
    }

    internal static async Task CopyPngAsync(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
        stream.Seek(0);

        var content = new DataPackage();
        content.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        Clipboard.SetContent(content);
        Clipboard.Flush();
    }
}

internal sealed record ClipboardPinContent(ClipboardPinSource Source, byte[] PngBytes);
