using System.Buffers.Binary;
using System.Security.Cryptography;
using Tbox.Core.Accounts;

namespace Tbox.Cloud;

internal sealed class Captures(Database db, CloudOptions options)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    internal async Task SaveAsync(Guid id, DateTimeOffset created, byte[] png, CancellationToken ct)
    {
        var cipher = Encrypt(id, png);
        await using var connection = await db.ConnectAsync(ct);
        await using var transaction = await db.LockAccountAsync(connection, ct);
        await using var insert = db.Command(connection, """
            WITH accepted AS (
              INSERT INTO capture_receipt (owner,id,received) VALUES (@owner,@id,now())
              ON CONFLICT DO NOTHING RETURNING owner,id
            )
            INSERT INTO capture (owner,id,created,bytes,encrypted) SELECT owner,id,@created,@bytes,@image FROM accepted
            """, ("id", id), ("created", created), ("bytes", png.Length), ("image", cipher));
        await insert.ExecuteNonQueryAsync(ct);
        await using var prune = db.Command(connection, """
            DELETE FROM capture WHERE owner=@owner AND
            (created<=now()-make_interval(days=>@days) OR id IN (
              SELECT id FROM (
                SELECT id,row_number() OVER ordered AS position,sum(bytes) OVER ordered AS total
                FROM capture WHERE owner=@owner
                WINDOW ordered AS (ORDER BY created DESC,id DESC)
              ) AS quota WHERE position>@count OR total>@size
            ))
            """, ("days", CloudProtocol.HistoryDays), ("count", CloudProtocol.MaximumCaptureCount),
            ("size", CloudProtocol.MaximumHistoryBytes));
        await prune.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    internal async Task PurgeExpiredAsync(CancellationToken ct)
    {
        await using var connection = await db.ConnectAsync(ct);
        await using var command = db.Command(connection,
            """
            DELETE FROM capture WHERE owner=@owner AND created<=now()-make_interval(days=>@days);
            DELETE FROM capture_receipt WHERE owner=@owner AND received<=now()-make_interval(days=>@days+1);
            """, ("days", CloudProtocol.HistoryDays));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal async Task<List<CaptureListing>> ListAsync(CancellationToken ct)
    {
        await using var connection = await db.ConnectAsync(ct);
        await using var command = db.Command(connection, """
            SELECT id,created,bytes FROM capture WHERE owner=@owner AND created>now()-make_interval(days=>@days)
            ORDER BY created DESC,id DESC
            """, ("days", CloudProtocol.HistoryDays));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<CaptureListing>();
        while (await reader.ReadAsync(ct))
            result.Add(new CaptureListing(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt32(2)));
        return result;
    }

    internal async Task<byte[]?> ReadAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await db.ConnectAsync(ct);
        await using var command = db.Command(connection, """
            SELECT encrypted FROM capture WHERE owner=@owner AND id=@id AND created>now()-make_interval(days=>@days)
            """, ("id", id), ("days", CloudProtocol.HistoryDays));
        return await command.ExecuteScalarAsync(ct) is byte[] encrypted ? Decrypt(id, encrypted) : null;
    }

    internal async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await db.ConnectAsync(ct);
        await using var command = db.Command(connection, "DELETE FROM capture WHERE owner=@owner AND id=@id", ("id", id));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static bool IsPng(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 33 || !png.StartsWith(signature) ||
            BinaryPrimitives.ReadInt32BigEndian(png[8..]) != 13 || !png[12..16].SequenceEqual("IHDR"u8))
            return false;
        var width = BinaryPrimitives.ReadInt32BigEndian(png[16..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(png[20..]);
        return width > 0 && height > 0 && (long)width * height <= 40_000_000;
    }

    internal byte[] Encrypt(Guid id, byte[] png)
    {
        var data = new byte[NonceSize + TagSize + png.Length];
        RandomNumberGenerator.Fill(data.AsSpan(0, NonceSize));
        using var aes = new AesGcm(options.CaptureKey, TagSize);
        aes.Encrypt(data.AsSpan(0, NonceSize), png, data.AsSpan(NonceSize + TagSize),
            data.AsSpan(NonceSize, TagSize), AssociatedData(id));
        return data;
    }

    internal byte[] Decrypt(Guid id, byte[] data)
    {
        var png = new byte[data.Length - NonceSize - TagSize];
        using var aes = new AesGcm(options.CaptureKey, TagSize);
        aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize),
            data.AsSpan(NonceSize, TagSize), png, AssociatedData(id));
        return png;
    }

    private byte[] AssociatedData(Guid id)
    {
        var data = new byte[24];
        id.TryWriteBytes(data);
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(16), db.Owner);
        return data;
    }
}
