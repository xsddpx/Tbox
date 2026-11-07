using Tbox.Core;

namespace Tbox.Desktop.Models;

public sealed class RecordingWorkspace : IDisposable
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tbox", "cache", "desktop-recordings");
    public string SessionDirectory { get; }
    public string MasterFilePath => Path.Combine(SessionDirectory, "recording.mp4");
    public RecordingWorkspace(string? root = null)
    {
        SessionDirectory = Path.Combine(root ?? DefaultRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(SessionDirectory);
    }
    public string ExportPath(RecordingExportFormat format) => Path.Combine(SessionDirectory, "export" + RecordingExportProfiles.Get(format).Extension);
    public void ResetMaster() { if (File.Exists(MasterFilePath)) File.Delete(MasterFilePath); }
    public void Dispose()
    {
        try { if (Directory.Exists(SessionDirectory)) Directory.Delete(SessionDirectory, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Retry at next startup after native handles are released. */ }
    }
    public static void CleanupExpired(string? root = null, DateTime? now = null)
    {
        root ??= DefaultRoot;
        if (!Directory.Exists(root)) return;
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (!Guid.TryParseExact(directory.Name, "N", out _) || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            try { if (directory.LastWriteTimeUtc < (now ?? DateTime.UtcNow).AddHours(-24)) directory.Delete(true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
