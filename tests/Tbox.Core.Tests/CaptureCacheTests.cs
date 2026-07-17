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
