using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace Tbox.Desktop.Models;

/// <summary>
/// Owns an independent image until the OS releases the clipboard provider.
/// Clipboard serialization can happen long after the capture/result window closes.
/// </summary>
internal sealed class ClipboardImageTransfer : IDataTransfer, IAsyncDataTransfer
{
    private Picture? _picture;
    private readonly DataTransferItem[] _items;
    public IReadOnlyList<DataFormat> Formats { get; } = [DataFormat.Bitmap];
    IReadOnlyList<IDataTransferItem> IDataTransfer.Items => _items;
    IReadOnlyList<IAsyncDataTransferItem> IAsyncDataTransfer.Items => _items;

    public ClipboardImageTransfer(byte[] png)
    {
        _picture = new Picture(png);
        _items = [DataTransferItem.Create(DataFormat.Bitmap, () => _picture?.Bitmap)];
    }

    public ClipboardImageTransfer(Picture picture)
    {
        _picture = picture.Clone();
        _items = [DataTransferItem.Create(DataFormat.Bitmap, () => _picture?.Bitmap)];
    }

    public void Dispose() => Interlocked.Exchange(ref _picture, null)?.Dispose();
}
