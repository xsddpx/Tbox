namespace Tbox.Desktop.Models;

internal sealed record CacheLocation(string Path, bool IsFallback)
{
    public static CacheLocation Prepare(string preferred, string fallback)
    {
        try { VerifyWritable(preferred); return new(preferred, false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (string.Equals(preferred, fallback, StringComparison.Ordinal)) throw;
            VerifyWritable(fallback); return new(fallback, true);
        }
    }
    public static void VerifyWritable(string directory)
    {
        Directory.CreateDirectory(directory);
        using var probe = new FileStream(System.IO.Path.Combine(directory, $".tbox-write-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        probe.WriteByte(0); probe.Flush(true);
    }
}
