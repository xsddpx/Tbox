using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class PinCropPolicyTests
{
    [Fact]
    public void ReverseDragMapsToSourcePixelsAndPhysicalWindowBounds()
    {
        var created = PinCropPolicy.TryCreate(
            new PixelRegion(100, 50, 400, 300),
            new PinWindowBounds(1000, 500, 800, 600),
            400,
            300,
            300,
            240,
            100,
            60,
            8,
            out var result);

        Assert.True(created);
        Assert.Equal(new PixelRegion(200, 110, 200, 180), result.Region);
        Assert.Equal(new PinWindowBounds(1200, 620, 400, 360), result.WindowBounds);
    }

    [Fact]
    public void NestedCropUsesTheCurrentVisibleRegionAsItsCoordinateSpace()
    {
        var created = PinCropPolicy.TryCreate(
            new PixelRegion(200, 110, 200, 180),
            new PinWindowBounds(1200, 620, 400, 360),
            200,
            180,
            50,
            45,
            150,
            135,
            8,
            out var result);

        Assert.True(created);
        Assert.Equal(new PixelRegion(250, 155, 100, 90), result.Region);
        Assert.Equal(new PinWindowBounds(1300, 710, 200, 180), result.WindowBounds);
    }

    [Fact]
    public void DpiScaledWindowKeepsTheSelectedContentAtItsScreenPosition()
    {
        var created = PinCropPolicy.TryCreate(
            new PixelRegion(0, 0, 1000, 500),
            new PinWindowBounds(100, 50, 500, 250),
            400,
            200,
            80,
            40,
            320,
            160,
            8,
            out var result);

        Assert.True(created);
        Assert.Equal(new PixelRegion(200, 100, 600, 300), result.Region);
        Assert.Equal(new PinWindowBounds(200, 100, 300, 150), result.WindowBounds);
    }

    [Fact]
    public void SelectionIsClampedAndFullCurrentRegionIsNotReportedAsACrop()
    {
        var created = PinCropPolicy.TryCreate(
            new PixelRegion(100, 50, 400, 300),
            new PinWindowBounds(1000, 500, 800, 600),
            400,
            300,
            -50,
            -20,
            450,
            340,
            8,
            out _);

        Assert.False(created);
    }

    [Fact]
    public void SelectionBelowMinimumDisplaySizeIsRejected()
    {
        var created = PinCropPolicy.TryCreate(
            new PixelRegion(0, 0, 1000, 500),
            new PinWindowBounds(0, 0, 1000, 500),
            1000,
            500,
            10,
            10,
            17,
            200,
            8,
            out _);

        Assert.False(created);
    }

    [Fact]
    public void RestoreExpandsAroundTheMovedAndScaledVisibleRegion()
    {
        var restored = PinCropPolicy.Restore(
            new PixelRegion(200, 100, 600, 300),
            new PinWindowBounds(700, 400, 900, 450),
            1000,
            500);

        Assert.Equal(new PixelRegion(0, 0, 1000, 500), restored.Region);
        Assert.Equal(new PinWindowBounds(400, 250, 1500, 750), restored.WindowBounds);
        Assert.True(PinCropPolicy.IsFullImage(restored.Region, 1000, 500));
    }
}
