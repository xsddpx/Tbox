using Tbox.Core;
using Tbox.Desktop.Models;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class HotkeyConfigurationTests
{
    private static readonly Shortcut F6 = new(false, false, false, false, "F6"), F9 = new(false, false, false, false, "F9");
    private static readonly Shortcut CtrlG = new(true, false, false, false, "G");

    [Fact]
    public void AConflictMarksOnlyThatActionAndTheOthersStillRegister()
    {
        var taken = new HashSet<Shortcut> { CtrlG };
        var configuration = new HotkeyConfiguration(binding => !taken.Contains(binding.Shortcut), _ => { });
        configuration.Apply([new(HotkeyAction.Capture, F6), new(HotkeyAction.Folder, CtrlG)]);
        Assert.True(configuration.IsRegistered(HotkeyAction.Capture));
        Assert.Equal([HotkeyAction.Folder], configuration.Conflicts);
    }

    [Fact]
    public void ApplyReleasesActionsNoLongerWantedAndKeepsUnchangedOnes()
    {
        var registered = new List<HotkeyAction>(); var released = new List<HotkeyAction>();
        var configuration = new HotkeyConfiguration(binding => { registered.Add(binding.Action); return true; }, released.Add);
        configuration.Apply([new(HotkeyAction.Capture, F6), new(HotkeyAction.Folder, CtrlG)]);
        configuration.Apply([new(HotkeyAction.Folder, CtrlG)]);
        Assert.Equal([HotkeyAction.Capture, HotkeyAction.Folder], registered);
        Assert.Equal([HotkeyAction.Capture], released);
        Assert.False(configuration.IsRegistered(HotkeyAction.Capture));
    }

    [Fact]
    public void FailedChangeRestoresThePreviousShortcut()
    {
        var calls = new List<Shortcut>();
        var configuration = new HotkeyConfiguration(binding => { calls.Add(binding.Shortcut); return binding.Shortcut != F9; }, _ => { });
        configuration.Apply([new(HotkeyAction.Capture, F6)]);
        Assert.False(configuration.TryChange(HotkeyAction.Capture, F9, F6));
        Assert.Equal([F6, F9, F6], calls);
        Assert.True(configuration.IsRegistered(HotkeyAction.Capture));
        Assert.Empty(configuration.Conflicts);
    }

    [Fact]
    public void ReleasingForRecordingFreesTheShortcutUntilApplied()
    {
        var released = new List<HotkeyAction>();
        var configuration = new HotkeyConfiguration(_ => true, released.Add);
        configuration.Apply([new(HotkeyAction.Pin, F6)]);
        configuration.Release(HotkeyAction.Pin);
        Assert.Equal([HotkeyAction.Pin], released);
        configuration.Apply([new(HotkeyAction.Pin, F6)]);
        Assert.True(configuration.IsRegistered(HotkeyAction.Pin));
    }
}
