using System.Text.Json;
using Tbox.Core;

namespace Tbox.Desktop.Platform;

// The current power plan's lid-close action, read and written for both power sources.
internal interface ILidPowerPlan
{
    Guid ActiveScheme();
    (uint PluggedIn, uint OnBattery) ReadLidAction(Guid scheme);
    void WriteLidAction(Guid scheme, uint pluggedIn, uint onBattery);
}

// Sets "close lid: do nothing" and puts back the user's own values. The original values are written to disk
// before anything changes, so a crash or power loss is undone the next time Tbox starts.
internal sealed class LidCloseGuard(ILidPowerPlan plan, string backupPath)
{
    public const uint DoNothing = 0;

    public bool Held => Read() is not null;

    public void Hold()
    {
        var active = plan.ActiveScheme();
        // The user switched power plans while it was held: give the old plan back before taking the new one.
        if (Read() is { } backup && backup.Scheme != active) Release();
        if (Read() is null)
        {
            var (pluggedIn, onBattery) = plan.ReadLidAction(active);
            Save(new LidActionBackup(active, pluggedIn, onBattery));
        }
        plan.WriteLidAction(active, DoNothing, DoNothing);
    }

    public void Release()
    {
        if (Read() is not { } backup) return;
        plan.WriteLidAction(backup.Scheme, backup.PluggedIn, backup.OnBattery);
        File.Delete(backupPath);
    }

    private LidActionBackup? Read()
    {
        try { return File.Exists(backupPath) ? JsonSerializer.Deserialize<LidActionBackup>(File.ReadAllText(backupPath)) : null; }
        catch (JsonException error)
        {
            // Without the saved values there is nothing safe to write back.
            AppLog.Write("Discarding unreadable lid backup: " + error.Message);
            File.Delete(backupPath);
            return null;
        }
    }

    private void Save(LidActionBackup backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
        var temporary = backupPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(backup));
        File.Move(temporary, backupPath, true);
    }
}
