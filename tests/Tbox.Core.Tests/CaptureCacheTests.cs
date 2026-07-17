using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class CaptureCacheTests
{
    [Fact]
    public async Task SavedCaptureBecomesLatestAndCanBeDeleted()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var cache = new CaptureCache(directory + Path.DirectorySeparatorChar);
            var filePath = await cache.SaveAsync(new byte[] { 1, 2, 3, 4 });

            Assert.Equal(filePath, cache.GetLatestFilePath());
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(filePath));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            Assert.True(cache.Delete(filePath));
            Assert.False(cache.Delete(filePath));
            Assert.Null(cache.GetLatestFilePath());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SavingAnotherCaptureKeepsPreviousCapture()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var cache = new CaptureCache(directory);
            var firstPath = await cache.SaveAsync(new byte[] { 1 });
            var secondPath = await cache.SaveAsync(new byte[] { 2 });

            Assert.True(File.Exists(firstPath));
            Assert.True(File.Exists(secondPath));
            Assert.Equal(secondPath, cache.GetLatestFilePath());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DeleteRejectsFilesOutsideTheCacheDirectory()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var cache = new CaptureCache(directory);
            var outsidePath = Path.Combine(Path.GetDirectoryName(directory)!, "outside.png");

            Assert.Throws<ArgumentException>(() => cache.Delete(outsidePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UsesLocalDateTimeAsBaseFileName()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var localTime = new DateTime(2026, 7, 13, 14, 25, 30, DateTimeKind.Local);
            var cache = new CaptureCache(directory, () => localTime);

            var filePath = await cache.SaveAsync(new byte[] { 1 });

            Assert.Equal(Path.Combine(directory, "2026-07-13_14-25-30.png"), filePath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UsesNumberedSuffixesWithoutOverwriting()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var localTime = new DateTime(2026, 7, 13, 14, 25, 30, DateTimeKind.Local);
            var cache = new CaptureCache(directory, () => localTime);
            var existingPath = Path.Combine(directory, "2026-07-13_14-25-30.png");
            await File.WriteAllBytesAsync(existingPath, [9]);

            var firstPath = await cache.SaveAsync(new byte[] { 1 });
            var secondPath = await cache.SaveAsync(new byte[] { 2 });

            Assert.Equal(Path.Combine(directory, "2026-07-13_14-25-30_001.png"), firstPath);
            Assert.Equal(Path.Combine(directory, "2026-07-13_14-25-30_002.png"), secondPath);
            Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(existingPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LatestAndPruneUseFileTimesAcrossLegacyAndReadableNames()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var cache = new CaptureCache(directory);
            var legacyPath = Path.Combine(directory, "202607131425300000000-deadbeef.png");
            var readablePath = Path.Combine(directory, "2026-07-13_14-26-30.png");
            File.WriteAllBytes(legacyPath, [1]);
            File.WriteAllBytes(readablePath, [2]);
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(legacyPath, now.AddMinutes(-2));
            File.SetLastWriteTimeUtc(readablePath, now.AddMinutes(-1));

            Assert.Equal(readablePath, cache.GetLatestFilePath());

            var removed = cache.Prune(maximumFileCount: 1, maximumTotalBytes: 100);

            Assert.Equal(1, removed);
            Assert.False(File.Exists(legacyPath));
            Assert.True(File.Exists(readablePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PruneHonorsLimitsAndKeepsProtectedCaptures()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var cache = new CaptureCache(directory);
            var firstPath = await cache.SaveAsync(new byte[] { 1 });
            await Task.Delay(2);
            var secondPath = await cache.SaveAsync(new byte[] { 2 });
            await Task.Delay(2);
            var thirdPath = await cache.SaveAsync(new byte[] { 3 });

            var removed = cache.Prune([firstPath], maximumFileCount: 2, maximumTotalBytes: 2);

            Assert.Equal(1, removed);
            Assert.True(File.Exists(firstPath));
            Assert.False(File.Exists(secondPath));
            Assert.True(File.Exists(thirdPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Tbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
