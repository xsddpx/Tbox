using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class OcrPolicyTests
{
    [Theory]
    [InlineData(800, 600, 2600, 800, 600)]
    [InlineData(5200, 2600, 2600, 2600, 1300)]
    [InlineData(1300, 5200, 2600, 650, 2600)]
    public void ImageScalingNeverUpscalesAndPreservesAspectRatio(
        int width,
        int height,
        int maximumDimension,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.Equal(
            new OcrImageSize(expectedWidth, expectedHeight),
            OcrImageScalePolicy.FitWithinMaximumDimension(width, height, maximumDimension));
    }

    [Fact]
    public void LogicalLineNumbersIgnoreVisualWrappingAndIncludeTrailingEmptyLine()
    {
        const string text = "第一段很长但没有逻辑换行\r\nsecond line\n";

        Assert.Equal([0, 14, 26], OcrLineNumberPolicy.GetLogicalLineStarts(text));
    }

    [Fact]
    public void LogicalLineNumbersSupportTextBoxCarriageReturnNormalization()
    {
        const string text = "first\rsecond\rthird";

        Assert.Equal([0, 6, 13], OcrLineNumberPolicy.GetLogicalLineStarts(text));
    }

    [Fact]
    public void InvalidImageDimensionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OcrImageScalePolicy.FitWithinMaximumDimension(0, 100, 2600));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OcrImageScalePolicy.FitWithinMaximumDimension(100, 100, 0));
    }
}
