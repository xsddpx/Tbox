using Tbox.Core.Accounts;

namespace Tbox.Cloud;

internal sealed class Preferences(Database db)
{
    internal async Task<List<CloudPreference>?> InitializeAsync(Dictionary<string, string> initial, CancellationToken ct)
    {
        if (initial.Count != 3 || initial.Any(pair => !CloudProtocol.IsPreference(pair.Key, pair.Value))) return null;
        await using var connection = await db.ConnectAsync(ct);
        await using var transaction = await db.LockAccountAsync(connection, ct);
        foreach (var (key, value) in initial)
        {
            await using var insert = db.Command(connection, """
                INSERT INTO preference (owner,key,value,revision) VALUES (@owner,@key,@value,1)
                ON CONFLICT DO NOTHING
                """, ("key", key), ("value", value));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return await ReadAsync(ct);
    }

    internal async Task<List<CloudPreference>> ReadAsync(CancellationToken ct)
    {
        await using var connection = await db.ConnectAsync(ct);
        await using var command = db.Command(connection, "SELECT key,value,revision FROM preference WHERE owner=@owner");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<CloudPreference>();
        while (await reader.ReadAsync(ct))
            result.Add(new CloudPreference(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return result;
    }

    internal async Task<PreferenceResult?> ChangeAsync(PreferenceEdit edit, CancellationToken ct)
    {
        if (edit.Revision <= 0 || !CloudProtocol.IsPreference(edit.Key, edit.Value)) return null;
        await using var connection = await db.ConnectAsync(ct);
        await using var transaction = await db.LockAccountAsync(connection, ct);
        CloudPreference current;
        await using (var read = db.Command(connection,
            "SELECT value,revision FROM preference WHERE owner=@owner AND key=@key", ("key", edit.Key)))
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            current = new CloudPreference(edit.Key, reader.GetString(0), reader.GetInt64(1));
        }
        // A retried write whose response was lost is already satisfied.
        if (current.Value == edit.Value) return new PreferenceResult(current, false);
        if (current.Revision != edit.Revision) return new PreferenceResult(current, true);
        await using var update = db.Command(connection, """
            UPDATE preference SET value=@value,revision=revision+1 WHERE owner=@owner AND key=@key
            """, ("key", edit.Key), ("value", edit.Value));
        await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new PreferenceResult(current with { Value = edit.Value, Revision = current.Revision + 1 }, false);
    }
}
