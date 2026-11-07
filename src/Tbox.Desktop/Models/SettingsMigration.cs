using System.Text.Json;
using Tbox.Core;

namespace Tbox.Desktop.Models;

internal static class SettingsMigration
{
    // Import once, retaining the original bytes for rollback to the old application.
    // Never replace settings already written by the shared application.
    public static async Task<string?> ImportIfMissingAsync(string destination, string legacy, CancellationToken token = default)
    {
        if (File.Exists(destination) || !File.Exists(legacy)) return null;
        string? temporary = null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(legacy, token);
            if (JsonSerializer.Deserialize<AppSettings>(bytes) is null)
                throw new JsonException("Empty settings");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            temporary = destination + ".import-" + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, token);
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { /* A newer setting won the race. */ }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return "未能导入旧版设置，本次使用新版设置；原设置文件已保留。";
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
