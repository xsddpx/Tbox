using Tbox.Core;
using Tbox.Desktop.Models;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class CaptureSessionTests
{
    [Fact]
    public void EditingASelectionInvalidatesPendingAutomaticTargetResults()
    {
        var session = new CaptureSessionState();
        session.Select(1, new CaptureSelectionBounds(0, 0, 100, 100), true);
        Assert.True(session.SelectionWasDetected);
        var detectionRevision = session.Revision;
        session.Select(1, new CaptureSelectionBounds(10, 20, 80, 60));
        Assert.False(session.SelectionWasDetected);
        Assert.NotEqual(detectionRevision, session.Revision);
        Assert.True(session.TryComplete());
        Assert.NotEqual(detectionRevision + 1, session.Revision);
    }

    [Fact]
    public void StartingOnAnotherDisplayClearsThePreviousSelection()
    {
        var session = new CaptureSessionState();
        session.Select(1, new CaptureSelectionBounds(10, 20, 30, 40));
        session.Select(2, null);
        Assert.Equal(2u, session.ActiveDisplay);
        Assert.Null(session.Selection);
        Assert.False(session.TryComplete());
    }

    [Fact]
    public void ASessionCanPublishOnlyOneResult()
    {
        var session = new CaptureSessionState();
        session.Select(2, new CaptureSelectionBounds(10, 20, 30, 40));
        Assert.True(session.TryComplete());
        Assert.False(session.TryComplete());
        session.Select(1, null);
        Assert.Equal(2u, session.ActiveDisplay);
    }

    [Fact]
    public void CancelledSessionsCannotBeResurrected()
    {
        var session = new CaptureSessionState();
        session.Select(1, new CaptureSelectionBounds(10, 20, 30, 40));
        session.Cancel();
        session.Select(1, new CaptureSelectionBounds(0, 0, 10, 10));
        Assert.False(session.TryComplete());
        Assert.Null(session.Selection);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void EmptySelectionsCannotComplete(double width, double height)
    {
        var session = new CaptureSessionState();
        session.Select(1, new CaptureSelectionBounds(0, 0, width, height));
        Assert.False(session.TryComplete());
    }

    [Fact]
    public void NewOcrCancelsAndRejectsAnOlderResult()
    {
        using var requests = new LatestOperation();
        var first = requests.Begin();
        var second = requests.Begin();
        Assert.True(first.IsCancellationRequested);
        Assert.False(requests.IsCurrent(first));
        requests.Complete(first);
        Assert.True(requests.IsCurrent(second));
        requests.Dispose();
        Assert.True(second.IsCancellationRequested);
        Assert.False(requests.IsCurrent(second));
        requests.Complete(second);
    }
}
