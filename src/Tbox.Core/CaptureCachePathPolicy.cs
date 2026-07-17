namespace Tbox.Core;

public static class CaptureCachePathPolicy
{
    public static string GetDefaultPath(string localApplicationData) => Path.Combine(
        Path.GetFullPath(localApplicationData),
        "Tbox",
        "cache",
        "screenshots");

    public static string Resolve(string? configuredPath, string localApplicationData)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return GetDefaultPath(localApplicationData);
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configuredPath)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GetDefaultPath(localApplicationData);
        }
    }
}
