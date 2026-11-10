namespace Tbox.Core;

public static class FolderJumpList
{
    public const int RecommendationLimit = 3;
    public const int HistoryLimit = 20;
    public const int SearchLimit = 30;

    public static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string[] Recommend(IEnumerable<string> opened, IEnumerable<string> lastUsed)
        => OrderOpened(opened, lastUsed).Take(RecommendationLimit).ToArray();

    public static IEnumerable<string> OrderOpened(IEnumerable<string> opened, IEnumerable<string> lastUsed)
    {
        var paths = Unique(opened).ToArray();
        var available = paths.ToHashSet(PathComparer);
        return Unique(Unique(lastUsed).Where(available.Contains).Concat(paths));
    }

    public static string[] Remember(IEnumerable<string>? history, string path) =>
        Unique((history ?? []).Prepend(path)).Take(HistoryLimit).ToArray();

    public static string[] ToggleFavorite(IEnumerable<string>? favorites, string path)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        var paths = Unique(favorites ?? []).ToArray();
        return paths.Contains(path, PathComparer)
            ? paths.Where(item => !PathComparer.Equals(item, path)).ToArray()
            : paths.Append(path).ToArray();
    }

    public static string[] Search(IEnumerable<string> paths, string query) =>
        Unique(paths).Where(path => path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(SearchLimit).ToArray();

    private static IEnumerable<string> Unique(IEnumerable<string> paths) =>
        paths.Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
            .Select(Path.TrimEndingDirectorySeparator).Distinct(PathComparer);
}
