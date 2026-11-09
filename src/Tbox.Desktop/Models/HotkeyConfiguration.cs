using Tbox.Core;

namespace Tbox.Desktop.Models;

// Which global shortcuts are wanted and which ones actually registered. Each action is registered on its own,
// so a shortcut another application holds is reported for that action while the others keep working.
public sealed class HotkeyConfiguration(Func<HotkeyBinding, bool> register, Action<HotkeyAction> unregister)
{
    private readonly Dictionary<HotkeyAction, Shortcut> _registered = [];
    private readonly HashSet<HotkeyAction> _conflicts = [];

    // False until the first registration attempt, so nothing reports a conflict before startup has run.
    public bool Applied { get; private set; }
    public IReadOnlyCollection<HotkeyAction> Conflicts => _conflicts;
    public bool IsRegistered(HotkeyAction action) => _registered.ContainsKey(action);

    // Registers every wanted shortcut and releases the rest. Unchanged registrations are left alone.
    public void Apply(IEnumerable<HotkeyBinding> wanted)
    {
        Applied = true;
        var targets = wanted.ToDictionary(binding => binding.Action, binding => binding.Shortcut);
        foreach (var action in ShortcutPolicy.Actions.Where(action => !targets.ContainsKey(action))) Release(action);
        foreach (var (action, shortcut) in targets)
        {
            if (_registered.TryGetValue(action, out var current) && current == shortcut) continue;
            Release(action);
            if (register(new HotkeyBinding(action, shortcut))) _registered[action] = shortcut;
            else _conflicts.Add(action);
        }
    }

    // Moves one action to a new shortcut. On failure the previous shortcut is restored and false is returned.
    public bool TryChange(HotkeyAction action, Shortcut shortcut, Shortcut? previous)
    {
        Applied = true;
        if (_registered.TryGetValue(action, out var current) && current == shortcut) return true;
        Release(action);
        if (register(new HotkeyBinding(action, shortcut))) { _registered[action] = shortcut; return true; }
        if (previous is not null)
        {
            if (register(new HotkeyBinding(action, previous))) _registered[action] = previous; else _conflicts.Add(action);
        }
        return false;
    }

    // Releases one shortcut, e.g. while the main window records a new combination for it.
    public void Release(HotkeyAction action)
    {
        _conflicts.Remove(action);
        if (_registered.Remove(action)) unregister(action);
    }
}
