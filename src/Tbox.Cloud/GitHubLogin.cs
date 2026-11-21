using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Tbox.Core.Accounts;

namespace Tbox.Cloud;

internal sealed class GitHubLogin(
    CloudOptions options, IDataProtectionProvider dataProtection, IHttpClientFactory clients, DeviceSessions sessions)
{
    private const string Cookie = "__Host-TboxLogin";
    private ITimeLimitedDataProtector Protector => dataProtection.CreateProtector("Tbox.GitHubLogin.v1").ToTimeLimitedDataProtector();
    private string CallbackUri => new Uri(options.Origin, "/v1/auth/github/callback").AbsoluteUri;

    internal IResult Start(HttpContext context, int port, string challenge, string state)
    {
        if (port is < 1024 or > 65535 || !CloudProtocol.IsSecret(challenge) || !CloudProtocol.IsSecret(state))
            return Results.BadRequest();
        var flow = new BrowserFlow(port, challenge, state, CloudProtocol.RandomSecret(), CloudProtocol.RandomSecret());
        var protectedState = Protector.Protect(JsonSerializer.Serialize(flow), TimeSpan.FromMinutes(5));
        context.Response.Cookies.Append(Cookie, flow.Cookie, new CookieOptions
        {
            Path = "/", HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(5)
        });
        return Results.Redirect(QueryHelpers.AddQueryString("https://github.com/login/oauth/authorize",
            new Dictionary<string, string?>
            {
                ["client_id"] = options.ClientId,
                ["redirect_uri"] = CallbackUri,
                ["state"] = protectedState,
                ["code_challenge"] = CloudProtocol.Challenge(flow.GitHubVerifier),
                ["code_challenge_method"] = "S256"
            }));
    }

    internal async Task<IResult> CompleteAsync(HttpContext context, string? code, string? state, CancellationToken ct)
    {
        if (state is not { Length: > 0 and <= 4096 }) return Results.BadRequest();
        BrowserFlow? flow;
        try { flow = JsonSerializer.Deserialize<BrowserFlow>(Protector.Unprotect(state)); }
        catch (CryptographicException) { return Results.BadRequest(); }
        if (flow is null || !context.Request.Cookies.TryGetValue(Cookie, out var cookie) ||
            !CryptographicOperations.FixedTimeEquals(DeviceSessions.Hash(cookie), DeviceSessions.Hash(flow.Cookie)))
            return Results.BadRequest();
        context.Response.Cookies.Delete(Cookie, new CookieOptions
        {
            Path = "/", Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax
        });

        string? issued = null;
        if (code is { Length: > 0 and <= 1024 })
        {
            try
            {
                if (await ReadOwnerAsync(code, flow.GitHubVerifier, ct) is { } login)
                    issued = await sessions.IssueCodeAsync(flow.Challenge, login, ct);
            }
            catch (Exception error) when (error is HttpRequestException or JsonException || error is TaskCanceledException && !ct.IsCancellationRequested)
            {
                // A GitHub error is indistinguishable from a denied login in the redirect.
            }
        }
        var destination = $"http://127.0.0.1:{flow.Port}/tbox-callback";
        return Results.Redirect(QueryHelpers.AddQueryString(destination, new Dictionary<string, string?>
        {
            ["state"] = flow.AppState,
            [issued is null ? "error" : "code"] = issued ?? "denied"
        }));
    }

    private async Task<string?> ReadOwnerAsync(string code, string verifier, CancellationToken ct)
    {
        var client = clients.CreateClient("GitHub");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId, ["client_secret"] = options.ClientSecret,
                ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = CallbackUri
            })
        };
        using var answer = await client.SendAsync(request, ct);
        if (!answer.IsSuccessStatusCode) return null;
        using var exchange = await ReadJsonAsync(answer, ct);
        if (!exchange.RootElement.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String)
            return null;

        using var user = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        user.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.GetString());
        using var identity = await client.SendAsync(user, ct);
        if (!identity.IsSuccessStatusCode) return null;
        using var result = await ReadJsonAsync(identity, ct);
        var root = result.RootElement;
        return root.TryGetProperty("id", out var id) && id.TryGetInt64(out var userId) &&
            userId == options.OwnerId && root.TryGetProperty("login", out var login) &&
            login.GetString() is { Length: > 0 and <= 100 } name ? name : null;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var content = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(content, cancellationToken: ct);
    }

    private sealed record BrowserFlow(int Port, string Challenge, string AppState, string Cookie, string GitHubVerifier);
}
