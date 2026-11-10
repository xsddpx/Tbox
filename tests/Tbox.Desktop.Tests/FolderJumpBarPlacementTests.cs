using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class FolderJumpBarPlacementTests
{
    [Fact]
    public void SitsUnderTheDialogWhenThereIsRoomAtTheCurrentScale()
    {
        var target = new FolderDialogTarget(1, new(-1200, 100, 840, 500), new(-1600, 0, 1600, 1000), 1.25);
        var placement = Assert.IsType<DesktopPlacement>(FolderJumpBarSession.Placement(target, 200));
        Assert.Equal(607.5, placement.Top);
        Assert.Equal(250, placement.Height);
        Assert.Equal(-780, placement.Left + placement.Width / 2);
    }

    [Fact]
    public void SitsAboveTheDialogWhenTheBottomIsTooCloseToTheScreenEdge()
    {
        var target = new FolderDialogTarget(1, new(200, 220, 840, 560), new(0, 0, 1200, 800), 1);
        var placement = Assert.IsType<DesktopPlacement>(FolderJumpBarSession.Placement(target, 180));
        Assert.Equal(214, placement.Top + placement.Height);
    }

    [Fact]
    public void LeavesTheNativeDialogUncoveredWhenNeitherSideCanFitTheBar()
    {
        var target = new FolderDialogTarget(1, new(0, 40, 840, 730), new(0, 0, 1200, 800), 1);
        Assert.Null(FolderJumpBarSession.Placement(target, 200));
    }
}
