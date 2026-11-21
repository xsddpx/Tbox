using System.Numerics;
using System.Runtime.InteropServices;

namespace Tbox.Desktop.Platform;

internal static class BgraCapturePixels
{
    internal static byte[] CopyBaseline(nint source, int byteCount)
    {
        var bytes = new byte[byteCount];
        Marshal.Copy(source, bytes, 0, byteCount);
        for (var index = 3; index < byteCount; index += 4) bytes[index] = 255;
        return bytes;
    }

    public static byte[] CopyOpaque(ReadOnlySpan<uint> source)
    {
        const uint opaque = 0xff000000;
        var bytes = GC.AllocateUninitializedArray<byte>(checked(source.Length * sizeof(uint)));
        var destination = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
        var index = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var mask = new Vector<uint>(opaque);
            var lanes = Vector<uint>.Count;
            for (; index <= source.Length - lanes; index += lanes)
            {
                var pixels = new Vector<uint>(source.Slice(index, lanes));
                (pixels | mask).CopyTo(destination.Slice(index, lanes));
            }
        }
        for (; index < source.Length; index++)
            destination[index] = source[index] | opaque;
        return bytes;
    }
}
