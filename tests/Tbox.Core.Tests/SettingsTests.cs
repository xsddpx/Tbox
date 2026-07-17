using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void DefaultHotkeyIsCtrlG()
    {
        var settings = new AppSettings();
        Assert.Equal("Ctrl + G", settings.FolderJumpHotkey.DisplayText);
    }

    [Fact]
    public void InvalidShellLocationFallsBackToUserProfile()
    {
        var profile = Path.GetTempPath();
        Assert.Equal(profile, FolderTargetPolicy.Resolve("shell:HomeFolder", profile));
    }

    [Fact]
    public async Task SettingsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new JsonSettingsStore(path);
            var expected = new AppSettings { FolderJumpEnabled = false, StartWithWindows = true };
            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();
            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
