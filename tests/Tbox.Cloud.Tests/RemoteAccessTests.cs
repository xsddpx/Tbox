using System.Security.Cryptography;
using Npgsql;
using Tbox.Core.Accounts;
using Xunit;

namespace Tbox.Cloud.Tests;

public sealed class RemoteAccessTests
{
    [PostgresFact]
    public async Task OnlyReadyConsentingHostsCanRedeemAnInvitationSignedByTheControllerOnce()
    {
        var database = Environment.GetEnvironmentVariable("TBOX_TEST_POSTGRES")!;
        await using var source = NpgsqlDataSource.Create(database);
        var db = TestDatabase(source, database);
        await db.InitializeAsync(default);
        var devices = new DeviceSessions(db);
        var remote = new RemoteAccess(db);
        using var controller = new TestDevice("Controller", "macOS");
        using var host = new TestDevice("Host", "Windows");
        using var otherHost = new TestDevice("Other host", "Windows");
        await controller.SignInAsync(devices);
        var hostAccount = await host.SignInAsync(devices);
        await otherHost.SignInAsync(devices);
        var credential = CloudProtocol.RandomSecret();
        var otherCredential = CloudProtocol.RandomSecret();
        Assert.Empty(await remote.ListHostsAsync(controller.Id, default));
        Assert.Null(await remote.InviteAsync(controller.Id, host.Id, default));
        Assert.False(await remote.HeartbeatAsync(hostAccount.AccessToken, default));
        Assert.False(await remote.RegisterHostAsync(host.Id, controller.Register(credential), default));
        Assert.True(await remote.RegisterHostAsync(host.Id, host.Register(credential), default));
        Assert.False(await remote.RegisterHostAsync(otherHost.Id, otherHost.Register(credential), default));
        Assert.True(await remote.RegisterHostAsync(otherHost.Id, otherHost.Register(otherCredential), default));
        Assert.Empty(await remote.ListHostsAsync(controller.Id, default));

        Assert.True(await remote.HeartbeatAsync(credential, default));
        Assert.True(await remote.RegisterHostAsync(host.Id, host.Register(credential), default));
        Assert.Equal(host.Id, Assert.Single(await remote.ListHostsAsync(controller.Id, default)).DeviceId);
        Assert.Empty(await remote.ListHostsAsync(host.Id, default));
        Assert.True(await remote.HeartbeatAsync(otherCredential, default));
        Assert.Null(await remote.InviteAsync(host.Id, host.Id, default));
        Assert.Null(await remote.InviteAsync(Guid.NewGuid(), host.Id, default));
        var grant = Assert.IsType<RemoteInvitation>(await remote.InviteAsync(controller.Id, host.Id, default));
        Assert.Null(await remote.RenewAsync(credential, grant.SessionId, default));
        var challenge = CloudProtocol.RandomSecret();
        var validClaim = controller.Claim(host.Id, grant.Ticket, challenge);
        Assert.Null(await remote.ClaimAsync(otherCredential, validClaim, default));
        Assert.Null(await remote.ClaimAsync(credential, host.Claim(host.Id, grant.Ticket, challenge), default));
        Assert.Null(await remote.ClaimAsync(credential, validClaim with { Challenge = CloudProtocol.RandomSecret() }, default));

        var claims = await Task.WhenAll(remote.ClaimAsync(credential, validClaim, default), remote.ClaimAsync(credential, validClaim, default));
        var lease = Assert.Single(claims.OfType<RemoteLease>());
        Assert.Equal(grant.SessionId, lease.SessionId);
        Assert.Equal(controller.Id, lease.ControllerId);
        Assert.Equal(host.Id, lease.HostId);
        Assert.InRange(lease.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.Zero,
            TimeSpan.FromSeconds(RemoteAccessProtocol.LeaseSeconds + 1));
        Assert.Null(await remote.RenewAsync(otherCredential, lease.SessionId, default));
        Assert.NotNull(await remote.RenewAsync(credential, lease.SessionId, default));

        await remote.DisableHostAsync(host.Id, default);
        Assert.False(await remote.HeartbeatAsync(credential, default));
        Assert.Null(await remote.RenewAsync(credential, lease.SessionId, default));
        Assert.Null(await remote.InviteAsync(controller.Id, host.Id, default));
    }

