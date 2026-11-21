using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Tbox.Core.Accounts;
using Xunit;

namespace Tbox.Cloud.Tests;

public sealed class CloudIntegrationTests
{
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNQSGj4DwADpAIAhYa8VQAAAABJRU5ErkJggg==";

    private static CloudOptions Options(string database) => new(new Uri("https://tbox.test/"),
        RandomNumberGenerator.GetInt32(1, int.MaxValue), "fixture", "fixture", database, RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void ScreenshotEncryptionRejectsTamperingAndDifferentImageIds()
    {
        var options = Options("Host=localhost");
        var captures = new Captures(new Database(null!, options), options);
        var id = Guid.NewGuid();
        var png = Convert.FromBase64String(OnePixelPng);
        Assert.True(Captures.IsPng(png));
        Assert.False(Captures.IsPng(png[..32]));
        var encrypted = captures.Encrypt(id, png);
        Assert.False(png.SequenceEqual(encrypted));
        Assert.Equal(png, captures.Decrypt(id, encrypted));
        Assert.ThrowsAny<CryptographicException>(() => captures.Decrypt(Guid.NewGuid(), encrypted));
        encrypted[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => captures.Decrypt(id, encrypted));
    }

    [PostgresFact]
    public async Task DeviceProofsAuthorizationConflictsAndScreenshotLifecycle()
    {
        var options = Options(Environment.GetEnvironmentVariable("TBOX_TEST_POSTGRES")!);
        await using var source = NpgsqlDataSource.Create(options.Database);
        var db = new Database(source, options);
        await db.InitializeAsync(default);
        var sessions = new DeviceSessions(db);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = Guid.NewGuid();
        var enrollment = new DeviceEnrollment(id, "Home Mac", "macOS", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        string Sign(ECDsa signer, string purpose, string secret) =>
            Convert.ToBase64String(signer.SignData(CloudProtocol.Proof(purpose, id, secret), HashAlgorithmName.SHA256));

        var verifier = CloudProtocol.RandomSecret();
        var code = await sessions.IssueCodeAsync(CloudProtocol.Challenge(verifier), "owner", default);
        var exchange = new ExchangeRequest(code, verifier, Sign(key, "exchange", code), enrollment);
        Assert.Null(await sessions.ExchangeAsync(exchange with { Verifier = CloudProtocol.RandomSecret() }, default));
        Assert.Null(await sessions.ExchangeAsync(exchange with { Signature = Sign(wrongKey, "exchange", code) }, default));
        var session = Assert.IsType<CloudSession>(await sessions.ExchangeAsync(exchange, default));
        Assert.Null(await sessions.ExchangeAsync(exchange, default));

        var first = new DefaultHttpContext();
        first.Request.Headers.Authorization = "Bearer " + session.AccessToken;
        Assert.Equal(id, await sessions.AuthenticateAsync(first, default));
        var refresh = new RefreshRequest(id, session.RefreshToken, Sign(key, "refresh", session.RefreshToken));
        Assert.Null(await sessions.RefreshAsync(refresh with { Signature = Sign(wrongKey, "refresh", session.RefreshToken) }, default));
        var renewed = Assert.IsType<CloudSession>(await sessions.RefreshAsync(refresh, default));
        Assert.Null(await sessions.AuthenticateAsync(first, default));

        var laterCode = await sessions.IssueCodeAsync(CloudProtocol.Challenge(verifier), "owner", default);
        var later = Assert.IsType<CloudSession>(await sessions.ExchangeAsync(exchange with
        {
            Code = laterCode, Signature = Sign(key, "exchange", laterCode)
        }, default));
        Assert.NotEqual(renewed.RefreshToken, later.RefreshToken);
        Assert.Equal(id, Assert.Single(await sessions.ListAsync(default)).Id);

        var preferences = new Preferences(db);
        var initial = CloudPreferences.Snapshot(new Tbox.Core.AppSettings());
        Assert.Equal(3, (await preferences.InitializeAsync(initial, default))!.Count);
        var change = new PreferenceEdit(CloudProtocol.TranslationLanguage, "ja", 1);
        Assert.False((await preferences.ChangeAsync(change, default))!.Conflict);
        Assert.False((await preferences.ChangeAsync(change, default))!.Conflict);
        var conflict = await preferences.ChangeAsync(change with { Value = "ko" }, default);
        Assert.True(conflict!.Conflict);
        Assert.Equal("ja", conflict.Preference.Value);

        var captures = new Captures(db, options);
        var png = Convert.FromBase64String(OnePixelPng);
        var image = Guid.NewGuid();
        await captures.SaveAsync(image, DateTimeOffset.UtcNow, png, default);
        Assert.Equal(png, await captures.ReadAsync(image, default));
        Assert.Equal(image, Assert.Single(await captures.ListAsync(default)).Id);
        await captures.DeleteAsync(image, default);
        await captures.SaveAsync(image, DateTimeOffset.UtcNow, png, default);
        Assert.Null(await captures.ReadAsync(image, default));
        await captures.PurgeExpiredAsync(default);

        var events = new DeviceEvents();
        var watch = new DefaultHttpContext();
        watch.Request.Headers.Authorization = "Bearer " + later.AccessToken;
        using var stream = new WatchStream();
        watch.Response.Body = stream;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listening = events.WatchAsync(id, watch, sessions, timeout.Token);
        await stream.FirstFlush.Task.WaitAsync(timeout.Token);
        Assert.True(await sessions.RevokeAsync(id, default));
        events.Revoke(id);
        await listening;
        Assert.Contains("event: revoked", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        first.Request.Headers.Authorization = "Bearer " + later.AccessToken;
        Assert.Null(await sessions.AuthenticateAsync(first, default));
        Assert.Null(await sessions.RefreshAsync(new RefreshRequest(id, later.RefreshToken,
            Sign(key, "refresh", later.RefreshToken)), default));
        Assert.Empty(await sessions.ListAsync(default));
    }

    [PostgresFact]
    public async Task CountLimitRetainsTheNewestFiftyAndRetriesCannotRestoreAnEvictedCapture()
    {
        var options = Options(Environment.GetEnvironmentVariable("TBOX_TEST_POSTGRES")!);
        await using var source = NpgsqlDataSource.Create(options.Database);
        var db = new Database(source, options);
        await db.InitializeAsync(default);
        var captures = new Captures(db, options);
        var png = Convert.FromBase64String(OnePixelPng);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var ids = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray();
        // An older capture can arrive late when an offline device reconnects.
        foreach (var i in Enumerable.Range(1, 50).Append(0))
            await captures.SaveAsync(ids[i], start.AddSeconds(i), png, default);

        var expected = ids.Skip(1).Reverse().ToArray();
        Assert.Equal(expected, (await captures.ListAsync(default)).Select(c => c.Id));
        Assert.Null(await captures.ReadAsync(ids[0], default));
        await captures.DeleteAsync(expected[0], default);
        await captures.SaveAsync(ids[0], start, png, default);
        Assert.Equal(expected.Skip(1), (await captures.ListAsync(default)).Select(c => c.Id));
        Assert.Null(await captures.ReadAsync(ids[0], default));
    }

    private sealed class WatchStream : MemoryStream
    {
        internal TaskCompletionSource FirstFlush { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FirstFlush.TrySetResult();
            return base.FlushAsync(cancellationToken);
        }
    }
}


internal sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TBOX_TEST_POSTGRES")))
            Skip = "Set TBOX_TEST_POSTGRES to a disposable PostgreSQL database.";
    }
}
