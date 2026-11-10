using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Models;

internal static class LongCapturePinPlacement
{
    internal static DesktopPlacement Fit(DesktopPlacement selection, DesktopPlacement workArea, int imageWidth, int imageHeight)
    {
        // The selection defines the preferred screen width, not the aspect ratio of the stitched image.
        var scale = Math.Min(Math.Min(selection.Width, workArea.Width) / imageWidth, workArea.Height / imageHeight);
        var width = Math.Min(workArea.Width, imageWidth * scale);
        var height = Math.Min(workArea.Height, imageHeight * scale);
        var left = Math.Clamp(selection.Left, workArea.Left, workArea.Left + (workArea.Width - width));
        var top = Math.Clamp(selection.Top, workArea.Top, workArea.Top + (workArea.Height - height));
        return new DesktopPlacement(left, top, width, height);
    }
}
