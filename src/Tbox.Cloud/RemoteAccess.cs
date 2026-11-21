using Npgsql;
using Tbox.Core.Accounts;

namespace Tbox.Cloud;

internal sealed class RemoteAccess(Database db)
{
    private const string ActiveDevice =
        "NOT d.revoked AND d.refresh_hash IS NOT NULL AND d.refresh_until > statement_timestamp()";
    private const string Deadline = "statement_timestamp() + @seconds * interval '1 second'";
    private static readonly (string, object) LeasePeriod = ("seconds", RemoteAccessProtocol.LeaseSeconds);

    internal async Task<bool> RegisterHostAsync(Guid deviceId, RemoteHostRegistration registration, CancellationToken ct)
    {
        var credential = registration.Credential;
        var transport = registration.Transport;
        if (!CloudProtocol.IsSecret(credential) || !RemoteAccessProtocol.IsTransportIdentity(transport)) return false;
        await using var connection = await db.ConnectAsync(ct);
        await using var transaction = await db.LockAccountAsync(connection, ct);
        var hash = DeviceSessions.Hash(credential);
        await using var eligible = Command(transaction, $"""
            SELECT d.public_key FROM device d WHERE d.owner=@owner AND d.id=@id AND {ActiveDevice}
            AND NOT EXISTS(
                SELECT 1 FROM remote_host WHERE owner=@owner AND device_id<>@id
                  AND (credential_hash=@hash OR transport_id=@peer OR transport_key=@key)
            )
            """, ("id", deviceId), ("hash", hash), ("peer", transport.PeerId), ("key", transport.PublicKey));
        if (await eligible.ExecuteScalarAsync(ct) is not byte[] key ||
            !DeviceSessions.Verify(key, registration.Signature, RemoteAccessProtocol.HostRegistrationProof(deviceId, credential, transport)))
            return false;
        await using var update = Command(transaction, """
            INSERT INTO remote_host (owner, device_id, credential_hash, transport_id, transport_key)
            VALUES (@owner,@id,@hash,@peer,@key)
            ON CONFLICT (owner, device_id) DO UPDATE
            SET credential_hash=EXCLUDED.credential_hash, transport_id=EXCLUDED.transport_id,
              transport_key=EXCLUDED.transport_key, ready_until=NULL
            WHERE (remote_host.credential_hash,remote_host.transport_id,remote_host.transport_key)
                  IS DISTINCT FROM (EXCLUDED.credential_hash,EXCLUDED.transport_id,EXCLUDED.transport_key)
            """, ("id", deviceId), ("hash", hash), ("peer", transport.PeerId), ("key", transport.PublicKey));
        if (await update.ExecuteNonQueryAsync(ct) != 0)
        {
            await using var previous = Command(transaction,
                "DELETE FROM remote_session WHERE owner=@owner AND host_id=@id", ("id", deviceId));
            await previous.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return true;
    }

    internal async Task DisableHostAsync(Guid deviceId, CancellationToken ct)
    {
        await using var connection = await db.ConnectAsync(ct);
        await using var transaction = await db.LockAccountAsync(connection, ct);
        await using var command = Command(transaction,
            "DELETE FROM remote_host WHERE owner=@owner AND device_id=@id", ("id", deviceId));
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    internal async Task<bool> HeartbeatAsync(string credential, CancellationToken ct)
    {
        if (!CloudProtocol.IsSecret(credential)) return false;
        await using var connection = await db.ConnectAsync(ct);
        await using var transaction = await db.LockAccountAsync(connection, ct);
        await using var command = Command(transaction, $"""
            UPDATE remote_host h SET ready_until={Deadline}
            FROM device d WHERE d.owner=h.owner AND d.id=h.device_id
                AND h.owner=@owner AND h.credential_hash=@hash AND h.transport_key IS NOT NULL AND {ActiveDevice}
            """, ("hash", DeviceSessions.Hash(credential)), LeasePeriod);
        var ready = await command.ExecuteNonQueryAsync(ct) == 1;
        await transaction.CommitAsync(ct);
        return ready;
    }

    internal async Task<List<RemoteHost>> ListHostsAsync(Guid controller, CancellationToken ct)
    {
        await using var connection = await db.ConnectAsync(ct);
        await using var command = db.Command(connection, $"""
            SELECT h.device_id,d.name,d.platform
            FROM remote_host h JOIN device d ON d.owner=h.owner AND d.id=h.device_id
            WHERE h.owner=@owner AND h.device_id<>@controller AND h.ready_until>statement_timestamp()
              AND h.transport_key IS NOT NULL AND {ActiveDevice}
            ORDER BY d.name,h.device_id
            """, ("controller", controller));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var hosts = new List<RemoteHost>();
        while (await reader.ReadAsync(ct))
            hosts.Add(new RemoteHost(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        return hosts;
    }

    internal async Task<RemoteInvitation?> InviteAsync(Guid controller, Guid host, CancellationToken ct)
    {
        if (host == Guid.Empty || host == controller) return null;
        var id = Guid.NewGuid();
        var ticket = CloudProtocol.RandomSecret();
        await using var connection = await db.ConnectAsync(ct);
        await using var transaction = await db.LockAccountAsync(connection, ct);
        await using var expired = Command(transaction,
            "DELETE FROM remote_session WHERE owner=@owner AND expires_at<=statement_timestamp()");
        await expired.ExecuteNonQueryAsync(ct);
        await using var command = Command(transaction, $"""
            WITH target AS (
                SELECT h.device_id,h.transport_id,h.transport_key
                FROM remote_host h JOIN device d ON d.owner=h.owner AND d.id=h.device_id
                WHERE h.owner=@owner AND h.device_id=@host AND h.ready_until>statement_timestamp()
                  AND h.transport_key IS NOT NULL AND {ActiveDevice}
                  AND EXISTS (SELECT 1 FROM device d WHERE d.owner=@owner AND d.id=@controller AND {ActiveDevice})
                  AND (SELECT COUNT(*) FROM remote_session s WHERE s.owner=@owner AND s.controller_id=@controller
                       AND s.ticket_hash IS NOT NULL AND s.expires_at>statement_timestamp()) < 4
            ), invitation AS (
                INSERT INTO remote_session (owner,id,host_id,controller_id,ticket_hash,expires_at)
                SELECT @owner,@id,device_id,@controller,@hash,{Deadline} FROM target
                RETURNING expires_at
            )
            SELECT expires_at,transport_id,transport_key FROM invitation CROSS JOIN target
            """, ("id", id), ("host", host), ("controller", controller), ("hash", DeviceSessions.Hash(ticket)), LeasePeriod);
        RemoteInvitation? invitation = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
                invitation = new RemoteInvitation(id, ticket, reader.GetFieldValue<DateTimeOffset>(0), host,
                    new RemoteTransportIdentity(reader.GetString(1), reader.GetString(2)));
        await transaction.CommitAsync(ct);
        return invitation;
    }

    internal async Task<RemoteLease?> ClaimAsync(string credential, RemoteClaim claim, CancellationToken ct)
    {
        if (!CloudProtocol.IsSecret(credential) || !CloudProtocol.IsSecret(claim.Ticket) ||
            !CloudProtocol.IsSecret(claim.Challenge)) return null;
        await using var connection = await db.ConnectAsync(ct);
        await using var transaction = await db.LockAccountAsync(connection, ct);
        var host = await FindHostAsync(transaction, credential, ct);
        if (host is null) return null;

        Guid session, controller;
        await using (var command = Command(transaction, $"""
            SELECT s.id,s.controller_id,d.public_key
            FROM remote_session s JOIN device d ON d.owner=s.owner AND d.id=s.controller_id
            WHERE s.owner=@owner AND s.host_id=@host AND s.ticket_hash=@hash
                AND s.expires_at>statement_timestamp() AND {ActiveDevice}
            """, ("host", host.Value), ("hash", DeviceSessions.Hash(claim.Ticket))))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            session = reader.GetGuid(0);
            controller = reader.GetGuid(1);
            var proof = RemoteAccessProtocol.ClaimProof(controller, host.Value, claim.Ticket, claim.Challenge);
            if (!DeviceSessions.Verify(reader.GetFieldValue<byte[]>(2), claim.Signature, proof)) return null;
        }

        await using var consume = Command(transaction, $"""
            UPDATE remote_session SET ticket_hash=NULL,expires_at={Deadline}
            WHERE owner=@owner AND id=@id AND expires_at>statement_timestamp()
            RETURNING expires_at
            """, ("id", session), LeasePeriod);
        var answer = await consume.ExecuteScalarAsync(ct);
        await transaction.CommitAsync(ct);
        return answer is DateTime expiry ? new RemoteLease(session, controller, host.Value, expiry) : null;
    }

    internal async Task<RemoteLease?> RenewAsync(string credential, Guid session, CancellationToken ct)
    {
        if (!CloudProtocol.IsSecret(credential) || session == Guid.Empty) return null;
        await using var connection = await db.ConnectAsync(ct);
        await using var transaction = await db.LockAccountAsync(connection, ct);
        var host = await FindHostAsync(transaction, credential, ct);
        if (host is null) return null;
        await using var command = Command(transaction, $"""
            UPDATE remote_session s SET expires_at={Deadline}
            FROM device d
            WHERE d.owner=s.owner AND d.id=s.controller_id AND {ActiveDevice}
                AND s.owner=@owner AND s.id=@id AND s.host_id=@host
                AND s.ticket_hash IS NULL AND s.expires_at>statement_timestamp()
            RETURNING s.controller_id,s.expires_at
            """, ("id", session), ("host", host.Value), LeasePeriod);
        RemoteLease? answer = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
                answer = new RemoteLease(session, reader.GetGuid(0), host.Value, reader.GetFieldValue<DateTimeOffset>(1));
        await transaction.CommitAsync(ct);
        return answer;
    }

    internal static async Task RevokeDeviceAsync(Database db, NpgsqlTransaction transaction, Guid device, CancellationToken ct)
    {
        await using var command = db.Command(transaction.Connection!, """
            DELETE FROM remote_host WHERE owner=@owner AND device_id=@id;
            DELETE FROM remote_session WHERE owner=@owner AND controller_id=@id;
            """, ("id", device));
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<Guid?> FindHostAsync(NpgsqlTransaction transaction, string credential, CancellationToken ct)
    {
        await using var command = Command(transaction, $"""
            SELECT h.device_id FROM remote_host h JOIN device d ON d.owner=h.owner AND d.id=h.device_id
            WHERE h.owner=@owner AND h.credential_hash=@hash AND h.ready_until>statement_timestamp()
              AND h.transport_key IS NOT NULL AND {ActiveDevice}
            """, ("hash", DeviceSessions.Hash(credential)));
        return await command.ExecuteScalarAsync(ct) as Guid?;
    }

    private NpgsqlCommand Command(NpgsqlTransaction transaction, string sql, params (string, object)[] parameters)
    {
        var command = db.Command(transaction.Connection!, sql, parameters);
        command.Transaction = transaction;
        return command;
    }
}
