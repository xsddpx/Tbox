namespace Tbox.Core;

public readonly record struct PanelPlacement(double Left, double Top);

// Places the tray panel next to the tray or menu bar icon. Values share one desktop unit system.
public static class PanelPlacementPolicy
{
    public static PanelPlacement Place(double width, double height, double anchorX, double anchorY,
        double workLeft, double workTop, double workWidth, double workHeight, double gap)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (workWidth <= 0 || workHeight <= 0) throw new ArgumentOutOfRangeException(nameof(workWidth));
        var workRight = workLeft + workWidth;
        var workBottom = workTop + workHeight;
        var left = Math.Clamp(anchorX - width / 2, workLeft + gap, Math.Max(workLeft + gap, workRight - width - gap));
        // An anchor in the lower half (Windows taskbar) opens upward; otherwise (macOS menu bar) downward.
        var top = anchorY > workTop + workHeight / 2 ? anchorY - height - gap : anchorY + gap;
        top = Math.Clamp(top, workTop + gap, Math.Max(workTop + gap, workBottom - height - gap));
        return new PanelPlacement(Math.Max(workLeft, left), Math.Max(workTop, top));
    }
}

public static class RecentCaptureLabel
{
    public static string Format(DateTime captured, DateTime now) =>
        captured.Date == now.Date ? captured.ToString("HH:mm")
        : captured.Date == now.Date.AddDays(-1) ? "昨天"
        : captured.ToString("M/d");
}
