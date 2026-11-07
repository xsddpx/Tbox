using Tbox.Core;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Models;

// Runtime-only history is kept in absolute desktop units, never a monitor-relative DIP rectangle.
public sealed class CaptureHistory
{
    public DesktopPlacement? LastRegion { get; private set; }
    public void Remember(DesktopPlacement region)
    {
        if (region.Width > 0 && region.Height > 0) LastRegion = region;
    }

    public CapturedDisplay? FindDisplay(IEnumerable<CapturedDisplay> displays) => LastRegion is { } region
        ? displays.FirstOrDefault(display => region.Left >= display.Left && region.Top >= display.Top &&
            region.Left + region.Width <= display.Left + display.Width && region.Top + region.Height <= display.Top + display.Height)
        : null;

    public CaptureSelectionBounds MapToLocal(CapturedDisplay display, double localWidth, double localHeight)
    {
        var region = LastRegion ?? throw new InvalidOperationException("还没有完成过截图。");
        return new CaptureSelectionBounds((region.Left - display.Left) / display.Width * localWidth,
            (region.Top - display.Top) / display.Height * localHeight,
            region.Width / display.Width * localWidth, region.Height / display.Height * localHeight);
    }

    public static bool ContainsPointer(CapturedDisplay display, DesktopPoint point) =>
        point.X >= display.Left && point.X < display.Left + display.Width &&
        point.Y >= display.Top && point.Y < display.Top + display.Height;
}
