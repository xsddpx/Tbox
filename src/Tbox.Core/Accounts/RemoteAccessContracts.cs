namespace Tbox.Core.Accounts;

public static class RemoteAccessProtocol
{
    public const int LeaseSeconds = 30;

    public static byte[] HostRegistrationProof(Guid host, string credential) =>
        CloudProtocol.Proof("remote-host", host, credential);

    public static byte[] ClaimProof(Guid controller, Guid host, string ticket, string challenge) =>
        CloudProtocol.Proof("remote-claim", controller, $"{host:D}:{ticket}:{challenge}");
}

public sealed record RemoteHostRegistration(string Credential, string Signature);
public sealed record RemoteHost(Guid DeviceId, string Name, string Platform);
public sealed record RemoteConnectionRequest(Guid HostId);
public sealed record RemoteInvitation(Guid SessionId, string Ticket, DateTimeOffset ExpiresAt);
public sealed record RemoteClaim(string Ticket, string Challenge, string Signature);
public sealed record RemoteLease(Guid SessionId, Guid ControllerId, Guid HostId, DateTimeOffset ExpiresAt);
