using Npgsql;

namespace VoxelRush.Api;

public sealed record RestoreRequest(DateTimeOffset? AsOf);

public sealed record RestoreView(int Epoch, DateTimeOffset AsOf, int Chunks, Guid ActorId, DateTimeOffset CreatedAt);

public sealed record RestorePoint(DateTimeOffset SavedAt, int Chunks);

public sealed record StorageMetrics(int Worlds, long Objects, long StoredBytes, long Restores, decimal EstimatedMonthlyCostUsd, IReadOnlyList<WorldStorage> TopWorlds);

public sealed record WorldStorage(Guid WorldId, long Bytes);

public enum RestoreError
{
    None,
    NotFound,
    Forbidden,
    WorldRunning,
    OutsideRetention,
}

/// <summary>
/// SAVE-009 backups: every committed snapshot inside the retention window is a restore point. A restore (owner only,
/// world stopped) starts a new epoch whose rows point at the objects current at the chosen time, and is audited.
/// Servers see the new epoch in their lease and rebuild their local disk from storage.
/// </summary>
public sealed class BackupStore(NpgsqlDataSource db, ApiOptions options, TimeProvider clock)
{
    public async Task<(RestoreError, RestoreView?)> RestoreAsync(Guid actor, Guid world, DateTimeOffset asOf, CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (asOf > now || asOf < now.AddDays(-options.Snapshots.RetentionDays))
        {
            return (RestoreError.OutsideRetention, null);
        }

        await using NpgsqlConnection c = await db.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await c.BeginTransactionAsync(ct);
        int epoch;
        // Row lock: a lease acquire waits for the restore (and then sees the new epoch), or the restore sees its lease.
        await using (var world_ = new NpgsqlCommand("SELECT owner_id, restore_epoch FROM worlds WHERE id = $1 AND status = 'active' FOR UPDATE", c, tx) { Parameters = { new() { Value = world } } })
        await using (NpgsqlDataReader r = await world_.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct))
            {
                return (RestoreError.NotFound, null);
            }

            if (r.GetGuid(0) != actor)
            {
                return (RestoreError.Forbidden, null);
            }