    [PostgresFact]
    public async Task ExpiredAndRevokedGrantsAreNeverRestoredBySigningBackInOrReRegisteringTheHost()
    {
        var database = Environment.GetEnvironmentVariable("TBOX_TEST_POSTGRES")!;
        await using var source = NpgsqlDataSource.Create(database);
        var db = TestDatabase(source, database);
        await db.InitializeAsync(default);
        var devices = new DeviceSessions(db);
        var remote = new RemoteAccess(db);
        using var controller = new TestDevice("Controller", "macOS");
        using var host = new TestDevice("Host", "Windows");
        var controllerAccount = await controller.SignInAsync(devices);
        await host.SignInAsync(devices);
        var credential = CloudProtocol.RandomSecret();
        Assert.True(await remote.RegisterHostAsync(host.Id, host.Register(credential), default));
        Assert.True(await remote.HeartbeatAsync(credential, default));
        var challenge = CloudProtocol.RandomSecret();
        var expired = Assert.IsType<RemoteInvitation>(await remote.InviteAsync(controller.Id, host.Id, default));
        await using (var connection = await db.ConnectAsync(default))
        await using (var setDeadline = db.Command(connection,
            "UPDATE remote_session SET expires_at=statement_timestamp() WHERE owner=@owner AND id=@id", ("id", expired.SessionId)))
            await setDeadline.ExecuteNonQueryAsync();
        Assert.Null(await remote.ClaimAsync(credential, controller.Claim(host.Id, expired.Ticket, challenge), default));

        var active = Assert.IsType<RemoteInvitation>(await remote.InviteAsync(controller.Id, host.Id, default));
        var lease = Assert.IsType<RemoteLease>(
            await remote.ClaimAsync(credential, controller.Claim(host.Id, active.Ticket, challenge), default));
        var unused = Assert.IsType<RemoteInvitation>(await remote.InviteAsync(controller.Id, host.Id, default));
        Assert.True(await devices.EndSessionAsync(controller.SignOut(controllerAccount), default));
        Assert.Null(await remote.RenewAsync(credential, lease.SessionId, default));
        await controller.SignInAsync(devices);
        Assert.Null(await remote.ClaimAsync(credential, controller.Claim(host.Id, unused.Ticket, challenge), default));
        Assert.Null(await remote.RenewAsync(credential, lease.SessionId, default));

        var rotated = CloudProtocol.RandomSecret();
        Assert.True(await remote.RegisterHostAsync(host.Id, host.Register(rotated), default));
        Assert.False(await remote.HeartbeatAsync(credential, default));
        Assert.True(await remote.HeartbeatAsync(rotated, default));
        var previous = Assert.IsType<RemoteInvitation>(await remote.InviteAsync(controller.Id, host.Id, default));
        Assert.True(await devices.RevokeAsync(host.Id, default));
        Assert.False(await remote.HeartbeatAsync(rotated, default));
        await host.SignInAsync(devices);
        Assert.False(await remote.HeartbeatAsync(rotated, default));
        Assert.True(await remote.RegisterHostAsync(host.Id, host.Register(rotated), default));
        Assert.True(await remote.HeartbeatAsync(rotated, default));
        Assert.Null(await remote.ClaimAsync(rotated, controller.Claim(host.Id, previous.Ticket, challenge), default));
    }

    private static Database TestDatabase(NpgsqlDataSource source, string database) =>
        new(source, new CloudOptions(new Uri("https://tbox.test/"), RandomNumberGenerator.GetInt32(1, int.MaxValue),
            "fixture", "fixture", database, RandomNumberGenerator.GetBytes(32)));

    private sealed class TestDevice(string name, string platform) : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal Guid Id { get; } = Guid.NewGuid();

        internal async Task<CloudSession> SignInAsync(DeviceSessions sessions)
        {
            var verifier = CloudProtocol.RandomSecret();
            var code = await sessions.IssueCodeAsync(CloudProtocol.Challenge(verifier), "owner", default);
            var identity = new DeviceEnrollment(Id, name, platform, Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()));
            return Assert.IsType<CloudSession>(await sessions.ExchangeAsync(
                new ExchangeRequest(code, verifier, Sign(CloudProtocol.Proof("exchange", Id, code)), identity), default));
        }

        internal RefreshRequest SignOut(CloudSession session) =>
            new(Id, session.RefreshToken, Sign(CloudProtocol.Proof("signout", Id, session.RefreshToken)));

        internal RemoteClaim Claim(Guid host, string ticket, string challenge) =>
            new(ticket, challenge, Sign(RemoteAccessProtocol.ClaimProof(Id, host, ticket, challenge)));

        internal RemoteHostRegistration Register(string credential) =>
            new(credential, Sign(RemoteAccessProtocol.HostRegistrationProof(Id, credential)));

        private string Sign(byte[] bytes) => Convert.ToBase64String(_key.SignData(bytes, HashAlgorithmName.SHA256));
        public void Dispose() => _key.Dispose();
    }
}
