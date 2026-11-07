namespace Tbox.Desktop.Models;

public sealed class HotkeyConfiguration(Func<int, bool> register)
{
    public int ActiveMask { get; private set; }
    public bool TryChange(bool capture, bool folders)
    {
        var requested = (capture ? 7 : 0) | (folders ? 8 : 0);
        var previous = ActiveMask;
        if (register(requested)) { ActiveMask = requested; return true; }
        ActiveMask = register(previous) ? previous : 0;
        return false;
    }
}
