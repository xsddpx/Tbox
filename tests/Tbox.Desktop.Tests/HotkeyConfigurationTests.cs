using Tbox.Desktop.Models;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class HotkeyConfigurationTests
{
    [Fact]
    public void ConflictingNewShortcutRestoresPreviousModuleRegistration()
    {
        var calls = new List<int>();
        var settings = new HotkeyConfiguration(mask => { calls.Add(mask); return mask != 15; });
        Assert.True(settings.TryChange(true, false));
        Assert.False(settings.TryChange(true, true));
        Assert.Equal(7, settings.ActiveMask);
        Assert.Equal(new[] { 7, 15, 7 }, calls);
    }

    [Fact]
    public void DisablingCapturePreservesOnlyFolderShortcut()
    {
        var settings = new HotkeyConfiguration(_ => true);
        settings.TryChange(true, true);
        Assert.True(settings.TryChange(false, true)); Assert.Equal(8, settings.ActiveMask);
        Assert.True(settings.TryChange(false, false)); Assert.Equal(0, settings.ActiveMask);
    }

    [Fact]
    public void FailedRestoreDoesNotReportShortcutsAsRegistered()
    {
        var available = true;
        var settings = new HotkeyConfiguration(_ => available);
        settings.TryChange(true, false); available = false;
        Assert.False(settings.TryChange(true, true)); Assert.Equal(0, settings.ActiveMask);
    }
}
