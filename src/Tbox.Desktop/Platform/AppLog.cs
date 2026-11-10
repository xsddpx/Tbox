namespace Tbox.Desktop.Platform;

internal static class AppLog
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tbox",
        "tbox.log");
    private static readonly string PreviousLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tbox",
        "tbox.previous.log");

    internal static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaximumLogBytes)
                {
                    File.Move(LogPath, PreviousLogPath, overwrite: true);
                }
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never break the utility.
        }
    }
}
