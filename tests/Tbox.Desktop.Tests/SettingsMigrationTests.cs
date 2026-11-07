using System.Text.Json;
using Tbox.Core;
using Tbox.Desktop.Models;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tbox-migration-" + Guid.NewGuid().ToString("N"));
    private string Legacy => Path.Combine(_directory, "settings.json");
    private string Destination => Path.Combine(_directory, "desktop-settings.json");
    public SettingsMigrationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ImportsAllPreferencesAndKeepsOriginalBytes()
    {
        var original = new AppSettings { FolderJumpEnabled = false, ScreenPasteEnabled = false, AppTheme = "dark",
            ShowFailureNotifications = false, StartWithWindows = true, ScreenCaptureCacheDirectory = "D:\\My pictures\\截图",
            FolderJumpHotkey = new HotkeySettings(Control: true, Shift: true) };
        var json = JsonSerializer.Serialize(original);
        await File.WriteAllTextAsync(Legacy, json);
        Assert.Null(await SettingsMigration.ImportIfMissingAsync(Destination, Legacy));
        Assert.Equal(original, await new JsonSettingsStore(Destination).LoadAsync());
        Assert.Equal(json, await File.ReadAllTextAsync(Legacy));
        Assert.Equal(json, await File.ReadAllTextAsync(Destination));
    }

    [Fact]
    public async Task NewSettingsAlwaysWinAndImportIsIdempotent()
    {
        await File.WriteAllTextAsync(Legacy, "{\"AppTheme\":\"dark\"}");
        var store = new JsonSettingsStore(Destination);
        await store.SaveAsync(new AppSettings { AppTheme = "light" });
        await SettingsMigration.ImportIfMissingAsync(Destination, Legacy);
        Assert.Equal("light", (await store.LoadAsync()).AppTheme);
        Assert.Equal("{\"AppTheme\":\"dark\"}", await File.ReadAllTextAsync(Legacy));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task InvalidLegacyFileIsRetainedAndCanBeRetried(string invalid)
    {
        await File.WriteAllTextAsync(Legacy, invalid);
        Assert.NotNull(await SettingsMigration.ImportIfMissingAsync(Destination, Legacy));
        Assert.False(File.Exists(Destination));
        Assert.Equal(invalid, await File.ReadAllTextAsync(Legacy));
        Assert.Single(Directory.GetFiles(_directory));
        await File.WriteAllTextAsync(Legacy, "{\"ScreenPasteEnabled\":false}");
        Assert.Null(await SettingsMigration.ImportIfMissingAsync(Destination, Legacy));
        Assert.False((await new JsonSettingsStore(Destination).LoadAsync()).ScreenPasteEnabled);
    }

    [Fact]
    public async Task UnwritableDestinationDoesNotDestroyLegacyFile()
    {
        await File.WriteAllTextAsync(Legacy, "{}");
        var obstruction = Path.Combine(_directory, "file");
        await File.WriteAllTextAsync(obstruction, "retain");
        Assert.NotNull(await SettingsMigration.ImportIfMissingAsync(Path.Combine(obstruction, "settings.json"), Legacy));
        Assert.Equal("{}", await File.ReadAllTextAsync(Legacy));
        Assert.Equal("retain", await File.ReadAllTextAsync(obstruction));
    }

    [Fact]
    public async Task MissingLegacyFileDoesNotCreateDefaultSettings()
    {
        Assert.Null(await SettingsMigration.ImportIfMissingAsync(Destination, Legacy));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
