using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class CaptureHistoryRetentionTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void KeepsAtMostFiftyRecords()
    {
        var records = Enumerable.Range(0, 60).Select(i => (Now.AddMinutes(-i), 1L)).ToArray();
        Assert.Equal(CaptureHistoryRetention.MaximumCount, CaptureHistoryRetention.CountToKeep(records, Now));
    }

    [Fact]
    public void StopsAtTheFirstRecordOlderThanSevenDays()
    {
        (DateTime, long)[] records = [(Now, 1), (Now.AddDays(-6.9), 1), (Now.AddDays(-7.1), 1), (Now.AddDays(-8), 1)];
        Assert.Equal(2, CaptureHistoryRetention.CountToKeep(records, Now));
    }

    [Fact]
    public void KeepsTheTotalWithinOneGibibyte()
    {
        const long part = 400L * 1024 * 1024;
        (DateTime, long)[] records = [(Now, part), (Now.AddMinutes(-1), part), (Now.AddMinutes(-2), part)];
        Assert.Equal(2, CaptureHistoryRetention.CountToKeep(records, Now));
    }
}
