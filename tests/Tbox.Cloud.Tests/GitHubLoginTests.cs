using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using Tbox.Core.Accounts;
using Xunit;

namespace Tbox.Cloud.Tests;

public sealed class GitHubLoginTests
{
    [PostgresFact]
    public async Task BrowserAuthorizationChecksCorrelationAndTheConfiguredNumericOwner()
    {
        var settings = new CloudOptions(new Uri("https://tbox.test/"), 1500, "fixture", "fixture",
            Environment.GetEnvironmentVariable("TBOX_TEST_POSTGRES")!, new byte[32]);
        await using var source = NpgsqlDataSource.Create(settings.Database);
        var database = new Database(source, settings);
        await database.InitializeAsync(default);
        var provider = new GitHubHandler();
        using var http = new HttpClient(provider);
        var login = new GitHubLogin(settings, new EphemeralDataProtectionProvider(), new Factory(http), new DeviceSessions(database));
        var state = CloudProtocol.RandomSecret();
        var challenge = CloudProtocol.Challenge(CloudProtocol.RandomSecret());

        (string State, string Cookie) Start()
        {
            var flow = new DefaultHttpContext();
            var redirect = Assert.IsType<RedirectHttpResult>(login.Start(flow, 55222, challenge, state));
            var query = QueryHelpers.ParseQuery(new Uri(redirect.Url).Query);
            Assert.Equal("S256", query["code_challenge_method"].ToString());
            Assert.Equal("https://tbox.test/v1/auth/github/callback", query["redirect_uri"].ToString());
            return (query["state"].ToString(), flow.Response.Headers.SetCookie.ToString().Split(';')[0]);
        }

        var attempt = Start();
        Assert.IsType<BadRequest>(await login.CompleteAsync(new DefaultHttpContext(), "fixture", attempt.State, default));
        var rejected = new DefaultHttpContext();
        rejected.Request.Headers.Cookie = attempt.Cookie;
        var failed = Assert.IsType<RedirectHttpResult>(await login.CompleteAsync(rejected, "fixture", attempt.State, default));
        Assert.Contains("error=denied", failed.Url);

        attempt = Start();
        provider.UserId = settings.OwnerId;
        var accepted = new DefaultHttpContext();
        accepted.Request.Headers.Cookie = attempt.Cookie;
        var redirect = Assert.IsType<RedirectHttpResult>(await login.CompleteAsync(accepted, "fixture", attempt.State, default));
        var destination = new Uri(redirect.Url);
        Assert.Equal("127.0.0.1", destination.Host);
        Assert.Equal(55222, destination.Port);
        var ticket = QueryHelpers.ParseQuery(destination.Query);
        Assert.Equal(state, ticket["state"].ToString());
        Assert.True(CloudProtocol.IsSecret(ticket["code"].ToString()));
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class GitHubHandler : HttpMessageHandler
    {
        internal long UserId { get; set; } = 1499;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            switch (request.RequestUri!.Host)
            {
                case "github.com":
                    var body = await request.Content!.ReadAsStringAsync(ct);
                    Assert.Contains("code_verifier=", body);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "fixture" }) };
                case "api.github.com":
                    Assert.Equal("fixture", request.Headers.Authorization!.Parameter);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = UserId, login = "account" }) };
                default:
                    throw new InvalidOperationException("Unexpected fixture address.");
            }
        }
    }
}
