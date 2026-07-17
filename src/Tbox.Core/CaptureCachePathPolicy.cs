namespace Tbox.Core;

public static class CaptureCachePathPolicy
{
    public static string GetDefaultPath(string picturesDirectory) => Path.Combine(
        Path.GetFullPath(picturesDirectory),
        "screenshots");

    public static string GetFallbackPath(string localApplicationData) => Path.Combine(
        Path.GetFullPath(localApplicationData),
        "Tbox",
        "cache",
        "screenshots");

    public static string Resolve(
        string? configuredPath,
        string picturesDirectory,
        string localApplicationData)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            try
            {
                return Normalize(configuredPath);
            }
            catch (Exception exception) when (IsInvalidPathException(exception))
            {
                // Fall through to the user Pictures directory.
            }
        }

        if (!string.IsNullOrWhiteSpace(picturesDirectory))
        {
            try
            {
                return GetDefaultPath(picturesDirectory);
            }
            catch (Exception exception) when (IsInvalidPathException(exception))
            {
                // Some redirected profiles can temporarily return an invalid path.
            }
        }

        return GetFallbackPath(localApplicationData);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        Environment.ExpandEnvironmentVariables(path)));

    private static bool IsInvalidPathException(Exception exception) =>
        exception is ArgumentException or NotSupportedException or PathTooLongException;
}
