using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void DefaultHotkeyIsCtrlG()
    {
        var settings = new AppSettings();
        Assert.Equal("Ctrl + G", settings.FolderJumpHotkey.DisplayText);
        Assert.True(settings.ScreenPasteEnabled);
    }

    [Fact]
    public void CaptureSelectionMapsFromDisplayUnitsToPixels()
    {
        var region = CaptureRegionPolicy.MapToPixels(100, 50, 10, 5, 20, 10, 200, 100);
        Assert.Equal(new PixelRegion(20, 10, 40, 20), region);
    }

    [Fact]
    public void CaptureSelectionMapsMixedDpiSurfaceWithoutExtraScaling()
    {
        var region = CaptureRegionPolicy.MapToPixels(2560, 1440, 0, 0, 2560, 1440, 3840, 2160);
        Assert.Equal(new PixelRegion(0, 0, 3840, 2160), region);
    }

    [Fact]
    public void CropCopiesOnlySelectedBgraPixels()
    {
        var source = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var cropped = CaptureRegionPolicy.CropBgra32(source, 2, 2, new PixelRegion(1, 0, 1, 2));
        Assert.Equal(new byte[] { 4, 5, 6, 7, 12, 13, 14, 15 }, cropped);
    }

    [Fact]
    public void InvalidShellLocationFallsBackToUserProfile()
    {
        var profile = Path.GetTempPath();
        Assert.Equal(profile, FolderTargetPolicy.Resolve("shell:HomeFolder", profile));
    }

    [Fact]
    public async Task SettingsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new JsonSettingsStore(path);
            var expected = new AppSettings
            {
                FolderJumpEnabled = false,
                ScreenPasteEnabled = false,
                ScreenCaptureCacheDirectory = Path.Combine(Path.GetTempPath(), "Tbox custom cache"),
                StartWithWindows = true
            };
            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();
            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ConcurrentSettingsSavesRemainAtomic()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new JsonSettingsStore(path);
            var candidates = Enumerable.Range(0, 20)
                .Select(index => new AppSettings
                {
                    FolderJumpEnabled = index % 2 == 0,
                    ScreenPasteEnabled = index % 3 == 0,
                    ScreenCaptureCacheDirectory = $"cache-{index}",
                    StartWithWindows = index % 5 == 0
                })
                .ToArray();

            await Task.WhenAll(candidates.Select(settings => store.SaveAsync(settings)));

            var actual = await store.LoadAsync();
            Assert.Contains(actual, candidates);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void CaptureCachePathUsesConfiguredDirectoryOrStableDefault()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "LocalAppData");
        var custom = Path.Combine(Path.GetTempPath(), "CustomCache");

        Assert.Equal(Path.GetFullPath(custom), CaptureCachePathPolicy.Resolve(custom, localAppData));
        Assert.Equal(
            Path.Combine(Path.GetFullPath(localAppData), "Tbox", "cache", "screenshots"),
            CaptureCachePathPolicy.Resolve(null, localAppData));
    }

    [Fact]
    public void PinWheelScaleUsesTenPercentStepsAndHonorsLimits()
    {
        Assert.Equal(1.1, PinWindowScalePolicy.ApplyWheelDelta(1, 120, 0.25, 4), precision: 10);
        Assert.Equal(1, PinWindowScalePolicy.ApplyWheelDelta(1.1, -120, 0.25, 4), precision: 10);
        Assert.Equal(4, PinWindowScalePolicy.ApplyWheelDelta(4, 120, 0.25, 4));
        Assert.Equal(0.25, PinWindowScalePolicy.ApplyWheelDelta(0.25, -120, 0.25, 4));
    }

    [Fact]
    public void PinResizeKeepsThePixelUnderTheCursorInPlace()
    {
        var resized = PinWindowScalePolicy.ResizeAroundPoint(
            new PinWindowBounds(100, 100, 200, 100),
            400,
            200,
            200,
            150);

        Assert.Equal(new PinWindowBounds(0, 50, 400, 200), resized);
    }
}
