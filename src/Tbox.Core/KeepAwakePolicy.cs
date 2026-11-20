namespace Tbox.Core;

// What the system is asked to do while keep-awake is on (AWAKE-001–003). Display and lid only apply with the main switch.
public sealed record KeepAwakeRequest(bool System, bool Display, bool Lid)
{
    public static KeepAwakeRequest None { get; } = new(false, false, false);

    public static KeepAwakeRequest From(AppSettings settings) => settings.KeepAwakeEnabled
        ? new(true, settings.KeepAwakeDisplay, settings.KeepAwakeLidClosed)
        : None;

    // Banner text: 已开启 · 屏幕常亮 · 合盖不睡眠.
    public string Summary
    {
        get
        {
            if (!System) return "未开启";
            var parts = new List<string> { "已开启" };
            if (Display) parts.Add("屏幕常亮");
            if (Lid) parts.Add("合盖不睡眠");
            return string.Join(" · ", parts);
        }
    }
}

// The lid-close action of one power plan before Tbox changed it, saved so a crash can be undone on the next start.
public sealed record LidActionBackup(Guid Scheme, uint PluggedIn, uint OnBattery);