            epoch = r.GetInt32(1);
        }

        await using (var live = new NpgsqlCommand("SELECT 1 FROM world_leases WHERE world_id = $1 AND NOT released AND expires_at > $2", c, tx)
            { Parameters = { new() { Value = world }, new() { Value = now } } })
        {
            if (await live.ExecuteScalarAsync(ct) != null)
            {
                return (RestoreError.WorldRunning, null);
            }
        }

        // The state at asOf is the epoch current then (the last restore before it), each chunk's newest row up to asOf.
        int chunks;
        await using (var copy = new NpgsqlCommand(
            "INSERT INTO chunk_snapshots (world_id, epoch, chunk_x, chunk_y, chunk_z, revision, storage_key, sha256, size_bytes, status, committed_at) " +
            "SELECT DISTINCT ON (chunk_x, chunk_y, chunk_z) world_id, $3, chunk_x, chunk_y, chunk_z, revision, storage_key, sha256, size_bytes, 'committed', $4 " +
            "FROM chunk_snapshots WHERE world_id = $1 AND status = 'committed' AND committed_at <= $2 " +
            "AND epoch = (SELECT coalesce(max(epoch), 0) FROM world_restores WHERE world_id = $1 AND created_at <= $2) " +
            "ORDER BY chunk_x, chunk_y, chunk_z, committed_at DESC, revision DESC", c, tx)
            { Parameters = { new() { Value = world }, new() { Value = asOf }, new() { Value = epoch + 1 }, new() { Value = now } } })
        {
            chunks = await copy.ExecuteNonQueryAsync(ct);
        }

        await using (var bump = new NpgsqlCommand("UPDATE worlds SET restore_epoch = $2 WHERE id = $1", c, tx) { Parameters = { new() { Value = world }, new() { Value = epoch + 1 } } })
        {
            await bump.ExecuteNonQueryAsync(ct);
        }

        await using (var audit = new NpgsqlCommand("INSERT INTO world_restores (world_id, actor_id, as_of, epoch, chunks, created_at) VALUES ($1, $2, $3, $4, $5, $6)", c, tx)
            { Parameters = { new() { Value = world }, new() { Value = actor }, new() { Value = asOf }, new() { Value = epoch + 1 }, new() { Value = chunks }, new() { Value = now } } })
        {
            await audit.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (RestoreError.None, new RestoreView(epoch + 1, asOf, chunks, actor, now));
    }

    public async Task<IReadOnlyList<RestoreView>> RestoresAsync(Guid world, CancellationToken ct)
    {
        var list = new List<RestoreView>();
        await using NpgsqlCommand cmd = db.CreateCommand("SELECT epoch, as_of, chunks, actor_id, created_at FROM world_restores WHERE world_id = $1 ORDER BY created_at DESC, id DESC LIMIT 100");
        cmd.Parameters.Add(new() { Value = world });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new RestoreView(r.GetInt32(0), r.GetFieldValue<DateTimeOffset>(1), r.GetInt32(2), r.GetGuid(3), r.GetFieldValue<DateTimeOffset>(4)));
        }

        return list;
    }

    /// <summary>
    /// Saves inside the retention window, newest first. Commits less than 10 s apart are one save (a shutdown uploads
    /// several chunks); its time is the last commit, so restoring to it includes every chunk of that save.
    /// </summary>
    public async Task<IReadOnlyList<RestorePoint>> RestorePointsAsync(Guid world, CancellationToken ct)
    {
        var list = new List<RestorePoint>();
        await using NpgsqlCommand cmd = db.CreateCommand(
            "SELECT max(committed_at), count(*) FROM (" +
            "  SELECT committed_at, sum(gap) OVER (ORDER BY committed_at) save FROM (" +
            "    SELECT committed_at, CASE WHEN committed_at - lag(committed_at) OVER (ORDER BY committed_at) > interval '10 seconds' THEN 1 ELSE 0 END gap " +
            "    FROM chunk_snapshots WHERE world_id = $1 AND status = 'committed' AND committed_at >= $2) commits) saves " +
            "GROUP BY save ORDER BY 1 DESC LIMIT 50");
        cmd.Parameters.Add(new() { Value = world });
        cmd.Parameters.Add(new() { Value = clock.GetUtcNow().AddDays(-options.Snapshots.RetentionDays) });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new RestorePoint(r.GetFieldValue<DateTimeOffset>(0), (int)r.GetInt64(1)));
        }

        return list;
    }

    public async Task<StorageMetrics> MetricsAsync(CancellationToken ct)
    {
        await using NpgsqlCommand cmd = db.CreateCommand(
            "WITH objects AS (SELECT DISTINCT world_id, storage_key, size_bytes FROM chunk_snapshots WHERE status = 'committed') " +
            "SELECT (SELECT count(DISTINCT world_id) FROM objects), (SELECT count(*) FROM objects), (SELECT coalesce(sum(size_bytes), 0) FROM objects), (SELECT count(*) FROM world_restores)");
        long objects, bytes, restores;
        int worlds;
        await using (NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            (worlds, objects, bytes, restores) = ((int)r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
        }

        var top = new List<WorldStorage>();
        await using NpgsqlCommand topCmd = db.CreateCommand(
            "SELECT world_id, sum(size_bytes) FROM (SELECT DISTINCT world_id, storage_key, size_bytes FROM chunk_snapshots WHERE status = 'committed') o " +
            "GROUP BY world_id ORDER BY 2 DESC LIMIT 10");
        await using (NpgsqlDataReader r = await topCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                top.Add(new WorldStorage(r.GetGuid(0), r.GetInt64(1)));
            }
        }

        decimal cost = Math.Round(bytes / 1_000_000_000m * options.Snapshots.CostPerGbMonthUsd, 6);
        return new StorageMetrics(worlds, objects, bytes, restores, cost, top);
    }
}
