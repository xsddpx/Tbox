namespace Tbox.Core;

public readonly record struct ExplorerTabCandidate(long TabWindowHandle, string? ShellPath);

public static class ExplorerTabSelectionPolicy
{
    public static ExplorerTabCandidate? SelectActive(
        IReadOnlyList<ExplorerTabCandidate> candidates,
        long activeTabWindowHandle)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return null;

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

        return candidates[0];
    }
}
