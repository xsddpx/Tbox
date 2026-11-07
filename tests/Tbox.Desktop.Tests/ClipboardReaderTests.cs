using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Tbox.Desktop.Models;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class ClipboardReaderTests
{
    [Fact]
    public async Task DamagedImageFallsBackToFilesBeforeText()
    {
        var textRead = false;
        var result = await ClipboardReader.ReadAsync<string>([
            () => throw new ArgumentException("damaged bitmap"),
            () => Task.FromResult<string?>("file image"),
            () => { textRead = true; return Task.FromResult<string?>("text"); }
        ], CancellationToken.None);
        Assert.Equal("file image", result); Assert.False(textRead);
    }

    [Fact]
    public async Task BusyClipboardIsRetriedAtMostThreeTimes()
    {
        var attempts = 0; var delays = new List<TimeSpan>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClipboardReader.ReadAsync<string>([
            () => { attempts++; throw new COMException("busy", unchecked((int)0x800401D0)); }
        ], CancellationToken.None, (delay, _) => { delays.Add(delay); return Task.CompletedTask; }));
        Assert.Equal(3, attempts); Assert.Equal(2, delays.Count);
        Assert.All(delays, delay => Assert.InRange(delay.TotalMilliseconds, 1, 100));
    }

    [Fact]
    public async Task RetryCanRecoverWithoutReportingFailure()
    {
        var calls = 0;
        var result = await ClipboardReader.ReadAsync<string>([
            () => ++calls < 3 ? throw new COMException("busy", unchecked((int)0x800401D0)) : Task.FromResult<string?>("image")
        ], CancellationToken.None, (_, _) => Task.CompletedTask);
        Assert.Equal("image", result); Assert.Equal(3, calls);
    }

    [AvaloniaFact]
    public void VeryLongTextProducesBoundedCardInsteadOfFailing()
    {
        using var picture = DesktopController.RenderText(string.Concat(Enumerable.Repeat("长文字贴图保留内容并在画布末尾省略。\n", 4000)));
        Assert.InRange(picture.Bitmap.PixelSize.Height, 100, 8192);
        Assert.Equal(1280, picture.Bitmap.PixelSize.Width);
    }
}
