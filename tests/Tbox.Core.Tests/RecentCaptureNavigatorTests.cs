using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

// Stepping through recent captures: index 0 is the newest, so "older" moves towards the end of the list.
public sealed class RecentCaptureNavigatorTests
{
    [Theory]
    [InlineData(0, 5, true, 1)]
    [InlineData(3, 5, true, 4)]
    [InlineData(4, 5, true, 4)] // No older capture: stay on the last one.
    [InlineData(4, 5, false, 3)]
    [InlineData(0, 5, false, 0)] // No newer capture: stay on the newest one.
    [InlineData(-1, 5, true, 0)]
    [InlineData(-1, 5, false, 4)]
    public void NextStopsAtBothEnds(int current, int count, bool older, int expected) =>
        Assert.Equal(expected, RecentCaptureNavigator.Next(current, count, older));

    [Theory]
    [InlineData(0, 5, true, 1)]
    [InlineData(4, 5, true, 0)] // Wraps to the oldest capture.
    [InlineData(0, 5, false, 4)] // Wraps to the newest capture.
    [InlineData(2, 3, false, 1)]
    public void WrapCyclesThroughEveryCapture(int current, int count, bool older, int expected) =>
        Assert.Equal(expected, RecentCaptureNavigator.Wrap(current, count, older));

    [Fact]
    public void WrapKeepsASingleCaptureInPlace()
    {
        Assert.Equal(0, RecentCaptureNavigator.Wrap(0, 1, true));
        Assert.Equal(0, RecentCaptureNavigator.Wrap(0, 1, false));
    }

    [Fact]
    public void EmptyHistoryHasNoPosition()
    {
        Assert.Equal(-1, RecentCaptureNavigator.Next(0, 0, true));
        Assert.Equal(-1, RecentCaptureNavigator.Wrap(0, 0, true));
        Assert.False(RecentCaptureNavigator.HasOlder(0, 0));
        Assert.False(RecentCaptureNavigator.HasNewer(0, 0));
        Assert.Equal("", RecentCaptureNavigator.Position(0, 0));
    }

    [Fact]
    public void EndsReportWhichDirectionIsAvailable()
    {
        Assert.True(RecentCaptureNavigator.HasOlder(0, 3));
        Assert.False(RecentCaptureNavigator.HasNewer(0, 3));
        Assert.True(RecentCaptureNavigator.HasNewer(2, 3));
        Assert.False(RecentCaptureNavigator.HasOlder(2, 3));
        Assert.True(RecentCaptureNavigator.HasOlder(1, 3));
        Assert.True(RecentCaptureNavigator.HasNewer(1, 3));
    }

    [Theory]
    [InlineData(0, 8, "1 / 8")]
    [InlineData(7, 8, "8 / 8")]
    [InlineData(2, 1, "")]
    [InlineData(3, 3, "")]
    public void PositionCountsFromOne(int current, int count, string expected) =>
        Assert.Equal(expected, RecentCaptureNavigator.Position(current, count));
}
