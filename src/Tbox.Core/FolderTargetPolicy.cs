namespace Tbox.Core;

public static class FolderTargetPolicy
{
    public static string Resolve(string? shellPath, string userProfile)
    {
        if (!string.IsNullOrWhiteSpace(shellPath) &&
            (Path.IsPathRooted(shellPath) || shellPath.StartsWith(@"\\", StringComparison.Ordinal)) &&
            Directory.Exists(shellPath))
        {
            return shellPath;
        }

        return userProfile;
    }
}

