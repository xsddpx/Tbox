using Tbox.Core.Accounts;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class CloudPreferencesTests
{
    [Fact]
    public void AppliesCloudPreferencesWithoutChangingLocalPathsOrShortcuts()
    {
        var settings = new AppSettings { ScreenCaptureCacheDirectory = "/local/history", FavoriteFolders = ["/local/project"] };
        var changed = CloudPreferences.Apply(settings, new CloudPreference(CloudProtocol.TranslationLanguage, "ja", 1));
        Assert.Equal("ja", changed.TranslationLanguage);
        Assert.Equal(settings.ScreenCaptureCacheDirectory, changed.ScreenCaptureCacheDirectory);
        Assert.Equal(settings.FavoriteFolders, changed.FavoriteFolders);
        Assert.False(changed.Cloud.SyncCaptures);
        Assert.Equal(3, CloudPreferences.Snapshot(changed).Count);
    }

    [Fact]
    public void ConflictingOfflineEditUsesNewerServerVersion()
    {
        var initial = CloudPreferences.Apply(new AppSettings(), new CloudPreference(CloudProtocol.TranslationLanguage, "en", 1));
        var edited = CloudPreferences.Track(initial, initial with { TranslationLanguage = "ja" });
        var request = Assert.Single(edited.Cloud.Pending).Value;
        Assert.Equal(1, request.Revision);
        Assert.Equal("ja", request.Value);
        var server = new CloudPreference(CloudProtocol.TranslationLanguage, "ko", 2);
        Assert.Same(edited, CloudPreferences.Apply(edited, server));
        var reconciled = CloudPreferences.Apply(edited, server, request, conflict: true);
        Assert.Equal("ko", reconciled.TranslationLanguage);
        Assert.Empty(reconciled.Cloud.Pending);
    }

    [Fact]
    public void FinishingAnUploadDoesNotOverwriteANewerLocalEdit()
    {
        var initial = CloudPreferences.Apply(new AppSettings(), new CloudPreference(CloudProtocol.TranslationLanguage, "en", 1));
        var first = CloudPreferences.Track(initial, initial with { TranslationLanguage = "ja" });
        var newer = CloudPreferences.Track(first, first with { TranslationLanguage = "ko" });
        var reconciled = CloudPreferences.Apply(newer,
            new CloudPreference(CloudProtocol.TranslationLanguage, "ja", 2), first.Cloud.Pending[CloudProtocol.TranslationLanguage]);
        Assert.Equal("ko", reconciled.TranslationLanguage);
        Assert.Equal(2, Assert.Single(reconciled.Cloud.Pending).Value.Revision);
    }
}
