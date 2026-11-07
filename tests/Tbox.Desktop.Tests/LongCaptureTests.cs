using Tbox.Core;
using Tbox.Desktop.Models;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class LongCaptureTests
{
    [Fact]
    public void CapturesFirstFrameImmediatelyAndWaitsForStableScrolling()
    {
        const int width = 80, height = 120, shift = 40;
        var pixels = new byte[width * (height + shift) * 4];
        new Random(408).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var first = pixels[..(width * height * 4)];
        var next = pixels[(width * shift * 4)..];
        var buffer = new LongCaptureBuffer(width, height);
        Assert.Equal(ScrollCaptureFrameDecision.Started, buffer.Add(first)!.Value.Decision);
        Assert.Equal(height, buffer.Height);
        Assert.Null(buffer.Add(next));
        Assert.Equal(height, buffer.Height);
        Assert.Equal(ScrollCaptureFrameDecision.Appended, buffer.Add(next)!.Value.Decision);
        Assert.Equal(height + shift, buffer.Height);
        Assert.Equal(pixels, buffer.Complete());
        Assert.Equal(ScrollCaptureFrameDecision.Duplicate, buffer.Add(next)!.Value.Decision);
        Assert.Equal(height + shift, buffer.Height);
    }

    [Fact]
    public void RejectsChangedFrameDimensionsBeforeDamagingExistingResult()
    {
        var buffer = new LongCaptureBuffer(10, 20);
        var frame = Enumerable.Repeat((byte)255, 10 * 20 * 4).ToArray();
        buffer.Add(frame);
        Assert.Throws<ArgumentException>(() => buffer.Add(new byte[4]));
        Assert.Equal(frame, buffer.Complete());
    }
}
