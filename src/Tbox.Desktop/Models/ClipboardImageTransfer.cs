using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace Tbox.Desktop.Models;

/// <summary>
/// Owns an independent image until the OS releases the clipboard provider.
/// Clipboard serialization can happen long after the capture/result window closes.
/// </summary>
internal sealed class ClipboardImageTransfer : IDataTransfer, IAsyncDataTransfer
{
    private Bitmap? _bitmap;
    private readonly DataTransferItem[] _items;
    public IReadOnlyList<DataFormat> Formats { get; } = [DataFormat.Bitmap];
    IReadOnlyList<IDataTransferItem> IDataTransfer.Items => _items;
    IReadOnlyList<IAsyncDataTransferItem> IAsyncDataTransfer.Items => _items;

    public ClipboardImageTransfer(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        _bitmap = new Bitmap(stream);
        _items = [DataTransferItem.Create(DataFormat.Bitmap, () => _bitmap)];
    }

    public void Dispose() => Interlocked.Exchange(ref _bitmap, null)?.Dispose();
}
