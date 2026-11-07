using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class CaptureHistoryTests
{
    [Fact]
    public void RestoresAbsoluteRegionOnNegativeOriginDisplayWithDifferentScale()
    {
        var history = new CaptureHistory();
        var display = new CapturedDisplay(7, -2560, -100, 2560, 1440, []);
        history.Remember(new DesktopPlacement(-2460, 100, 1000, 600));
        Assert.Same(display, history.FindDisplay([new CapturedDisplay(1, 0, 0, 1920, 1080, []), display]));
        var local = history.MapToLocal(display, 1280, 720);
        Assert.Equal(50, local.Left); Assert.Equal(100, local.Top);
        Assert.Equal(500, local.Width); Assert.Equal(300, local.Height);
    }

    [Fact]
    public void RejectsRegionThatNoLongerFitsOneDisplay()
    {
        var history = new CaptureHistory();
        var displays = new[] { new CapturedDisplay(1, 0, 0, 1000, 800, []), new CapturedDisplay(2, 1000, 0, 1000, 800, []) };
        Assert.Null(history.FindDisplay(displays));
        history.Remember(new DesktopPlacement(900, 50, 300, 300));
        Assert.Null(history.FindDisplay(displays));
        history.Remember(new DesktopPlacement(1000, 0, 1000, 800));
        Assert.Equal(2u, history.FindDisplay(displays)!.Id);
    }

    [Fact]
    public void PointerOnSharedDisplayEdgeBelongsToExactlyOneDisplay()
    {
        var left = new CapturedDisplay(1, -1000, 0, 1000, 800, []);
        var right = new CapturedDisplay(2, 0, 0, 2000, 1600, []);
        Assert.False(CaptureHistory.ContainsPointer(left, new DesktopPoint(0, 400)));
        Assert.True(CaptureHistory.ContainsPointer(right, new DesktopPoint(0, 400)));
        Assert.True(CaptureHistory.ContainsPointer(left, new DesktopPoint(-0.1, 400)));
    }
}
