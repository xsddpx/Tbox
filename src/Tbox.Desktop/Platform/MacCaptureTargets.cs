using System.Runtime.InteropServices;
using Tbox.Core;

namespace Tbox.Desktop.Platform;

internal sealed class MacCaptureTargets : ICaptureTargetSource
{
    private readonly NativeTarget[] _targets = MacPlatformServices.ReadJson<TargetList>(NativeTargets()).Targets;
    public bool EnableElementDetection() => NativeEnableElements() != 0;

    public CaptureTargetBounds? FindTarget(CaptureDetectionMode mode, DesktopPoint point)
    {
        var target = _targets.FirstOrDefault(target => target.Bounds.Contains((int)Math.Floor(point.X), (int)Math.Floor(point.Y)));
        if (target is null) return null;
        if (mode == CaptureDetectionMode.InterfaceElement)
        {
            var element = MacPlatformServices.ReadJson<ElementResult>(NativeElement(target.ProcessId, point.X, point.Y));
            if (element.Found && element.Width > 1 && element.Height > 1)
                return new CaptureTargetBounds(element.Left, element.Top, element.Width, element.Height);
        }
        return target.Bounds;
    }

    private sealed record TargetList(NativeTarget[] Targets);
    private sealed record NativeTarget(int ProcessId, int Left, int Top, int Width, int Height)
    { public CaptureTargetBounds Bounds => new(Left, Top, Width, Height); }
    private sealed record ElementResult(bool Found, int Left, int Top, int Width, int Height);
    [DllImport("TboxNative", EntryPoint = "tbox_capture_targets")] private static extern nint NativeTargets();
    [DllImport("TboxNative", EntryPoint = "tbox_enable_elements")] private static extern int NativeEnableElements();
    [DllImport("TboxNative", EntryPoint = "tbox_element_bounds")] private static extern nint NativeElement(int processId, double x, double y);
}
