using Tbox.Desktop.Accounts;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class RemoteEngineTests
{
    [Fact]
    public async Task ReadsTheControlMessageAfterEngineOutput()
    {
        using var stream = new StringReader(
            "Flutter initialization\nTBOX_REMOTE {\"challenge\":\"example\"}\n");
        Assert.Equal("{\"challenge\":\"example\"}", await RemoteEngineProcess.ReadControlAsync(stream, default));
    }

    [Fact]
    public async Task RejectsMissingAndOversizedControlMessages()
    {
        using var missing = new StringReader("Flutter initialization\n");
        await Assert.ThrowsAsync<IOException>(() => RemoteEngineProcess.ReadControlAsync(missing, default));

        using var oversized = new StringReader("TBOX_REMOTE " + new string('a', 4096) + "\n");
        await Assert.ThrowsAsync<IOException>(() => RemoteEngineProcess.ReadControlAsync(oversized, default));
    }
}
