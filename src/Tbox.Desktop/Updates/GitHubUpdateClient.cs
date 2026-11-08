using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tbox.Desktop.Updates;

internal sealed class GitHubUpdateClient : IDisposable
{
    internal const string Repository = "xsddpx/Tbox";
    private readonly HttpClient _client;
    public GitHubUpdateClient(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Tbox-Updater/1.0");
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    public async Task<UpdateRelease?> CheckAsync(Version installed, string runtime, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = ApiRequest($"https://api.github.com/repos/{Repository}/releases?per_page=100");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        RequireSuccess(response);
        var bytes = await ReadBoundedAsync(response.Content, 4 * 1024 * 1024, timeout.Token);
        using var json = JsonDocument.Parse(bytes);
        return UpdateReleasePolicy.Select(json.RootElement, installed, runtime);
    }

    public async Task<string> DownloadAsync(UpdateRelease release, string directory, IProgress<double>? progress, CancellationToken token)
    {
        if (release.AssetId <= 0 || release.Size <= 0 || release.Size > UpdateReleasePolicy.MaximumPackageBytes ||
            release.Sha256.Length != 64 || release.Sha256.Any(c => !Uri.IsHexDigit(c)) || release.FileName.IndexOfAny(['/', '\\']) >= 0 ||
            Path.GetFileName(release.FileName) != release.FileName)
            throw new InvalidDataException("无效的更新包信息。");
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        var completed = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-" + release.FileName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            using var request = ApiRequest($"https://api.github.com/repos/{Repository}/releases/assets/{release.AssetId}", binary: true);
            using var initial = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            HttpResponseMessage? redirected = null;
            try
            {
                var response = initial;
                if (initial.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.MovedPermanently)
                {
                    var location = initial.Headers.Location;
                    if (!IsAssetLocation(location)) throw new InvalidDataException("更新下载地址无效，已停止下载。");
                    // Only GitHub's HTTPS release asset hosts are allowed.
                    using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, location);
                    redirected = await _client.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    response = redirected;
                }
                RequireSuccess(response);
                if (response.Content.Headers.ContentLength is { } declared && declared != release.Size)
                    throw new InvalidDataException("下载大小与发布记录不一致。");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long size = 0;
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    var buffer = new byte[81920]; int count;
                    while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
                    {
                        size = checked(size + count);
                        if (size > release.Size) throw new InvalidDataException("下载内容超出发布大小。");
                        hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                        progress?.Report((double)size / release.Size);
                    }
                    await output.FlushAsync(timeout.Token);
                }
                if (size != release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
                    throw new InvalidDataException("更新包校验失败，请重新下载。");
                File.Move(partial, completed); return completed;
            }
            finally { redirected?.Dispose(); }
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    internal static bool IsAssetLocation(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true } &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    private static HttpRequestMessage ApiRequest(string url, bool binary = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" : "application/vnd.github+json"));
        return request;
    }

    internal static void RequireSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound) throw new InvalidOperationException("公开下载服务尚未就绪，请稍后重试。");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("检查更新过于频繁或 GitHub 暂时限制访问，请稍后重试。");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("无法连接更新服务，请稍后重试。", null, response.StatusCode);
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("更新服务响应过大。");
        await using var input = await content.ReadAsStreamAsync(token); using var output = new MemoryStream();
        var buffer = new byte[16384]; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("更新服务响应过大。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    public void Dispose() => _client.Dispose();
}
