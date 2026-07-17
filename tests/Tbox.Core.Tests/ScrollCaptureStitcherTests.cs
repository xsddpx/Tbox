using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class ScrollCaptureStitcherTests
{
    [Fact]
    public void AppendsOnlyRowsRevealedByDownwardScroll()
    {
        const int width = 48;
        const int frameHeight = 80;
        var document = CreateDocument(width, 180);
        var first = CropRows(document, width, 0, frameHeight);
        var second = CropRows(document, width, 35, frameHeight);
        var stitcher = new ScrollCaptureStitcher(width, frameHeight);

        Assert.Equal(ScrollCaptureFrameDecision.Started, stitcher.AddFrame(first).Decision);
        var result = stitcher.AddFrame(second);

        Assert.Equal(ScrollCaptureFrameDecision.Appended, result.Decision);
        Assert.Equal(35, result.AddedRows);
        Assert.Equal(115, result.TotalHeight);
        Assert.Equal(ScrollCaptureDirection.Down, result.Direction);
        Assert.Equal(35, result.ViewportTop);
        Assert.Equal(CropRows(document, width, 0, 115), stitcher.Complete());
    }

    [Fact]
    public void PrependsRowsRevealedByUpwardScroll()
    {
        const int width = 48;
        const int frameHeight = 80;
        var document = CreateDocument(width, 180);
        var first = CropRows(document, width, 60, frameHeight);
        var second = CropRows(document, width, 30, frameHeight);
        var stitcher = new ScrollCaptureStitcher(width, frameHeight);

        stitcher.AddFrame(first);
        var result = stitcher.AddFrame(second);

        Assert.Equal(ScrollCaptureFrameDecision.Appended, result.Decision);
        Assert.Equal(30, result.AddedRows);
        Assert.Equal(110, result.TotalHeight);
        Assert.Equal(ScrollCaptureDirection.Up, result.Direction);
        Assert.Equal(0, result.ViewportTop);
        Assert.Equal(CropRows(document, width, 30, 110), stitcher.Complete());
    }

    [Fact]
    public void ReversingDirectionMovesInsideCapturedContentWithoutGrowingOutput()
    {
        const int width = 48;
        const int frameHeight = 80;
        var document = CreateDocument(width, 220);
        var stitcher = new ScrollCaptureStitcher(width, frameHeight);
        stitcher.AddFrame(CropRows(document, width, 50, frameHeight));
        stitcher.AddFrame(CropRows(document, width, 85, frameHeight));

        var result = stitcher.AddFrame(CropRows(document, width, 65, frameHeight));

        Assert.Equal(ScrollCaptureFrameDecision.Moved, result.Decision);
        Assert.Equal(0, result.AddedRows);
        Assert.Equal(115, result.TotalHeight);
        Assert.Equal(ScrollCaptureDirection.Up, result.Direction);
        Assert.Equal(15, result.ViewportTop);
        Assert.Equal(CropRows(document, width, 50, 115), stitcher.Complete());
    }

    [Fact]
    public void ExtendsBothEndsAfterChangingScrollDirection()
    {
        const int width = 48;
        const int frameHeight = 80;
        var document = CreateDocument(width, 240);
        var stitcher = new ScrollCaptureStitcher(width, frameHeight);
        stitcher.AddFrame(CropRows(document, width, 80, frameHeight));
        stitcher.AddFrame(CropRows(document, width, 110, frameHeight));

        var result = stitcher.AddFrame(CropRows(document, width, 60, frameHeight));

        Assert.Equal(ScrollCaptureFrameDecision.Appended, result.Decision);
        Assert.Equal(20, result.AddedRows);
        Assert.Equal(130, result.TotalHeight);
        Assert.Equal(ScrollCaptureDirection.Up, result.Direction);
        Assert.Equal(0, result.ViewportTop);
        Assert.Equal(CropRows(document, width, 60, 130), stitcher.Complete());
    }

    [Fact]
    public void IdenticalFrameDoesNotGrowOutput()
    {
        var frame = CreateDocument(40, 60);
        var stitcher = new ScrollCaptureStitcher(40, 60);
        stitcher.AddFrame(frame);

        var result = stitcher.AddFrame(frame);

        Assert.Equal(ScrollCaptureFrameDecision.Duplicate, result.Decision);
        Assert.Equal(60, stitcher.Height);
    }

    [Fact]
    public void UnrelatedFrameIsRejectedWithoutChangingLastAcceptedFrame()
    {
        const int width = 36;
        const int height = 64;
        var first = CreateDocument(width, height);
        var unrelated = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        for (var index = 3; index < unrelated.Length; index += 4) unrelated[index] = 255;
        var stitcher = new ScrollCaptureStitcher(width, height);
        stitcher.AddFrame(first);

        var result = stitcher.AddFrame(unrelated);

        Assert.Equal(ScrollCaptureFrameDecision.NoMatch, result.Decision);
        Assert.Equal(height, stitcher.Height);
        Assert.Equal(first, stitcher.Complete());
    }

    [Fact]
    public void PixelLimitStopsAtDeterministicHeight()
    {
        const int width = 32;
        const int frameHeight = 50;
        var document = CreateDocument(width, 150);
        var stitcher = new ScrollCaptureStitcher(width, frameHeight, maximumPixels: width * 70, maximumHeight: 500);
        stitcher.AddFrame(CropRows(document, width, 0, frameHeight));

        var result = stitcher.AddFrame(CropRows(document, width, 20, frameHeight));

        Assert.Equal(ScrollCaptureFrameDecision.LimitReached, result.Decision);
        Assert.Equal(20, result.AddedRows);
        Assert.Equal(70, stitcher.Height);
        Assert.Equal(CropRows(document, width, 0, 70), stitcher.Complete());
    }

    [Fact]
    public void SparseLowContrastPageIsNotMistakenForAnUnchangedFrame()
    {
        const int width = 80;
        const int frameHeight = 100;
        const int shift = 7;
        var document = CreateSparseDocument(width, 180);
        var first = CropRows(document, width, 0, frameHeight);
        var second = CropRows(document, width, shift, frameHeight);
        var stitcher = new ScrollCaptureStitcher(width, frameHeight);
        stitcher.AddFrame(first);

        Assert.True(ScrollCaptureStitcher.CalculateFrameError(first, second) < 2.5);
        var result = stitcher.AddFrame(second);

        Assert.Equal(ScrollCaptureFrameDecision.Appended, result.Decision);
        Assert.Equal(shift, result.AddedRows);
        Assert.Equal(CropRows(document, width, 0, frameHeight + shift), stitcher.Complete());
    }

    [Fact]
    public void FixedHeaderDoesNotPreventDownwardContentMatching()
    {
        const int width = 72;
        const int frameHeight = 96;
        const int shift = 24;
        var document = CreateDocument(width, 180);
        var first = CropRows(document, width, 0, frameHeight);
        var second = CropRows(document, width, shift, frameHeight);
        PaintFixedHeader(first, width, 10);
        PaintFixedHeader(second, width, 10);
        var stitcher = new ScrollCaptureStitcher(width, frameHeight);
        stitcher.AddFrame(first);

        var result = stitcher.AddFrame(second);

        Assert.Equal(ScrollCaptureFrameDecision.Appended, result.Decision);
        Assert.Equal(shift, result.AddedRows);
        var expected = new byte[width * (frameHeight + shift) * 4];
        Buffer.BlockCopy(first, 0, expected, 0, first.Length);
        Buffer.BlockCopy(
            second,
            (frameHeight - shift) * width * 4,
            expected,
            first.Length,
            shift * width * 4);
        Assert.Equal(expected, stitcher.Complete());
    }

    [Fact]
    public void SmallDynamicIndicatorDoesNotPreventStableFrameDetection()
    {
        const int width = 100;
        const int height = 100;
        var first = CreateSolidFrame(width, height, 240);
        var smallIndicatorChange = (byte[])first.Clone();
        PaintBlock(smallIndicatorChange, width, 20, 20, 20, 20, 224);
        var broadMovement = (byte[])first.Clone();
        PaintBlock(broadMovement, width, 20, 20, 25, 25, 224);

        Assert.True(ScrollCaptureStitcher.AreFramesVisuallyStable(first, smallIndicatorChange));
        Assert.False(ScrollCaptureStitcher.AreFramesVisuallyStable(first, broadMovement));
    }

    private static byte[] CreateDocument(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            pixels[index] = (byte)((x * 17 + y * 13) % 251);
            pixels[index + 1] = (byte)((x * 7 + y * 19) % 253);
            pixels[index + 2] = (byte)((x * 23 + y * 29) % 247);
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static byte[] CreateSparseDocument(int width, int height)
    {
        var pixels = Enumerable.Repeat((byte)250, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        for (var y = 4; y < height; y += 20)
        {
            var lineWidth = 12 + y % Math.Max(13, width - 12);
            for (var x = 3; x < Math.Min(width - 3, lineWidth); x++)
            {
                var index = (y * width + x) * 4;
                pixels[index] = 230;
                pixels[index + 1] = 230;
                pixels[index + 2] = 230;
            }
        }
        return pixels;
    }

    private static void PaintFixedHeader(byte[] frame, int width, int rows)
    {
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            frame[index] = 35;
            frame[index + 1] = 45;
            frame[index + 2] = 55;
            frame[index + 3] = 255;
        }
    }

    private static byte[] CreateSolidFrame(int width, int height, byte value)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = value;
            pixels[index + 1] = value;
            pixels[index + 2] = value;
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static void PaintBlock(
        byte[] frame,
        int width,
        int left,
        int top,
        int blockWidth,
        int blockHeight,
        byte value)
    {
        for (var y = top; y < top + blockHeight; y++)
        for (var x = left; x < left + blockWidth; x++)
        {
            var index = (y * width + x) * 4;
            frame[index] = value;
            frame[index + 1] = value;
            frame[index + 2] = value;
        }
    }

    private static byte[] CropRows(byte[] document, int width, int startRow, int rowCount)
    {
        var output = new byte[width * rowCount * 4];
        Buffer.BlockCopy(document, startRow * width * 4, output, 0, output.Length);
        return output;
    }
}
