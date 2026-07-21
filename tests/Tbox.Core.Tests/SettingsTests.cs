using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void DefaultHotkeyIsCtrlG()
    {
        var settings = new AppSettings();
        Assert.Equal("Ctrl + G", settings.FolderJumpHotkey.DisplayText);
        Assert.True(settings.ScreenPasteEnabled);
        Assert.Equal("system", settings.AppTheme);
    }

    [Fact]
    public void CaptureSelectionMapsFromDisplayUnitsToPixels()
    {
        var region = CaptureRegionPolicy.MapToPixels(100, 50, 10, 5, 20, 10, 200, 100);
        Assert.Equal(new PixelRegion(20, 10, 40, 20), region);
    }

    [Fact]
    public void CaptureSelectionMapsMixedDpiSurfaceWithoutExtraScaling()
    {
        var region = CaptureRegionPolicy.MapToPixels(2560, 1440, 0, 0, 2560, 1440, 3840, 2160);
        Assert.Equal(new PixelRegion(0, 0, 3840, 2160), region);
    }

    [Fact]
    public void CaptureSelectionHitTestingRecognizesFourSidesAndFourCorners()
    {
        var bounds = new CaptureSelectionBounds(100, 80, 400, 300);

        Assert.Equal(CaptureSelectionHandle.TopLeft,
            CaptureSelectionAdjustmentPolicy.HitTest(bounds, 100, 80, 7));
        Assert.Equal(CaptureSelectionHandle.Top,
            CaptureSelectionAdjustmentPolicy.HitTest(bounds, 300, 80, 7));
        Assert.Equal(CaptureSelectionHandle.TopRight,
            CaptureSelectionAdjustmentPolicy.HitTest(bounds, 500, 80, 7));
        Assert.Equal(CaptureSelectionHandle.Right,
            CaptureSelectionAdjustmentPolicy.HitTest(bounds, 500, 230, 7));
        Assert.Equal(CaptureSelectionHandle.BottomRight,
            CaptureSelectionAdjustmentPolicy.HitTest(bounds, 500, 380, 7));
        Assert.Equal(CaptureSelectionHandle.Bottom,
            CaptureSelectionAdjustmentPolicy.HitTest(bounds, 300, 380, 7));
        Assert.Equal(CaptureSelectionHandle.BottomLeft,
            CaptureSelectionAdjustmentPolicy.HitTest(bounds, 100, 380, 7));
        Assert.Equal(CaptureSelectionHandle.Left,
            CaptureSelectionAdjustmentPolicy.HitTest(bounds, 100, 230, 7));
        Assert.Equal(CaptureSelectionHandle.None,
            CaptureSelectionAdjustmentPolicy.HitTest(bounds, 300, 230, 7));
    }

    [Fact]
    public void CaptureSelectionResizeKeepsOppositeEdgesFixedAndClampsToSurface()
    {
        var bounds = new CaptureSelectionBounds(100, 80, 400, 300);

        Assert.Equal(
            new CaptureSelectionBounds(250, 80, 250, 300),
            CaptureSelectionAdjustmentPolicy.Resize(
                bounds,
                CaptureSelectionHandle.Left,
                250,
                0,
                800,
                600,
                8,
                8));
        Assert.Equal(
            new CaptureSelectionBounds(100, 80, 700, 520),
            CaptureSelectionAdjustmentPolicy.Resize(
                bounds,
                CaptureSelectionHandle.BottomRight,
                900,
                700,
                800,
                600,
                8,
                8));
        Assert.Equal(
            new CaptureSelectionBounds(0, 0, 500, 380),
            CaptureSelectionAdjustmentPolicy.Resize(
                bounds,
                CaptureSelectionHandle.TopLeft,
                -20,
                -30,
                800,
                600,
                8,
                8));
    }

    [Fact]
    public void CaptureSelectionResizeEnforcesMinimumSizeWithoutCrossingOppositeEdge()
    {
        var bounds = new CaptureSelectionBounds(100, 80, 400, 300);

        Assert.Equal(
            new CaptureSelectionBounds(100, 80, 8, 300),
            CaptureSelectionAdjustmentPolicy.Resize(
                bounds,
                CaptureSelectionHandle.Right,
                20,
                0,
                800,
                600,
                8,
                8));
        Assert.Equal(
            new CaptureSelectionBounds(100, 372, 400, 8),
            CaptureSelectionAdjustmentPolicy.Resize(
                bounds,
                CaptureSelectionHandle.Top,
                0,
                500,
                800,
                600,
                8,
                8));
    }

    [Fact]
    public void CaptureCanActivateAnyDisplayIncludingNegativeCoordinates()
    {
        CaptureDisplayBounds[] displays =
        [
            new(-1920, 0, 1920, 1080),
            new(0, 0, 2560, 1440),
            new(2560, -400, 1600, 900)
        ];

        Assert.Equal(0, CaptureDisplayPolicy.FindContainingDisplayIndex(displays, -100, 500));
        Assert.Equal(1, CaptureDisplayPolicy.FindContainingDisplayIndex(displays, 100, 500));
        Assert.Equal(2, CaptureDisplayPolicy.FindContainingDisplayIndex(displays, 3000, -100));
    }

    [Fact]
    public void CaptureDisplayBoundsUseExclusiveRightAndBottomEdges()
    {
        var display = new CaptureDisplayBounds(100, 200, 800, 600);

        Assert.True(display.Contains(100, 200));
        Assert.True(display.Contains(899, 799));
        Assert.False(display.Contains(900, 799));
        Assert.False(display.Contains(899, 800));
    }

    [Fact]
    public void CaptureDetectionToggleAlternatesWindowAndInterfaceElement()
    {
        Assert.Equal(
            CaptureDetectionMode.InterfaceElement,
            CaptureTargetPolicy.Toggle(CaptureDetectionMode.Window));
        Assert.Equal(
            CaptureDetectionMode.Window,
            CaptureTargetPolicy.Toggle(CaptureDetectionMode.InterfaceElement));
    }

    [Fact]
    public void CaptureDetectionUsesTheFirstTopmostTargetContainingThePointer()
    {
        CaptureTargetBounds[] targets =
        [
            new(100, 100, 500, 400),
            new(0, 0, 1920, 1080)
        ];

        Assert.Equal(targets[0], CaptureTargetPolicy.FindTopmostContaining(targets, 200, 200));
        Assert.Equal(targets[1], CaptureTargetPolicy.FindTopmostContaining(targets, 50, 50));
        Assert.Null(CaptureTargetPolicy.FindTopmostContaining(targets, 1920, 1080));
    }

    [Fact]
    public void CaptureTargetMappingClipsDesktopBoundsToNegativeCoordinateDisplay()
    {
        var display = new CaptureDisplayBounds(-1920, 0, 1920, 1080);
        var target = new CaptureTargetBounds(-2000, 100, 500, 400);

        Assert.Equal(
            new PixelRegion(0, 100, 420, 400),
            CaptureTargetPolicy.MapClippedToDisplay(display, target));
        Assert.Null(CaptureTargetPolicy.MapClippedToDisplay(
            display,
            new CaptureTargetBounds(0, 0, 400, 300)));
    }

    [Fact]
    public void CaptureSessionTransfersTheOnlyActiveSelectionBetweenDisplays()
    {
        var session = new CaptureSelectionSession();

        Assert.Null(session.BeginSelection(0, 2));
        Assert.Equal(0, session.ActiveDisplayIndex);
        Assert.Equal(0, session.BeginSelection(1, 2));
        Assert.Equal(1, session.ActiveDisplayIndex);
        Assert.Equal(1, session.BeginSelection(1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.BeginSelection(2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.BeginSelection(0, 0));
    }

    [Theory]
    [InlineData(false, false, CapturePointerCancellationAction.PreserveSelection)]
    [InlineData(false, true, CapturePointerCancellationAction.CancelRegionSelection)]
    [InlineData(true, false, CapturePointerCancellationAction.CancelAnnotationGesture)]
    [InlineData(true, true, CapturePointerCancellationAction.CancelAnnotationGesture)]
    public void PointerCancellationOnlyClearsAnActiveGesture(
        bool annotationGestureInProgress,
        bool regionSelectionInProgress,
        CapturePointerCancellationAction expected)
    {
        Assert.Equal(
            expected,
            CaptureInteractionPolicy.ResolvePointerCancellation(
                annotationGestureInProgress,
                regionSelectionInProgress));
    }

    [Fact]
    public void CropCopiesOnlySelectedBgraPixels()
    {
        var source = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var cropped = CaptureRegionPolicy.CropBgra32(source, 2, 2, new PixelRegion(1, 0, 1, 2));
        Assert.Equal(new byte[] { 4, 5, 6, 7, 12, 13, 14, 15 }, cropped);
    }

    [Fact]
    public void InvalidShellLocationFallsBackToUserProfile()
    {
        var profile = Path.GetTempPath();
        Assert.Equal(profile, FolderTargetPolicy.Resolve("shell:HomeFolder", profile));
    }

    [Fact]
    public void ExplorerTabSelectionUsesTheActiveViewWhenTabsShareAWindow()
    {
        ExplorerTabCandidate[] candidates =
        [
            new(0, @"C:\Users\tester\Desktop", "桌面"),
            new(0, @"C:\Users\tester\Documents", "文档")
        ];

        var selected = ExplorerTabSelectionPolicy.SelectActive(
            candidates,
            activeTabWindowHandle: 0,
            "文档 和 1 个其他选项卡 - 文件资源管理器");

        Assert.Equal(@"C:\Users\tester\Documents", selected?.ShellPath);
    }

    [Fact]
    public void ExplorerTabSelectionDoesNotGuessAmongUnmappedBackgroundTabs()
    {
        ExplorerTabCandidate[] candidates =
        [
            new(0, @"C:\Users\tester\First\Reports", "Reports"),
            new(0, @"C:\Users\tester\Second\Reports", "Reports")
        ];

        Assert.Null(ExplorerTabSelectionPolicy.SelectActive(
            candidates,
            activeTabWindowHandle: 0,
            "Reports and 1 more tab - File Explorer"));
        Assert.Null(ExplorerTabSelectionPolicy.SelectActive(
            candidates,
            activeTabWindowHandle: 0));
        Assert.Equal(
            candidates[0],
            ExplorerTabSelectionPolicy.SelectActive([candidates[0]], 202));
    }

    [Fact]
    public void ExplorerTabSelectionUsesTheMostSpecificWindowTitleMatch()
    {
        ExplorerTabCandidate[] candidates =
        [
            new(0, @"C:\Work\Design", "Design"),
            new(0, @"C:\Work\Design Assets", "Design Assets")
        ];

        var selected = ExplorerTabSelectionPolicy.SelectActive(
            candidates,
            activeTabWindowHandle: 0,
            "Design Assets and 1 more tab - File Explorer");

        Assert.Equal(@"C:\Work\Design Assets", selected?.ShellPath);
    }

    [Fact]
    public async Task SettingsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new JsonSettingsStore(path);
            var expected = new AppSettings
            {
                FolderJumpEnabled = false,
                ScreenPasteEnabled = false,
                ScreenCaptureCacheDirectory = Path.Combine(Path.GetTempPath(), "Tbox custom cache"),
                StartWithWindows = true,
                AppTheme = "dark"
            };
            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();
            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ConcurrentSettingsSavesRemainAtomic()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new JsonSettingsStore(path);
            var candidates = Enumerable.Range(0, 20)
                .Select(index => new AppSettings
                {
                    FolderJumpEnabled = index % 2 == 0,
                    ScreenPasteEnabled = index % 3 == 0,
                    ScreenCaptureCacheDirectory = $"cache-{index}",
                    StartWithWindows = index % 5 == 0
                })
                .ToArray();

            await Task.WhenAll(candidates.Select(settings => store.SaveAsync(settings)));

            var actual = await store.LoadAsync();
            Assert.Contains(actual, candidates);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void CaptureCachePathUsesConfiguredDirectoryOrStableDefault()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "LocalAppData");
        var pictures = Path.Combine(Path.GetTempPath(), "Pictures");
        var custom = Path.Combine(Path.GetTempPath(), "CustomCache");

        Assert.Equal(
            Path.GetFullPath(custom),
            CaptureCachePathPolicy.Resolve(custom, pictures, localAppData));
        Assert.Equal(
            Path.Combine(Path.GetFullPath(pictures), "screenshots"),
            CaptureCachePathPolicy.Resolve(null, pictures, localAppData));
        Assert.Equal(
            Path.Combine(Path.GetFullPath(localAppData), "Tbox", "cache", "screenshots"),
            CaptureCachePathPolicy.Resolve(null, string.Empty, localAppData));
    }

    [Fact]
    public void PinWheelScaleUsesTenPercentStepsAndHonorsLimits()
    {
        Assert.Equal(1.1, PinWindowScalePolicy.ApplyWheelDelta(1, 120, 0.25, 4), precision: 10);
        Assert.Equal(1, PinWindowScalePolicy.ApplyWheelDelta(1.1, -120, 0.25, 4), precision: 10);
        Assert.Equal(4, PinWindowScalePolicy.ApplyWheelDelta(4, 120, 0.25, 4));
        Assert.Equal(0.25, PinWindowScalePolicy.ApplyWheelDelta(0.25, -120, 0.25, 4));
    }

    [Fact]
    public void PinResizeKeepsThePixelUnderTheCursorInPlace()
    {
        var resized = PinWindowScalePolicy.ResizeAroundPoint(
            new PinWindowBounds(100, 100, 200, 100),
            400,
            200,
            200,
            150);

        Assert.Equal(new PinWindowBounds(0, 50, 400, 200), resized);
    }

    [Fact]
    public void PinToolbarIsPlacedOutsideTheImageAndFlipsAboveNearTheWorkAreaBottom()
    {
        var workArea = new PinWindowBounds(0, 0, 1920, 1040);
        var below = PinToolbarPlacementPolicy.PlaceAdjacent(
            new PinWindowBounds(200, 100, 800, 500),
            workArea,
            520,
            44);
        var above = PinToolbarPlacementPolicy.PlaceAdjacent(
            new PinWindowBounds(1500, 800, 500, 220),
            workArea,
            520,
            44);

        Assert.Equal(new PinWindowBounds(480, 608, 520, 44), below);
        Assert.Equal(new PinWindowBounds(1400, 748, 520, 44), above);
        Assert.True(below.Y >= 100 + 500);
        Assert.True(above.Y + above.Height <= 800);
    }

    [Theory]
    [InlineData(96u, 620)]
    [InlineData(120u, 775)]
    [InlineData(144u, 930)]
    public void PinToolbarLogicalWidthIsConvertedForDisplayDpi(uint dpi, int expectedPhysicalWidth)
    {
        Assert.Equal(
            expectedPhysicalWidth,
            WindowDpiScalePolicy.LogicalToPhysicalPixels(620, dpi));
    }
}
