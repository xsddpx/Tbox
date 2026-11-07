using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public async Task PermissionProbeUsesExistingServerAndCanBeRepeated()
    {
        var pipeName = "tbox-test-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), pipeName + ".lock");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        using var server = new SingleInstance(file, pipeName);
        var calls = 0;
        var listening = server.ListenAsync(command =>
        {
            Assert.Equal(3, command); calls++;
            return Task.FromResult<string?>("{\"screenPermission\":true}");
        });
        try
        {
            Assert.Equal("{\"screenPermission\":true}", await SingleInstance.RequestPermissionProbeAsync(pipeName));
            Assert.Equal("{\"screenPermission\":true}", await SingleInstance.RequestPermissionProbeAsync(pipeName));
            Assert.Equal(2, calls);
        }
        finally { server.Dispose(); await listening.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
