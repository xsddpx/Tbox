namespace Tbox.Core.Accounts;

public sealed record LocalCloudPreferences
{
    public string? Origin { get; init; }
    public bool SyncPreferences { get; init; } = true;
    public bool SyncCaptures { get; init; }
    public Dictionary<string, long> Revisions { get; init; } = [];
    public Dictionary<string, PreferenceEdit> Pending { get; init; } = [];

    public bool Equals(LocalCloudPreferences? other) => other is not null &&
        Origin == other.Origin && SyncPreferences == other.SyncPreferences && SyncCaptures == other.SyncCaptures &&
        SameValues(Revisions, other.Revisions) && SameValues(Pending, other.Pending);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Origin);
        hash.Add(SyncPreferences);
        hash.Add(SyncCaptures);
        foreach (var entry in Revisions.OrderBy(entry => entry.Key, StringComparer.Ordinal)) hash.Add(entry);
        foreach (var entry in Pending.OrderBy(entry => entry.Key, StringComparer.Ordinal)) hash.Add(entry);
        return hash.ToHashCode();
    }

    private static bool SameValues<T>(Dictionary<string, T> left, Dictionary<string, T> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) &&
            EqualityComparer<T>.Default.Equals(pair.Value, value));
}

public static class CloudPreferences
{
    public static Dictionary<string, string> Snapshot(AppSettings settings) => new()
    {
        [CloudProtocol.Theme] = CloudProtocol.IsPreference(CloudProtocol.Theme, settings.AppTheme) ? settings.AppTheme : "system",
        [CloudProtocol.FailureNotifications] = settings.ShowFailureNotifications ? "true" : "false",
        [CloudProtocol.TranslationLanguage] =
            CloudProtocol.IsPreference(CloudProtocol.TranslationLanguage, settings.TranslationLanguage) ? settings.TranslationLanguage : "zh-Hans"
    };

    public static AppSettings Track(AppSettings old, AppSettings next)
    {
        if (next.Cloud is not { SyncPreferences: true } cloud || cloud.Revisions.Count == 0) return next;
        var before = Snapshot(old);
        var after = Snapshot(next);
        var pending = new Dictionary<string, PreferenceEdit>(cloud.Pending);
        foreach (var (key, value) in after)
            if (before[key] != value && cloud.Revisions.TryGetValue(key, out var revision))
                pending[key] = new PreferenceEdit(key, value, revision);
        return next with { Cloud = cloud with { Pending = pending } };
    }

    public static AppSettings Apply(AppSettings current, CloudPreference preference, PreferenceEdit? completed = null, bool conflict = false)
    {
        var cloud = current.Cloud;
        if (!cloud.SyncPreferences || !CloudProtocol.IsPreference(preference.Key, preference.Value) || preference.Revision < 1)
            return current;
        if (cloud.Revisions.TryGetValue(preference.Key, out var revision) && revision > preference.Revision) return current;
        if (!cloud.Pending.ContainsKey(preference.Key) && revision == preference.Revision &&
            Snapshot(current)[preference.Key] == preference.Value) return current;
        var revisions = new Dictionary<string, long>(cloud.Revisions) { [preference.Key] = preference.Revision };
        var pending = new Dictionary<string, PreferenceEdit>(cloud.Pending);
        if (pending.TryGetValue(preference.Key, out var local))
        {
            if (completed is null) return current;
            if (local != completed)
            {
                if (!conflict) pending[preference.Key] = local with { Revision = preference.Revision };
                return current with { Cloud = cloud with { Revisions = revisions, Pending = pending } };
            }
            pending.Remove(preference.Key);
        }

        current = preference.Key switch
        {
            CloudProtocol.Theme => current with { AppTheme = preference.Value },
            CloudProtocol.FailureNotifications => current with { ShowFailureNotifications = preference.Value == "true" },
            CloudProtocol.TranslationLanguage => current with { TranslationLanguage = preference.Value },
            _ => current
        };
        return current with { Cloud = cloud with { Revisions = revisions, Pending = pending } };
    }
}
