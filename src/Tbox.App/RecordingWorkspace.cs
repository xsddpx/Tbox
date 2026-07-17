using Tbox.Core;

namespace Tbox.App;

internal sealed class RecordingWorkspace : IDisposable
{
    private bool _disposed;

    internal RecordingWorkspace()
    {
        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tbox",
            "cache",
            "recordings");
        Directory.CreateDirectory(RootDirectory);
        SessionDirectory = Path.Combine(RootDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(SessionDirectory);
        MasterFilePath = Path.Combine(SessionDirectory, "recording.mp4");
    }

    internal string RootDirectory { get; }
    internal string SessionDirectory { get; }
    internal string MasterFilePath { get; }

    internal string GetExportPath(RecordingExportFormat format) => Path.Combine(
        SessionDirectory,
        "export" + RecordingExportProfiles.Get(format).Extension);

    internal void ResetMasterFile()
    {
        if (File.Exists(MasterFilePath)) File.Delete(MasterFilePath);
    }

    internal static void CleanupExpired()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tbox",
            "cache",
            "recordings");
        if (!Directory.Exists(root)) return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            try
            {
                if (directory.LastWriteTimeUtc < cutoff) directory.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"Cleanup stale recording workspace failed: {exception}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootDirectory));
            var fullSession = Path.TrimEndingDirectorySeparator(Path.GetFullPath(SessionDirectory));
            if (fullSession.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(fullSession))
            {
                Directory.Delete(fullSession, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Delete recording workspace failed: {exception}");
        }
        _disposed = true;
    }
}
