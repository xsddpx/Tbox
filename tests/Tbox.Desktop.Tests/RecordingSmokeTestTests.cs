using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class RecordingSmokeTestTests
{
    [Fact]
    public void AcceptsBothRecordedPhasesWithPausedTimeRemoved() =>
        RecordingSmokeTest.Validate(new(4000, 40, 20, 0, 20));

    [Theory]
    [InlineData(40, 40, 0, 0)] // Frozen on the initial frame.
    [InlineData(40, 0, 0, 40)] // Missing everything before the pause.
    [InlineData(40, 20, 1, 19)] // A paused frame leaked into the output.
    [InlineData(40, 0, 0, 0)] // Black or unrelated desktop.
    [InlineData(10, 5, 0, 5)] // Insufficient continuous decoded frames.
    public void RejectsMissingOrIncorrectCapturedContent(int frames, int red, int green, int blue) =>
        Assert.Throws<InvalidOperationException>(() => RecordingSmokeTest.Validate(new(4000, frames, red, green, blue)));

    [Theory]
    [InlineData(0)]
    [InlineData(2000)]
    [InlineData(6600)] // Wall time including the pause.
    public void RejectsTruncatedOrUntrimmedTimeline(long duration) =>
        Assert.Throws<InvalidOperationException>(() => RecordingSmokeTest.Validate(new(duration, 40, 20, 0, 20)));
}
