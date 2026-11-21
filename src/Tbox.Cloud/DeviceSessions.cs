using System.Security.Cryptography;
using System.Text;
using Tbox.Core.Accounts;

namespace Tbox.Cloud;

internal sealed class DeviceSessions(Database db)
{
    internal static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);

    internal async Task<string> IssueCodeAsync(string challenge, string login, CancellationToken ct)
    {
        var code = CloudProtocol.RandomSecret();
        await using var connection = await db.ConnectAsync(ct);
        await using var command = db.Command(connection, """
            DELETE FROM login_code WHERE owner=@owner AND expires<=now();
            INSERT INTO login_code (owner, hash, challenge, login, expires)
            VALUES (@owner, @hash, @challenge, @login, now() + interval '1 minute')
            """, ("hash", Hash(code)), ("challenge", challenge), ("login", login));
        await command.ExecuteNonQueryAsync(ct);
        return code;
    }

    internal async Task<CloudSession?> ExchangeAsync(ExchangeRequest request, CancellationToken ct)
    {
        if (!CloudProtocol.IsSecret(request.Code) || !CloudProtocol.IsSecret(request.Verifier) ||
            request.Device is not { DeviceId: var id, Name: { Length: > 0 and <= 100 } name,
                Platform: "Windows" or "macOS" } device || id == Guid.Empty ||
            string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) ||
            !TryDecode(device.PublicKey, 160, out var key) ||
            !Verify(key, request.Signature, CloudProtocol.Proof("exchange", id, request.Code))) return null;

        await using var connection = await db.ConnectAsync(ct);
        await using var transaction = await db.LockAccountAsync(connection, ct);
        await using var code = db.Command(connection, """
            DELETE FROM login_code WHERE owner=@owner AND hash=@hash AND challenge=@challenge AND expires>now()
            RETURNING login
            """, ("hash", Hash(request.Code)), ("challenge", CloudProtocol.Challenge(request.Verifier)));
        code.Transaction = transaction;
        if (await code.ExecuteScalarAsync(ct) is not string login) return null;

        var session = NewSession(login);
        await using var update = db.Command(connection, """
            INSERT INTO device (owner,id,public_key,name,platform,login,access_hash,access_until,refresh_hash,refresh_until,last_seen)
            VALUES (@owner,@id,@key,@name,@platform,@login,@access,@accessUntil,@refresh,@refreshUntil,now())
            ON CONFLICT (owner,id) DO UPDATE SET
              name=EXCLUDED.name, platform=EXCLUDED.platform, login=EXCLUDED.login, revoked=false,
              access_hash=EXCLUDED.access_hash,access_until=EXCLUDED.access_until,
              refresh_hash=EXCLUDED.refresh_hash,refresh_until=EXCLUDED.refresh_until,last_seen=now()
            WHERE device.public_key=EXCLUDED.public_key
            """, ("id", id), ("key", key), ("name", name), ("platform", device.Platform), ("login", login),
            ("access", Hash(session.AccessToken)), ("accessUntil", session.AccessExpires),
            ("refresh", Hash(session.RefreshToken)), ("refreshUntil", DateTimeOffset.UtcNow + RefreshLifetime));
        if (await update.ExecuteNonQueryAsync(ct) != 1) return null;
        await transaction.CommitAsync(ct);
        return session;
    }

    internal async Task<CloudSession?> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        if (request.DeviceId == Guid.Empty || !CloudProtocol.IsSecret(request.RefreshToken)) return null;
        var hash = Hash(request.RefreshToken);
        await using var connection = await db.ConnectAsync(ct);
        await using var transaction = await db.LockAccountAsync(connection, ct);
        string login;
        await using (var read = db.Command(connection, """
            SELECT public_key,login FROM device
            WHERE owner=@owner AND id=@id AND refresh_hash=@hash AND refresh_until>now() AND NOT revoked
            """, ("id", request.DeviceId), ("hash", hash)))
        {
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) ||
                !Verify(reader.GetFieldValue<byte[]>(0), request.Signature,
                    CloudProtocol.Proof("refresh", request.DeviceId, request.RefreshToken))) return null;
            login = reader.GetString(1);
        }
        // Retain the current refresh token so a lost HTTP response can be retried safely.
        var session = NewSession(login) with { RefreshToken = request.RefreshToken };
        await using var update = db.Command(connection, """
            UPDATE device SET access_hash=@access, access_until=@until, last_seen=now()
            WHERE owner=@owner AND id=@id
            """, ("id", request.DeviceId), ("access", Hash(session.AccessToken)), ("until", session.AccessExpires));
        await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return session;
    }

    internal async Task<Guid?> AuthenticateAsync(HttpContext context, CancellationToken ct)
    {
        const string bearer = "Bearer ";
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase) ||
            !CloudProtocol.IsSecret(authorization[bearer.Length..])) return null;
        await using var connection = await db.ConnectAsync(ct);
        await using var command = db.Command(connection, """
            UPDATE device SET last_seen=now()
            WHERE owner=@owner AND access_hash=@hash AND access_until>now() AND NOT revoked
            RETURNING id
            """, ("hash", Hash(authorization[bearer.Length..])));
        return await command.ExecuteScalarAsync(ct) as Guid?;
    }

    internal async Task<bool> RevokeAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await db.ConnectAsync(ct);
        await using var transaction = await db.LockAccountAsync(connection, ct);
        await using var command = db.Command(connection, """
            UPDATE device SET revoked=true, access_hash=NULL, refresh_hash=NULL, last_seen=NULL
            WHERE owner=@owner AND id=@id
            """, ("id", id));
        var found = await command.ExecuteNonQueryAsync(ct) == 1;
        await transaction.CommitAsync(ct);
        return found;
    }

    internal async Task<bool> EndSessionAsync(RefreshRequest request, CancellationToken ct)
    {
        if (request.DeviceId == Guid.Empty || !CloudProtocol.IsSecret(request.RefreshToken)) return false;
        await using var connection = await db.ConnectAsync(ct);
        await using var transaction = await db.LockAccountAsync(connection, ct);
        var hash = Hash(request.RefreshToken);
        await using var read = db.Command(connection,
            "SELECT public_key FROM device WHERE owner=@owner AND id=@id AND refresh_hash=@hash",
            ("id", request.DeviceId), ("hash", hash));
        if (await read.ExecuteScalarAsync(ct) is not byte[] key ||
            !Verify(key, request.Signature, CloudProtocol.Proof("signout", request.DeviceId, request.RefreshToken))) return false;
        await using var update = db.Command(connection, """
            UPDATE device SET access_hash=NULL, refresh_hash=NULL, last_seen=NULL
            WHERE owner=@owner AND id=@id
            """, ("id", request.DeviceId));
        await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    internal async Task<List<CloudDevice>> ListAsync(CancellationToken ct)
    {
        await using var connection = await db.ConnectAsync(ct);
        await using var command = db.Command(connection, """
            SELECT id,name,platform,last_seen FROM device
            WHERE owner=@owner AND NOT revoked AND refresh_hash IS NOT NULL AND refresh_until>now()
            ORDER BY name,id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<CloudDevice>();
        while (await reader.ReadAsync(ct))
            result.Add(new CloudDevice(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        return result;
    }

    internal static byte[] Hash(string secret) => SHA256.HashData(Encoding.ASCII.GetBytes(secret));

    private static CloudSession NewSession(string login) => new(CloudProtocol.RandomSecret(),
        DateTimeOffset.UtcNow + AccessLifetime, CloudProtocol.RandomSecret(), login);

    private static bool TryDecode(string? text, int maximum, out byte[] bytes)
    {
        bytes = [];
        if (text is not { Length: > 0 } || text.Length > maximum) return false;
        var buffer = new byte[maximum];
        if (!Convert.TryFromBase64String(text, buffer, out var length)) return false;
        bytes = buffer[..length];
        return true;
    }

    private static bool Verify(byte[] key, string? signature, byte[] data)
    {
        if (!TryDecode(signature, 100, out var decoded)) return false;
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(key, out var length);
            return length == key.Length && verifier.KeySize == 256 &&
                verifier.VerifyData(data, decoded, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException) { return false; }
    }
}
