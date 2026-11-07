using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tbox.Desktop.Updates;

internal sealed record UpdateRelease(Version Version, long AssetId, string FileName, long Size, string Sha256, string Notes);

internal static partial class UpdateReleasePolicy
{
    public const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;

    public static UpdateRelease? Select(JsonElement releases, Version installed, string runtime)
    {
        if (runtime is not ("osx-arm64" or "win-x64")) throw new NotSupportedException("当前平台尚无可用更新包。");
        if (releases.ValueKind != JsonValueKind.Array) throw new InvalidDataException("更新服务返回了无效的版本列表。");
        var candidates = new List<(Version Version, JsonElement Release)>();
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!StableVersion().IsMatch(tag) || !Version.TryParse(tag.TrimStart('v'), out var parsed)) continue;
            var version = new Version(parsed.Major, parsed.Minor, parsed.Build, 0);
            if (version > Normalize(installed)) candidates.Add((version, release));
        }
        if (candidates.Count == 0) return null;
        var latest = candidates.MaxBy(c => c.Version);
        var shortVersion = latest.Version.ToString(3);
        var expectedName = runtime == "osx-arm64" ? $"Tbox_{shortVersion}_osx-arm64.zip" : $"Tbox_{shortVersion}.0_win-x64.msix";
        var assets = latest.Release.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == expectedName).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("发现新版本，但当前平台的安装包尚未就绪。请稍后检查。");
        var item = assets[0];
        var digest = item.TryGetProperty("digest", out var value) ? value.GetString() : null;
        var size = item.GetProperty("size").GetInt64(); var id = item.GetProperty("id").GetInt64();
        if (id <= 0 || size <= 0 || size > MaximumPackageBytes || digest is null || !Digest().IsMatch(digest) ||
            item.GetProperty("state").GetString() != "uploaded")
            throw new InvalidDataException("新版本安装包缺少有效的完整性信息，已停止更新。");
        var notes = latest.Release.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
        return new(latest.Version, id, expectedName, size, digest[7..].ToLowerInvariant(), notes.Length > 8000 ? notes[..8000] : notes);
    }

    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    [GeneratedRegex("^v?[0-9]+\\.[0-9]+\\.[0-9]+(?:\\.0)?$", RegexOptions.CultureInvariant)] private static partial Regex StableVersion();
    [GeneratedRegex("^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex Digest();
}
