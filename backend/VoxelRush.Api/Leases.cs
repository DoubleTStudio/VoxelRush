using Npgsql;

namespace VoxelRush.Api;

public sealed record LeaseRequest(string? Holder, int? TtlSeconds, long? Fencing);

public sealed record LeaseView(string Holder, long Fencing, DateTimeOffset ExpiresAt);

public enum LeaseOutcome
{
    Granted,
    Held,
    Lost,
    NotFound,
    Invalid,
}

/// <summary>
/// SAVE-005 world leases (SAVE_GAME world lifecycle: "acquire per-world lease … lease/fencing token required").
/// Acquire succeeds when the world has no live lease (none, released or expired) or the caller already holds it;
/// each new holder gets fencing + 1. Renew and release require the current holder and fencing number.
/// </summary>
public sealed class LeaseStore(NpgsqlDataSource db, TimeProvider clock)
{
    public const int MinTtlSeconds = 10;
    public const int MaxTtlSeconds = 300;

    public async Task<(LeaseOutcome, LeaseView?)> AcquireAsync(Guid world, LeaseRequest r, CancellationToken ct)
    {
        if (!ValidHolder(r.Holder) || r.TtlSeconds is not int ttl || ttl < MinTtlSeconds || ttl > MaxTtlSeconds)
        {
            return (LeaseOutcome.Invalid, null);
        }

        DateTimeOffset now = clock.GetUtcNow();
        await using NpgsqlCommand cmd = db.CreateCommand(
            // Atomic: insert, or take over a dead lease (new fencing), or extend our own (same fencing).
            "INSERT INTO world_leases (world_id, holder, fencing, expires_at) SELECT $1, $2, 1, $3 WHERE EXISTS (SELECT 1 FROM worlds WHERE id = $1 AND status = 'active') " +
            "ON CONFLICT (world_id) DO UPDATE SET " +
            "fencing = CASE WHEN world_leases.holder = EXCLUDED.holder AND NOT world_leases.released AND world_leases.expires_at > $4 THEN world_leases.fencing ELSE world_leases.fencing + 1 END, " +
            "holder = EXCLUDED.holder, expires_at = EXCLUDED.expires_at, released = false " +
            "WHERE world_leases.released OR world_leases.expires_at <= $4 OR world_leases.holder = EXCLUDED.holder " +
            "RETURNING holder, fencing, expires_at");
        cmd.Parameters.Add(new() { Value = world });
        cmd.Parameters.Add(new() { Value = r.Holder! });
        cmd.Parameters.Add(new() { Value = now.AddSeconds(ttl) });
        cmd.Parameters.Add(new() { Value = now });
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                return (LeaseOutcome.Granted, Read(reader));
            }
        }

        LeaseView? current = await CurrentAsync(world, ct);
        return current == null ? (LeaseOutcome.NotFound, null) : (LeaseOutcome.Held, current);
    }

    public async Task<(LeaseOutcome, LeaseView?)> RenewAsync(Guid world, LeaseRequest r, CancellationToken ct)
    {
        if (!ValidHolder(r.Holder) || r.Fencing is not long fencing || r.TtlSeconds is not int ttl || ttl < MinTtlSeconds || ttl > MaxTtlSeconds)
        {
            return (LeaseOutcome.Invalid, null);
        }

        DateTimeOffset now = clock.GetUtcNow();
        await using NpgsqlCommand cmd = db.CreateCommand(
            "UPDATE world_leases SET expires_at = $4 WHERE world_id = $1 AND holder = $2 AND fencing = $3 AND NOT released AND expires_at > $5 " +
            "RETURNING holder, fencing, expires_at");
        foreach (object v in new object[] { world, r.Holder!, fencing, now.AddSeconds(ttl), now })
        {
            cmd.Parameters.Add(new() { Value = v });
        }

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        // An expired lease is lost even if nobody took it yet: the holder must re-acquire (and gets a new fencing number).
        return await reader.ReadAsync(ct) ? (LeaseOutcome.Granted, Read(reader)) : (LeaseOutcome.Lost, null);
    }

    public async Task<LeaseOutcome> ReleaseAsync(Guid world, LeaseRequest r, CancellationToken ct)
    {
        if (!ValidHolder(r.Holder) || r.Fencing is not long fencing)
        {
            return LeaseOutcome.Invalid;
        }

        await using NpgsqlCommand cmd = db.CreateCommand("UPDATE world_leases SET released = true WHERE world_id = $1 AND holder = $2 AND fencing = $3 AND NOT released");
        cmd.Parameters.Add(new() { Value = world });
        cmd.Parameters.Add(new() { Value = r.Holder! });
        cmd.Parameters.Add(new() { Value = fencing });
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? LeaseOutcome.Granted : LeaseOutcome.Lost;
    }

    /// <summary>True when (holder, fencing) is the world's live lease; world-scoped server writes must pass this.</summary>
    public async Task<bool> IsCurrentAsync(Guid world, string? holder, long? fencing, CancellationToken ct)
    {
        if (!ValidHolder(holder) || fencing is not long f)
        {
            return false;
        }

        await using NpgsqlCommand cmd = db.CreateCommand("SELECT 1 FROM world_leases WHERE world_id = $1 AND holder = $2 AND fencing = $3 AND NOT released AND expires_at > $4");
        cmd.Parameters.Add(new() { Value = world });
        cmd.Parameters.Add(new() { Value = holder! });
        cmd.Parameters.Add(new() { Value = f });
        cmd.Parameters.Add(new() { Value = clock.GetUtcNow() });
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private async Task<LeaseView?> CurrentAsync(Guid world, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = db.CreateCommand("SELECT holder, fencing, expires_at FROM world_leases WHERE world_id = $1");
        cmd.Parameters.Add(new() { Value = world });
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    private static bool ValidHolder(string? holder) => !string.IsNullOrWhiteSpace(holder) && holder.Length <= 128;

    private static LeaseView Read(NpgsqlDataReader r) => new(r.GetString(0), r.GetInt64(1), r.GetFieldValue<DateTimeOffset>(2));
}
