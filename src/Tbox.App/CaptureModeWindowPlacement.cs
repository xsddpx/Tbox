using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Tbox.Core;
using Windows.Foundation;
using Windows.Graphics;

namespace Tbox.App;

internal static class CaptureModeWindowPlacement
{
    internal static void PrepareOnTargetDisplay(IntPtr hwnd, RectInt32 region)
    {
        NativeMethods.MoveAndResizeWindow(hwnd, new RectInt32(region.X, region.Y, 1, 1));
    }

    internal static void PositionAdjacent(IntPtr hwnd, FrameworkElement content, RectInt32 region, int gap = 8)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(hwnd));
        var width = WindowDpiScalePolicy.LogicalToPhysicalPixels(Math.Max(160, content.DesiredSize.Width), dpi);
        var height = WindowDpiScalePolicy.LogicalToPhysicalPixels(Math.Max(40, content.DesiredSize.Height), dpi);
        var physicalGap = WindowDpiScalePolicy.LogicalToPhysicalPixels(gap, dpi);
        var center = new PointInt32(region.X + region.Width / 2, region.Y + region.Height / 2);
        var work = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Primary).WorkArea;
        var placed = PinToolbarPlacementPolicy.PlaceAdjacent(
            new PinWindowBounds(region.X, region.Y, region.Width, region.Height),
            new PinWindowBounds(work.X, work.Y, work.Width, work.Height),
            width,
            height,
            physicalGap);
        NativeMethods.MoveAndResizeWindow(hwnd, new RectInt32(placed.X, placed.Y, placed.Width, placed.Height));
    }

    internal static void PositionSidePreview(IntPtr hwnd, FrameworkElement content, RectInt32 region, int gap = 12)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(hwnd));
        var width = WindowDpiScalePolicy.LogicalToPhysicalPixels(Math.Max(180, content.DesiredSize.Width), dpi);
        var height = WindowDpiScalePolicy.LogicalToPhysicalPixels(Math.Max(240, content.DesiredSize.Height), dpi);
        var physicalGap = WindowDpiScalePolicy.LogicalToPhysicalPixels(gap, dpi);
        var center = new PointInt32(region.X + region.Width / 2, region.Y + region.Height / 2);
        var work = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Primary).WorkArea;
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);

        var right = region.X + region.Width + physicalGap;
        var left = region.X - physicalGap - width;
        var x = right + width <= work.X + work.Width
            ? right
            : left >= work.X
                ? left
                : Math.Clamp(right, work.X, work.X + work.Width - width);
        var y = Math.Clamp(region.Y, work.Y, work.Y + work.Height - height);
        NativeMethods.MoveAndResizeWindow(hwnd, new RectInt32(x, y, width, height));
    }
}
