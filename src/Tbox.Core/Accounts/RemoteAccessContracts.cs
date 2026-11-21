namespace Tbox.Core.Accounts;

public static class RemoteAccessProtocol
{
    public const int LeaseSeconds = 30;

    public static byte[] HostRegistrationProof(Guid host, string credential, RemoteTransportIdentity transport) =>
        CloudProtocol.Proof("remote-host-transport", host, $"{credential}:{transport.PeerId}:{transport.PublicKey}");

    public static byte[] ClaimProof(Guid controller, Guid host, string ticket, string challenge) =>
        CloudProtocol.Proof("remote-claim", controller, $"{host:D}:{ticket}:{challenge}");

    public static bool IsTransportIdentity(RemoteTransportIdentity? transport)
    {
        if (transport?.PeerId is not { Length: >= 6 and <= 32 } id || !char.IsAsciiLetterOrDigit(id[0]) ||
            !id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ||
            transport.PublicKey is not { Length: 44 } publicKey)
            return false;
        Span<byte> key = stackalloc byte[32];
        return Convert.TryFromBase64String(publicKey, key, out var size) && size == key.Length &&
            Convert.ToBase64String(key) == publicKey;
    }
}

public sealed record RemoteTransportIdentity(string PeerId, string PublicKey);
public sealed record RemoteHostRegistration(string Credential, RemoteTransportIdentity Transport, string Signature);
public sealed record RemoteHost(Guid DeviceId, string Name, string Platform);
public sealed record RemoteConnectionRequest(Guid HostId);
public sealed record RemoteInvitation(Guid SessionId, string Ticket, DateTimeOffset ExpiresAt, Guid HostId,
    RemoteTransportIdentity Transport);
public sealed record RemoteClaim(string Ticket, string Challenge, string Signature);
public sealed record RemoteLease(Guid SessionId, Guid ControllerId, Guid HostId, DateTimeOffset ExpiresAt);
