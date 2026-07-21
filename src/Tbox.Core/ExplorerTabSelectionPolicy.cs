namespace Tbox.Core;

public readonly record struct ExplorerTabCandidate(
    long TabWindowHandle,
    string? ShellPath,
    string? DisplayName = null);

public static class ExplorerTabSelectionPolicy
{
    public static ExplorerTabCandidate? SelectActive(
        IReadOnlyList<ExplorerTabCandidate> candidates,
        long activeTabWindowHandle,
        string? explorerWindowTitle = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return null;

        var titleMatch = SelectUniqueTitleMatch(candidates, explorerWindowTitle);
        if (titleMatch is not null)
        {
            return titleMatch;
        }

        if (activeTabWindowHandle != 0)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.TabWindowHandle == activeTabWindowHandle)
                {
                    return candidate;
                }
            }

            // A single Shell view is unambiguous even when the platform does
            // not expose a tab-host mapping. With multiple views, preserving
            // the previous folder is safer than selecting a background tab.
            return candidates.Count == 1 ? candidates[0] : null;
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static ExplorerTabCandidate? SelectUniqueTitleMatch(
        IReadOnlyList<ExplorerTabCandidate> candidates,
        string? explorerWindowTitle)
    {
        if (string.IsNullOrWhiteSpace(explorerWindowTitle)) return null;

        var title = explorerWindowTitle.Trim();
        var matches = candidates
            .Where(candidate => TitleStartsWithDisplayName(title, candidate.DisplayName))
            .ToArray();
        if (matches.Length == 0) return null;

        var longestNameLength = matches.Max(candidate => candidate.DisplayName!.Trim().Length);
        var mostSpecificMatches = matches
            .Where(candidate => candidate.DisplayName!.Trim().Length == longestNameLength)
            .ToArray();
        if (mostSpecificMatches.Length == 1) return mostSpecificMatches[0];

        var firstPath = mostSpecificMatches[0].ShellPath;
        return mostSpecificMatches.All(candidate =>
                string.Equals(candidate.ShellPath, firstPath, StringComparison.OrdinalIgnoreCase))
            ? mostSpecificMatches[0]
            : null;
    }

    private static bool TitleStartsWithDisplayName(string title, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;

        var name = displayName.Trim();
        if (!title.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;
        return title.Length == name.Length || char.IsWhiteSpace(title[name.Length]);
    }
}
