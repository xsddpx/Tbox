using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Tbox.Core.Accounts;

namespace Tbox.Desktop.Accounts;

internal sealed record LocalAccount(string Origin, Guid DeviceId, string PrivateKey, string? RefreshToken = null,
    string? Login = null, bool SignOutPending = false)
{
    internal static LocalAccount Create(Uri origin)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new LocalAccount(origin.AbsoluteUri, Guid.NewGuid(), Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
    }

    internal DeviceEnrollment Enrollment()
    {
        using var key = OpenKey();
        var name = new string(Environment.MachineName.Where(character => !char.IsControl(character)).Take(100).ToArray()).Trim();
        if (name.Length == 0) name = "Tbox";
        return new DeviceEnrollment(DeviceId, name, OperatingSystem.IsWindows() ? "Windows" : "macOS",
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    internal string Sign(string purpose, string secret)
    {
        using var key = OpenKey();
        return Convert.ToBase64String(key.SignData(CloudProtocol.Proof(purpose, DeviceId, secret), HashAlgorithmName.SHA256));
    }

    private ECDsa OpenKey()
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(PrivateKey), out _);
            return key;
        }
        catch { key.Dispose(); throw; }
    }
}

internal sealed record CaptureUpload(DateTimeOffset CapturedAt, byte[] Png);

internal sealed class CloudClient(HttpClient http)
{
    internal static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(45),
        MaxResponseContentBufferSize = CloudProtocol.MaximumCaptureBytes
    };

    internal static bool TryOrigin(string? text, out Uri origin)
    {
        if (Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment))
        {
            origin = uri;
            return true;
        }
        origin = null!;
        return false;
    }

    internal Task<CloudSession> ExchangeAsync(LocalAccount account, string code, string verifier, CancellationToken ct) =>
        RequestAsync<CloudSession>(account.Origin, HttpMethod.Post, "/v1/auth/exchange", null,
            new ExchangeRequest(code, verifier, account.Sign("exchange", code), account.Enrollment()), ct);

    internal Task<CloudSession> RefreshAsync(LocalAccount account, CancellationToken ct) =>
        RequestAsync<CloudSession>(account.Origin, HttpMethod.Post, "/v1/auth/refresh", null, Proof(account, "refresh"), ct);

    internal async Task SignOutAsync(LocalAccount account, CancellationToken ct) =>
        await RequestAsync<object>(account.Origin, HttpMethod.Post, "/v1/auth/signout", null, Proof(account, "signout"), ct);

    internal async Task<T> RequestAsync<T>(string origin, HttpMethod method, string path, string? token, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(origin), path));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is CaptureUpload capture)
        {
            request.Headers.Add("X-Tbox-Captured-At", capture.CapturedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            request.Content = new ByteArrayContent(capture.Png);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        }
        else if (body is not null) request.Content = JsonContent.Create(body);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.NoContent) return default!;
        if (typeof(T) == typeof(byte[])) return (T)(object)await response.Content.ReadAsByteArrayAsync(ct);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct) ?? throw new JsonException();
    }

    internal async Task<bool> WatchAsync(LocalAccount account, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(account.Origin), "/v1/devices/events"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return true;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (true)
        {
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(45));
            var line = await reader.ReadLineAsync(heartbeat.Token);
            if (line == "event: revoked") return true;
            if (line is null) return false;
        }
    }

    private static RefreshRequest Proof(LocalAccount account, string purpose)
    {
        var secret = account.RefreshToken ?? throw new InvalidOperationException("请重新登录 Tbox。");
        return new RefreshRequest(account.DeviceId, secret, account.Sign(purpose, secret));
    }
}
