using Avalonia;
using Avalonia.Controls;

namespace Tbox.Desktop.Views;

// Capture this before closing the source overlay. Keep Avalonia desktop
// coordinates separate from bitmap pixels (notably on Retina displays).
public sealed record OcrWindowSource(PixelRect WorkArea, PixelPoint Center, double DesktopScaling)
{
    internal static OcrWindowSource? FromWindow(Window source, PixelPoint? center = null)
    {
        var screen = source.Screens.ScreenFromWindow(source) ?? source.Screens.Primary;
        if (screen is null || screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0) return null;
        return new(screen.WorkingArea,
            center ?? source.PointToScreen(new Point(source.ClientSize.Width / 2, source.ClientSize.Height / 2)),
            source.DesktopScaling);
    }

    internal (Size ClientSize, PixelPoint Position) Fit(Size requested, Size decoration)
    {
        if (!double.IsFinite(DesktopScaling) || DesktopScaling <= 0 || WorkArea.Width <= 0 || WorkArea.Height <= 0)
            throw new ArgumentException("来源显示器的工作区或缩放无效。");
        var extraWidth = Math.Max(0, decoration.Width);
        var extraHeight = Math.Max(0, decoration.Height);
        var width = Math.Clamp(requested.Width, 1, Math.Max(1, WorkArea.Width / DesktopScaling - extraWidth));
        var height = Math.Clamp(requested.Height, 1, Math.Max(1, WorkArea.Height / DesktopScaling - extraHeight));
        var outerWidth = Math.Min(WorkArea.Width, Math.Ceiling((width + extraWidth) * DesktopScaling));
        var outerHeight = Math.Min(WorkArea.Height, Math.Ceiling((height + extraHeight) * DesktopScaling));
        var x = Math.Clamp(Math.Round(Center.X - outerWidth / 2), WorkArea.X, WorkArea.Right - outerWidth);
        var y = Math.Clamp(Math.Round(Center.Y - outerHeight / 2), WorkArea.Y, WorkArea.Bottom - outerHeight);
        return (new Size(width, height), new PixelPoint((int)x, (int)y));
    }
}
