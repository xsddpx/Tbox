using Tbox.Core;

namespace Tbox.Desktop.Models;

public sealed class LongCaptureBuffer
{
    private readonly ScrollCaptureStitcher _stitcher;
    private byte[]? _previous;
    public int Width => _stitcher.Width;
    public int Height => _stitcher.Height;
    public int ViewportTop => _stitcher.ViewportTop;
    public int FrameHeight { get; }
    public bool HasFrame => _stitcher.HasFrame;

    public LongCaptureBuffer(int width, int height)
    { FrameHeight = height; _stitcher = new ScrollCaptureStitcher(width, height); }

    public ScrollCaptureFrameResult? Add(byte[] pixels)
    {
        if (pixels.LongLength != (long)Width * FrameHeight * 4) throw new ArgumentException("显示器或捕获尺寸已变化，请完成当前长截图后重新框选。");
        var previous = _previous; _previous = pixels;
        if (!_stitcher.HasFrame) return _stitcher.AddFrame(pixels);
        if (previous is null || !ScrollCaptureStitcher.AreFramesVisuallyStable(previous, pixels)) return null;
        return _stitcher.AddFrame(pixels);
    }

    public byte[] Complete() => _stitcher.Complete();
}
