using System.Globalization;

namespace Tbox.Core;

public sealed class CaptureCache
{
    public const int DefaultMaximumFileCount = 200;
    public const long DefaultMaximumTotalBytes = 512L * 1024 * 1024;
    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromDays(30);

    public string DirectoryPath { get; }

    public CaptureCache(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("缓存目录不能为空。", nameof(directoryPath));
        }

        DirectoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
    }

    public async Task<string> SaveAsync(ReadOnlyMemory<byte> pngBytes, CancellationToken cancellationToken = default)
    {
        if (pngBytes.IsEmpty) throw new ArgumentException("截图内容不能为空。", nameof(pngBytes));

        Directory.CreateDirectory(DirectoryPath);
        var stem = DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture);
        var filePath = Path.Combine(DirectoryPath, $"{stem}-{Guid.NewGuid():N}.png");
        var temporaryPath = filePath + ".tmp";

        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous))
            {
                await output.WriteAsync(pngBytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, filePath);
            return filePath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public string? GetLatestFilePath()
    {
        if (!Directory.Exists(DirectoryPath)) return null;

        return Directory
            .EnumerateFiles(DirectoryPath, "*.png", SearchOption.TopDirectoryOnly)
            .MaxBy(Path.GetFileName, StringComparer.Ordinal);
    }

    public int Prune(
        IEnumerable<string>? protectedFilePaths = null,
        int maximumFileCount = DefaultMaximumFileCount,
        long maximumTotalBytes = DefaultMaximumTotalBytes,
        TimeSpan? maximumAge = null)
    {
        if (maximumFileCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumFileCount));
        if (maximumTotalBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumTotalBytes));
        if (!Directory.Exists(DirectoryPath)) return 0;

        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (protectedFilePaths is not null)
        {
            foreach (var path in protectedFilePaths)
            {
                if (!string.IsNullOrWhiteSpace(path)) protectedPaths.Add(Path.GetFullPath(path));
            }
        }

        var files = new DirectoryInfo(DirectoryPath)
            .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .ToList();
        var retainedCount = files.Count;
        var retainedBytes = files.Sum(file => file.Length);
        var cutoff = DateTime.UtcNow - (maximumAge ?? DefaultMaximumAge);
        var removed = 0;

        foreach (var file in files.AsEnumerable().Reverse())
        {
            if (protectedPaths.Contains(file.FullName)) continue;
            var expired = file.LastWriteTimeUtc < cutoff;
            var exceedsLimits = retainedCount > maximumFileCount || retainedBytes > maximumTotalBytes;
            if (!expired && !exceedsLimits) continue;

            var length = file.Length;
            try
            {
                file.Delete();
                retainedCount--;
                retainedBytes -= length;
                removed++;
            }
            catch (IOException)
            {
                // A pinned image or another process may still be using the file.
            }
            catch (UnauthorizedAccessException)
            {
                // Cache cleanup is best effort and must not break capture.
            }
        }
        return removed;
    }

    public bool Delete(string filePath)
    {
        var validatedPath = ValidateCacheFilePath(filePath);
        if (!File.Exists(validatedPath)) return false;

        File.Delete(validatedPath);
        return true;
    }

    private string ValidateCacheFilePath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.Equals(parent, DirectoryPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("只能操作截图缓存目录内的 PNG 文件。", nameof(filePath));
        }

        return fullPath;
    }
}
