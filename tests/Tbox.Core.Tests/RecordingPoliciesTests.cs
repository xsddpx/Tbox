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
    public void ProfilesCarryEachFormatsDefaults()
    {
        var mp4 = RecordingExportProfiles.Get(RecordingExportFormat.Mp4);
        Assert.Equal(".mp4", mp4.Extension);
        Assert.Equal(30, mp4.DefaultFramesPerSecond);
        Assert.True(mp4.SupportsAudio);
        Assert.Equal((10, 1280), (RecordingExportProfiles.Get(RecordingExportFormat.Gif).DefaultFramesPerSecond, RecordingExportProfiles.Get(RecordingExportFormat.Gif).DefaultMaximumLongEdge!.Value));
        Assert.Equal(15, RecordingExportProfiles.Get(RecordingExportFormat.WebP).DefaultFramesPerSecond);
        Assert.Equal(".jpg", RecordingExportProfiles.Get(RecordingExportFormat.ContactSheet).Extension);
        Assert.False(RecordingExportProfiles.Get(RecordingExportFormat.Gif).SupportsAudio);
        Assert.False(RecordingExportProfiles.Get(RecordingExportFormat.WebP).SupportsAudio);
        Assert.Equal([1, 5, 10, 15, 25, 30], RecordingExportProfiles.FrameRates);
    }

    [Fact]
    public void MastersAreAlwaysRecordedAtThirtyFramesPerSecond()
    {
        Assert.Equal(30, RecordingFrameRatePolicy.Validate(30));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecordingFrameRatePolicy.Validate(60));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecordingFrameRatePolicy.Validate(10));
    }

    [Fact]
    public void SizeChoicesNeverEnlargeAndVideoEdgesStayEven()
    {
        var source = new VideoSize(2560, 1440);
        Assert.Equal([new(2560, 1440), new(1920, 1080), new(1280, 720), new(640, 360)], RecordingSizePolicy.Choices(source, true).Select(c => c.Size));
        Assert.Equal(new VideoSize(2560, 1440), RecordingSizePolicy.ScaleToWidth(source, 9999, true));
        Assert.Equal(new VideoSize(332, 186), RecordingSizePolicy.ScaleToWidth(source, 333, true));
        Assert.Equal(new VideoSize(333, 187), RecordingSizePolicy.ScaleToWidth(source, 333, false));
        // GIF defaults to the largest preset whose long edge fits 1280, close to the old fixed GIF limit.
        Assert.Equal(2, RecordingSizePolicy.DefaultChoice(source, RecordingExportFormat.Gif, false));
        Assert.Equal(0, RecordingSizePolicy.DefaultChoice(new VideoSize(1280, 720), RecordingExportFormat.Gif, false));
        Assert.Equal(0, RecordingSizePolicy.DefaultChoice(source, RecordingExportFormat.Mp4, true));
    }

    [Fact]
    public void TimeRangeIsClampedAndKeepsHalfASecond()
    {
        var duration = TimeSpan.FromSeconds(10);
        var range = RecordingTimeRange.Normalize(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(12), duration);
        Assert.True(range.IsFull(duration));
        var tight = RecordingTimeRange.Normalize(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4.1), duration);
        Assert.Equal(TimeSpan.FromSeconds(0.5), tight.Length);
        var end = RecordingTimeRange.Normalize(TimeSpan.FromSeconds(9.9), duration, duration);
        Assert.Equal((TimeSpan.FromSeconds(9.5), duration), (end.Start, end.End));
        var shortClip = TimeSpan.FromMilliseconds(300);
        Assert.Equal(shortClip, RecordingTimeRange.Normalize(TimeSpan.Zero, shortClip, shortClip).Length);
    }

    [Fact]
    public void OnlyAnUnchangedMp4CopiesTheMaster()
    {
        var size = new VideoSize(1280, 720); var duration = TimeSpan.FromSeconds(42); var full = new RecordingTimeRange(TimeSpan.Zero, duration);
        Assert.True(new RecordingExportOptions(RecordingExportFormat.Mp4, 30, size, full).CopiesMaster(size, duration));
        Assert.False(new RecordingExportOptions(RecordingExportFormat.Mp4, 25, size, full).CopiesMaster(size, duration));
        Assert.False(new RecordingExportOptions(RecordingExportFormat.Mp4, 30, new(640, 360), full).CopiesMaster(size, duration));
        Assert.False(new RecordingExportOptions(RecordingExportFormat.Mp4, 30, size, new(TimeSpan.FromSeconds(1), duration)).CopiesMaster(size, duration));
        Assert.False(new RecordingExportOptions(RecordingExportFormat.Gif, 30, size, full).CopiesMaster(size, duration));
    }

    [Fact]
    public void ContactSheetSamplesMatchTheAfterEffectsScript()
    {
        // AE: first = ceil(start/dt), last = ceil(end/dt) - 1, frame i = first + round((last - first) * i / 5).
        Assert.Equal([0L, 6, 12, 17, 23, 29], ContactSheetPolicy.SampleFrames(0, 29));
        var times = ContactSheetPolicy.SampleTimes(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(29.8)), 30);
        Assert.Equal(TimeSpan.FromSeconds(5), times[0]);
        Assert.Equal(TimeSpan.FromSeconds(893 / 30.0), times[5]);
        Assert.False(ContactSheetPolicy.HasRepeats(times));
        Assert.True(ContactSheetPolicy.HasRepeats(ContactSheetPolicy.SampleTimes(new(TimeSpan.Zero, TimeSpan.FromSeconds(0.1)), 30)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContactSheetPolicy.SampleFrames(5, 4));
    }

    [Theory]
    [InlineData(2, 2, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(6, 3, 2)]
    [InlineData(8, 4, 2)]
    [InlineData(10, 5, 2)]
    public void ContactSheetUsesOnlyTheOfferedFrameCounts(int count, int columns, int rows)
    {
        Assert.Equal((columns, rows), ContactSheetPolicy.Grid(count));
        Assert.Equal(count, ContactSheetPolicy.Layout(new(200, 100), count).Count);
        Assert.Equal(count, ContactSheetPolicy.SampleFrames(0, 29, count).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => ContactSheetPolicy.Grid(3));
    }

    [Fact]
    public void ContactSheetLayoutMatchesTheAfterEffectsScript()
    {
        // 200 px cells: 648 × 2(h+28)+36 like the AE grid comp (AE floors 112.5 to 112), numbers 01–06 under each cell.
        var cell = RecordingSizePolicy.FitLongEdge(new VideoSize(1920, 1080), 200, false);
        Assert.Equal(new VideoSize(200, 112), cell);
        Assert.Equal(new VideoSize(648, 2 * (112 + 28) + 36), ContactSheetPolicy.SheetSize(cell));
        var layout = ContactSheetPolicy.Layout(cell);
        Assert.Equal(["01", "02", "03", "04", "05", "06"], layout.Select(c => c.Label));
        Assert.Equal(new SheetCell(12, 12, 17, 132, "01"), layout[0]);
        Assert.Equal(new SheetCell(436, 12 + 112 + 40, 441, 12 + 112 + 40 + 112 + 8, "06"), layout[5]);
        Assert.Equal(320, ContactSheetPolicy.DefaultLongEdge);
    }

    [Theory]
    [InlineData(320, 240, 2_000_000)]
    [InlineData(1920, 1080, 2_488_320)]
    [InlineData(7680, 4320, 20_000_000)]
    public void VideoBitrateIsResolutionAwareAndClamped(int width, int height, int expected)
    {
        Assert.Equal(expected, RecordingRegionPolicy.CalculateVideoBitrate(width, height, 10));
    }
}
