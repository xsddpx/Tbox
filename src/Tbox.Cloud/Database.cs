using Npgsql;

namespace Tbox.Cloud;

internal sealed class Database(NpgsqlDataSource source, CloudOptions options)
{
    public long Owner => options.OwnerId;

    internal async Task InitializeAsync(CancellationToken ct)
    {
        // This bootstrap migration is transactional, including when two replicas start together.
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(-314159);
            CREATE TABLE IF NOT EXISTS cloud_schema (version integer PRIMARY KEY);
            INSERT INTO cloud_schema VALUES (1) ON CONFLICT DO NOTHING;
            CREATE TABLE IF NOT EXISTS device (
              owner bigint NOT NULL, id uuid NOT NULL, public_key bytea NOT NULL,
              name text NOT NULL, platform text NOT NULL, login text NOT NULL,
              last_seen timestamptz, access_hash bytea, access_until timestamptz,
              refresh_hash bytea, refresh_until timestamptz, revoked boolean NOT NULL DEFAULT false,
              PRIMARY KEY (owner, id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS device_access ON device (owner, access_hash) WHERE access_hash IS NOT NULL;
            CREATE TABLE IF NOT EXISTS login_code (
              owner bigint NOT NULL, hash bytea NOT NULL, challenge text NOT NULL,
              login text NOT NULL, expires timestamptz NOT NULL, PRIMARY KEY (owner, hash)
            );
            CREATE TABLE IF NOT EXISTS preference (
              owner bigint NOT NULL, key text NOT NULL, value text NOT NULL, revision bigint NOT NULL,
              PRIMARY KEY (owner, key)
            );
            CREATE TABLE IF NOT EXISTS capture (
              owner bigint NOT NULL, id uuid NOT NULL, created timestamptz NOT NULL,
              bytes integer NOT NULL, encrypted bytea NOT NULL, PRIMARY KEY (owner, id)
            );
            CREATE INDEX IF NOT EXISTS capture_created ON capture (owner, created DESC, id DESC);
            CREATE TABLE IF NOT EXISTS capture_receipt (
              owner bigint NOT NULL, id uuid NOT NULL, received timestamptz NOT NULL,
              PRIMARY KEY (owner, id)
            );
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    internal async Task<NpgsqlConnection> ConnectAsync(CancellationToken ct) => await source.OpenConnectionAsync(ct);

    internal NpgsqlCommand Command(NpgsqlConnection connection, string sql, params (string Key, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("owner", Owner);
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        return command;
    }

    internal async Task<NpgsqlTransaction> LockAccountAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = Command(connection, "SELECT pg_advisory_xact_lock(@owner)");
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(ct);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
