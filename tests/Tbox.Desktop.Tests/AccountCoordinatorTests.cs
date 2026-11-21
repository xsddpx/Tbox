using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Tbox.Core;
using Tbox.Core.Accounts;
using Tbox.Desktop.Accounts;
using Tbox.Desktop.Platform;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class AccountCoordinatorTests
{
    [AvaloniaTheory]
    [InlineData(null, "https://configured.tbox.test/", "https://configured.tbox.test/")]
    [InlineData("https://existing.tbox.test/", "https://configured.tbox.test/", "https://existing.tbox.test/")]
    [InlineData(null, "https://configured.tbox.test/path", null)]
    public async Task SignInUsesTheConfiguredOriginAndOpensTheBrowserWithoutAnAddressField(
        string? savedOrigin, string packagedOrigin, string? expectedOrigin)
    {
        var settings = new AppSettings { Cloud = new LocalCloudPreferences { Origin = savedOrigin } };
        var account = savedOrigin is null ? null : LocalAccount.Create(new Uri(savedOrigin));
        var vault = new FixtureVault(account is null ? null : JsonSerializer.Serialize(account));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var callback = new HttpClient();
        using var handler = new CloudHandler();
        using var http = new HttpClient(handler);
        var code = CloudProtocol.RandomSecret();
        Uri? browserUrl = null;
        Task<string>? returned = null;
        using var coordinator = new AccountCoordinator(() => settings, edit =>
        {
            settings = edit(settings);
            return Task.CompletedTask;
        }, vault, Path.GetTempPath(), timeout.Token, http, packagedOrigin, url =>
        {
            browserUrl = url;
            var query = System.Web.HttpUtility.ParseQueryString(url.Query);
            returned = callback.GetStringAsync(
                $"http://127.0.0.1:{query["port"]}/tbox-callback?state={query["state"]}&code={code}", timeout.Token);
        });

        Assert.Equal(expectedOrigin is not null, coordinator.CanSignIn);
        await coordinator.SignInAsync();
        if (expectedOrigin is null)
        {
            Assert.Null(browserUrl);
            Assert.Null(vault.Value);
            Assert.Equal("账号服务尚未配置。", coordinator.Status);
            return;
        }
        Assert.NotNull(browserUrl);
        Assert.Equal(new Uri(expectedOrigin).Authority, browserUrl.Authority);
        Assert.Equal("/v1/auth/start", browserUrl.AbsolutePath);
        Assert.Contains("请返回 Tbox", await returned!);
        Assert.True(coordinator.SignedIn);
        Assert.Equal(expectedOrigin, settings.Cloud.Origin);
        var stored = JsonSerializer.Deserialize<LocalAccount>(vault.Value!)!;
        if (account is not null) Assert.Equal(account.DeviceId, stored.DeviceId);
        Assert.Equal(code, handler.ExchangedCode);
        Assert.Equal(System.Web.HttpUtility.ParseQueryString(browserUrl.Query)["challenge"],
            CloudProtocol.Challenge(handler.ExchangedVerifier!));
    }

    [AvaloniaFact]
    public async Task SigningOutCancelsTheInFlightScreenshotAndDiscardsPendingWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbox-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var account = LocalAccount.Create(new Uri("https://tbox.test/")) with { RefreshToken = CloudProtocol.RandomSecret(), Login = "owner" };
        var settings = new AppSettings
        {
            Cloud = new LocalCloudPreferences { Origin = account.Origin, SyncCaptures = true, SyncPreferences = false }
        };
        var vault = new FixtureVault(JsonSerializer.Serialize(account));
        using var handler = new CloudHandler();
        using var http = new HttpClient(handler);
        var outbox = new CaptureOutbox(Path.Combine(root, "cloud-capture-outbox"));
        try
        {
            await outbox.EnqueueAsync(account, [1, 2, 3], timeout.Token);
            await outbox.EnqueueAsync(account, [4, 5, 6], timeout.Token);
            using var coordinator = new AccountCoordinator(() => settings, update =>
            {
                settings = update(settings);
                return Task.CompletedTask;
            }, vault, root, timeout.Token, http);
            var refreshing = coordinator.RefreshAsync();
            await handler.UploadStarted.Task.WaitAsync(timeout.Token);

            await coordinator.SignOutAsync();
            await refreshing;

            Assert.True(handler.UploadCancelled);
            Assert.True(handler.ServerSignOut);
            Assert.Equal(1, handler.UploadCount);
            Assert.False(coordinator.SignedIn);
            Assert.False(settings.Cloud.SyncCaptures);
            Assert.Empty(outbox.Pending(account));
            Assert.Null(JsonSerializer.Deserialize<LocalAccount>(vault.Value!)!.RefreshToken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class FixtureVault(string? initial) : IAccountCredentialVault
    {
        internal string? Value { get; private set; } = initial;
        public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult<string?>(Value);
        public Task WriteAsync(string value, CancellationToken ct) { Value = value; return Task.CompletedTask; }
    }

    private sealed class CloudHandler : HttpMessageHandler
    {
        internal TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool UploadCancelled, ServerSignOut;
        internal int UploadCount;
        internal string? ExchangedCode, ExchangedVerifier;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Put)
            {
                UploadCount++;
                UploadStarted.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { UploadCancelled = true; throw; }
            }
            var path = request.RequestUri!.AbsolutePath;
            switch (path)
            {
                case "/v1/auth/exchange":
                    var exchange = (await request.Content!.ReadFromJsonAsync<ExchangeRequest>(cancellationToken: ct))!;
                    ExchangedCode = exchange.Code;
                    ExchangedVerifier = exchange.Verifier;
                    goto case "/v1/auth/refresh";
                case "/v1/auth/refresh":
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                        new CloudSession(CloudProtocol.RandomSecret(), DateTimeOffset.UtcNow.AddMinutes(10), "fixture", "owner")) };
                case "/v1/auth/signout":
                    ServerSignOut = true;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case "/v1/devices/events":
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
                default:
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            }
        }
    }
}
