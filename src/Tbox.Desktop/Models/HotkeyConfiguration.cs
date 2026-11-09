namespace Tbox.Desktop.Models;

public sealed class HotkeyConfiguration(Func<int, bool> register)
{
    public int ActiveMask { get; private set; }
    // False until the first registration attempt, so nothing reports a conflict before startup has run.
    public bool Applied { get; private set; }
    public bool TryChange(bool capture, bool folders)
    {
        var requested = (capture ? 7 : 0) | (folders ? 8 : 0);
        var previous = ActiveMask; Applied = true;
        if (register(requested)) { ActiveMask = requested; return true; }
        ActiveMask = register(previous) ? previous : 0;
        return false;
    }
}
