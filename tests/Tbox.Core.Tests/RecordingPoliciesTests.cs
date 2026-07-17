using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class RecordingPoliciesTests
{
    [Fact]
    public void VideoRegionIsTrimmedToEvenDimensionsWithoutMovingOrigin()
    {
        var normalized = RecordingRegionPolicy.NormalizeForVideo(new PixelRegion(11, 17, 101, 203));

        Assert.Equal(new PixelRegion(11, 17, 100, 202), normalized);
    }

    [Fact]
    public void Mp4IsTheAudioCapableDefaultProfile()
    {
        var profile = RecordingExportProfiles.Get(RecordingExportFormat.Mp4);

        Assert.Equal(".mp4", profile.Extension);
        Assert.Equal(10, profile.FramesPerSecond);
        Assert.True(profile.SupportsAudio);
        Assert.False(RecordingExportProfiles.Get(RecordingExportFormat.Gif).SupportsAudio);
        Assert.False(RecordingExportProfiles.Get(RecordingExportFormat.WebP).SupportsAudio);
    }

    [Fact]
    public void RecordingFrameRateChoicesAreFixedAndDefaultToTen()
    {
        Assert.Equal([5, 10, 15, 25, 60], RecordingFrameRatePolicy.AllowedFrameRates);
        Assert.Equal(10, RecordingFrameRatePolicy.DefaultFrameRate);
        Assert.Equal(60, RecordingFrameRatePolicy.Validate(60));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecordingFrameRatePolicy.Validate(24));
    }

    [Theory]
    [InlineData(320, 240, 2_000_000)]
    [InlineData(1920, 1080, 2_488_320)]
    [InlineData(7680, 4320, 20_000_000)]
    public void VideoBitrateIsResolutionAwareAndClamped(int width, int height, int expected)
    {
        Assert.Equal(expected, RecordingRegionPolicy.CalculateVideoBitrate(width, height));
    }
}
