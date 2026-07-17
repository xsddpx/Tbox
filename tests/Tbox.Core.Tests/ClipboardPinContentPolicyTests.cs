using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class ClipboardPinContentPolicyTests
{
    [Fact]
    public void ClipboardPinSourcesPreferBitmapThenImageFileThenText()
    {
        Assert.Equal(
            [ClipboardPinSource.Bitmap, ClipboardPinSource.ImageFile, ClipboardPinSource.Text],
            ClipboardPinContentPolicy.GetPreferredSources(
                hasBitmap: true,
                hasImageFiles: true,
                hasText: true));
        Assert.Equal(
            [ClipboardPinSource.ImageFile, ClipboardPinSource.Text],
            ClipboardPinContentPolicy.GetPreferredSources(
                hasBitmap: false,
                hasImageFiles: true,
                hasText: true));
    }

    [Fact]
    public void ClipboardTextNormalizationPreservesContentAndCanonicalizesLineEndings()
    {
        Assert.Equal(
            "  第一行  \n第二行\n第三行",
            ClipboardPinContentPolicy.NormalizeText("\r\n  第一行  \r\n第二行\r第三行\0\n"));
        Assert.Null(ClipboardPinContentPolicy.NormalizeText("\r\n \t\0\r"));
    }

    [Fact]
    public void ClipboardTextCardSizeClampsMeasuredContentToProductBounds()
    {
        Assert.Equal(
            new ClipboardTextCardSize(280, 112),
            ClipboardPinContentPolicy.FitTextCard(20, 18, 28, 280, 760, 112, 4096));
        Assert.Equal(
            new ClipboardTextCardSize(760, 4096),
            ClipboardPinContentPolicy.FitTextCard(1200.2, 6000.8, 28, 280, 760, 112, 4096));
        Assert.Equal(
            new ClipboardTextCardSize(457, 181),
            ClipboardPinContentPolicy.FitTextCard(400.2, 124.1, 28, 280, 760, 112, 4096));
    }
}
