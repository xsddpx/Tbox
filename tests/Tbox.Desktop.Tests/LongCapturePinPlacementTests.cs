using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class LongCapturePinPlacementTests
{
    [Theory]
    [InlineData(600, 300)] // Only the first frame was captured.
    [InlineData(1000, 500)] // The stitched image fits at the original screen width.
    public void PinUsesTheFullImageHeightWhenItFits(int imageHeight, double displayedHeight)
    {
        var selection = new DesktopPlacement(120, 80, 600, 300);
        var workArea = new DesktopPlacement(0, 24, 1400, 800);

        var pin = LongCapturePinPlacement.Fit(selection, workArea, 1200, imageHeight);

        Assert.Equal(new DesktopPlacement(120, 80, 600, displayedHeight), pin);
    }

    [Fact]
    public void TallPinScalesBothAxesToFitTheCaptureDisplay()
    {
        var selection = new DesktopPlacement(-1180, 130, 600, 300);
        var workArea = new DesktopPlacement(-1440, 32, 1440, 800);

        var pin = LongCapturePinPlacement.Fit(selection, workArea, 1200, 4800);

        Assert.Equal(new DesktopPlacement(-1180, 32, 200, 800), pin);
    }

    [Fact]
    public void PinMovesAwayFromScreenEdgesWithoutShrinkingAnImageThatFits()
    {
        var selection = new DesktopPlacement(890, 520, 300, 180);
        var workArea = new DesktopPlacement(0, 24, 1120, 676);

        var pin = LongCapturePinPlacement.Fit(selection, workArea, 600, 800);

        Assert.Equal(new DesktopPlacement(820, 300, 300, 400), pin);
    }
}
