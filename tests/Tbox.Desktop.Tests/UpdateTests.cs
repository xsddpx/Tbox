using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tbox.Desktop.Updates;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class UpdateTests
{
    private static readonly byte[] Package = Encoding.UTF8.GetBytes("signed package fixture");
    private static string Hash => Convert.ToHexString(SHA256.HashData(Package));
    private static object Release(string version, bool draft = false, bool preview = false, string? digest = null, string runtime = "osx-arm64") => new
    {
        tag_name = "v" + version, draft, prerelease = preview, body = "更新说明",
        assets = new[] { new { id = 19, name = runtime == "osx-arm64" ? $"Tbox_{version}_osx-arm64.zip" : $"Tbox_{version}.0_win-x64.msix",
            size = Package.Length, digest = digest ?? "sha256:" + Hash, state = "uploaded" } }
    };
    private static UpdateRelease? Select(params object[] releases)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(releases));
        return UpdateReleasePolicy.Select(json.RootElement, new Version(1, 1, 6, 0), "osx-arm64");
    }

    [Fact]
    public void SelectsHighestStableVersionRegardlessOfReleaseDateOrder()
    {
        var release = Select(Release("1.1.7"), Release("1.2.0"), Release("9.0.0", draft: true), Release("8.0.0", preview: true));
        Assert.Equal(new Version(1, 2, 0, 0), release!.Version);
    }
    [Fact]
    public void NeverDowngradesOrReinstallsCurrentVersion() => Assert.Null(Select(Release("1.1.6"), Release("1.0.45")));
    [Theory]
    [InlineData("sha256:short")]
    [InlineData("md5:00000000000000000000000000000000")]
    public void RefusesAssetsWithoutSha256(string digest) => Assert.Throws<InvalidDataException>(() => Select(Release("1.1.7", digest: digest)));
    [Fact]
    public void IncompleteLatestReleaseDoesNotSilentlyOfferOlderAssets() =>
        Assert.Throws<InvalidDataException>(() => Select(Release("1.1.7"), Release("1.2.0", runtime: "win-x64")));
    [Fact]
    public void ChoosesWindowsMsixWithFourPartVersion()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new[] { Release("1.1.7", runtime: "win-x64") }));
        Assert.Equal("Tbox_1.1.7.0_win-x64.msix", UpdateReleasePolicy.Select(json.RootElement, new Version(1, 1, 6), "win-x64")!.FileName);
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://github.com.evil.example/file")]
    [InlineData("https://example.com/file")]
    [InlineData("https://user@objects.githubusercontent.com/file")]
    [InlineData("https://objects.githubusercontent.com:444/file")]
    public void RejectsUntrustedRedirects(string url) => Assert.False(GitHubUpdateClient.IsAssetLocation(new Uri(url)));

    [Fact]
    public async Task DownloadsPublicBytesWithoutAnyAuthentication()
    {
        var directory = TemporaryDirectory();
        try
        {
            var requests = new List<(string Host, System.Net.Http.Headers.AuthenticationHeaderValue? Token)>();
            using var client = new GitHubUpdateClient(new StubHandler(request =>
            {
                requests.Add((request.RequestUri!.Host, request.Headers.Authorization));
                if (requests.Count == 1)
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/package?download=fixture"); return redirect;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Package) };
            }));
            var file = await client.DownloadAsync(Select(Release("1.1.7"))!, directory, null, CancellationToken.None);
            Assert.Equal(Package, await File.ReadAllBytesAsync(file));
            Assert.Null(requests[0].Token); Assert.Null(requests[1].Token);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CorruptOrTruncatedDownloadNeverBecomesInstallable(bool corrupt)
    {
        var directory = TemporaryDirectory();
        try
        {
            var bytes = Package.ToArray(); if (corrupt) bytes[0] ^= 1; else bytes = bytes[..^1];
            using var client = new GitHubUpdateClient(new StubHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(Select(Release("1.1.7"))!, directory, null, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CancellationCleansPartialDownloadAndPreservesExistingFile()
    {
        var directory = TemporaryDirectory();
        try
        {
            var existing = Path.Combine(directory, "retained.zip"); await File.WriteAllTextAsync(existing, "retained");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            using var client = new GitHubUpdateClient(new StubHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Package) }));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(Select(Release("1.1.7"))!, directory, null, cancelled.Token));
            Assert.Equal("retained", await File.ReadAllTextAsync(existing)); Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task UnavailablePublicRepositoryIsNotReportedAsLatestVersion()
    {
        using var client = new GitHubUpdateClient(new StubHandler(_ => new(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CheckAsync(new Version(1, 1, 6), "osx-arm64", CancellationToken.None));
    }
    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "tbox-update-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
}
