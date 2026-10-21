using System.Text.RegularExpressions;
using Npgsql;
using VoxelRush.Core.Networking;

namespace VoxelRush.Api;

public sealed record WorldView(Guid Id, string Name, string Visibility, long Seed, int GeneratorVersion, int SchemaVersion, string Role, DateTimeOffset CreatedAt, DateTimeOffset? LastSavedAt);

public sealed record CreateWorldRequest(string? Name, string? Visibility, long? Seed);

public sealed record UpdateWorldRequest(string? Name, string? Visibility);

public sealed record AddMemberRequest(Guid PlayerId, string? Role);

public sealed record JoinResponse(string Ticket, string Endpoint, DateTimeOffset ExpiresAt, string Role);

/// <summary>202 body while the world's server boots; the client calls join again after the delay.</summary>
public sealed record JoinPending(string Status, int RetryAfterSeconds);

public enum WorldError
{
    None,
    NotFound,
    Forbidden,
    Invalid,
    QuotaExceeded,
}

/// <summary>World metadata, membership and join decisions. A world a player may not see is reported as not found.</summary>
public sealed partial class WorldStore(NpgsqlDataSource db, ApiOptions options, TimeProvider clock, IWorldHostAllocator hosts)
{
    private static readonly string[] Visibilities = ["private", "friends", "public"];

    [GeneratedRegex(@"^[\p{L}\p{N} _\-']{1,40}$")]
    private static partial Regex NamePattern();

    public static bool ValidName(string? name) => name != null && name.Trim().Length == name.Length && NamePattern().IsMatch(name);

    public static bool ValidVisibility(string? v) => v != null && Visibilities.Contains(v);

    public async Task<(WorldError, WorldView?)> CreateAsync(Guid owner, CreateWorldRequest request, CancellationToken ct)
    {
        string visibility = request.Visibility ?? "private";
        if (!ValidName(request.Name) || !ValidVisibility(visibility))
        {
            return (WorldError.Invalid, null);
        }

        await using NpgsqlConnection c = await db.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        // SAVE-009: a world_quotas row overrides the default.
        await using (var count = new NpgsqlCommand(
            "SELECT count(*) FROM worlds WHERE owner_id = $1 AND status = 'active' HAVING count(*) >= coalesce((SELECT max_worlds FROM world_quotas WHERE owner_id = $1), $2)", c, tx)
            { Parameters = { new() { Value = owner }, new() { Value = options.MaxWorldsPerOwner } } })
        {
            if (await count.ExecuteScalarAsync(ct) != null)
            {
                return (WorldError.QuotaExceeded, null);
            }
        }

        var id = Guid.NewGuid();
        long seed = request.Seed ?? BitConverter.ToInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO worlds (id, owner_id, name, seed, generator_version, schema_version, visibility) VALUES ($1, $2, $3, $4, $5, $6, $7)", c, tx)
            {
                Parameters =
                {
                    new() { Value = id }, new() { Value = owner }, new() { Value = request.Name! }, new() { Value = seed },
                    new() { Value = options.GeneratorVersion }, new() { Value = options.SchemaVersion }, new() { Value = visibility },
                },
            })
        {
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (WorldError.None, await GetAsync(owner, id, ct));
    }

    public async Task<IReadOnlyList<WorldView>> MineAsync(Guid player, CancellationToken ct)
    {
        var list = new List<WorldView>();
        await using NpgsqlCommand cmd = db.CreateCommand(Select + " WHERE w.status = 'active' AND (w.owner_id = $1 OR m.player_id IS NOT NULL) ORDER BY w.created_at");
        cmd.Parameters.Add(new() { Value = player });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(Read(r));
        }

        return list;
    }

    /// <summary>Null when the world doesn't exist or isn't visible to this player.</summary>
    public async Task<WorldView?> GetAsync(Guid player, Guid world, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = db.CreateCommand(Select + " WHERE w.id = $2 AND w.status = 'active'");
        cmd.Parameters.Add(new() { Value = player });
        cmd.Parameters.Add(new() { Value = world });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return null;
        }

        WorldView view = Read(r);
        return view.Role != "none" ? view : null;
    }

    public async Task<WorldError> UpdateAsync(Guid player, Guid world, UpdateWorldRequest request, CancellationToken ct)
    {
        WorldView? view = await GetAsync(player, world, ct);
        if (view == null)
        {
            return WorldError.NotFound;
        }

        if (view.Role != "owner")
        {
            return WorldError.Forbidden;
        }

        if ((request.Name != null && !ValidName(request.Name)) || (request.Visibility != null && !ValidVisibility(request.Visibility)))
        {
            return WorldError.Invalid;
        }

        await using NpgsqlCommand cmd = db.CreateCommand("UPDATE worlds SET name = COALESCE($2, name), visibility = COALESCE($3, visibility) WHERE id = $1 AND owner_id = $4");
        cmd.Parameters.Add(new() { Value = world });
        cmd.Parameters.Add(new() { Value = (object?)request.Name ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = (object?)request.Visibility ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = player });
        await cmd.ExecuteNonQueryAsync(ct);
        return WorldError.None;
    }

    public async Task<WorldError> AddMemberAsync(Guid owner, Guid world, AddMemberRequest request, CancellationToken ct)
    {
        // Access first: a stranger learns nothing about the world from payload errors.
        WorldView? view = await GetAsync(owner, world, ct);
        if (view == null)
        {
            return WorldError.NotFound;
        }

        if (view.Role != "owner")
        {
            return WorldError.Forbidden;
        }

        string role = request.Role ?? "visitor";
        if ((role != "visitor" && role != "editor") || request.PlayerId == Guid.Empty || request.PlayerId == owner)
        {
            return WorldError.Invalid;
        }

        await using NpgsqlCommand cmd = db.CreateCommand(
            "INSERT INTO world_members (world_id, player_id, role, invited_by) SELECT $1, $2, $3, $4 WHERE EXISTS (SELECT 1 FROM players WHERE id = $2) " +
            "ON CONFLICT (world_id, player_id) DO UPDATE SET role = EXCLUDED.role");
        cmd.Parameters.Add(new() { Value = world });
        cmd.Parameters.Add(new() { Value = request.PlayerId });
        cmd.Parameters.Add(new() { Value = role });
        cmd.Parameters.Add(new() { Value = owner });
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? WorldError.None : WorldError.Invalid;
    }

    /// <summary>Soft delete (owner only): the world disappears from lists and joins; data stays for the retention period.</summary>
    public async Task<WorldError> DeleteAsync(Guid owner, Guid world, CancellationToken ct)
    {
        WorldView? view = await GetAsync(owner, world, ct);
        if (view == null)
        {
            return WorldError.NotFound;
        }

        if (view.Role != "owner")
        {
            return WorldError.Forbidden;
        }

        await using NpgsqlCommand cmd = db.CreateCommand("UPDATE worlds SET status = 'deleted' WHERE id = $1 AND owner_id = $2");
        cmd.Parameters.Add(new() { Value = world });
        cmd.Parameters.Add(new() { Value = owner });
        await cmd.ExecuteNonQueryAsync(ct);
        return WorldError.None;
    }

    /// <summary>Public worlds for discovery, newest first (paged by creation time).</summary>
    public async Task<IReadOnlyList<WorldView>> PublicAsync(Guid player, int limit, DateTimeOffset? before, CancellationToken ct)
    {
        var list = new List<WorldView>();
        await using NpgsqlCommand cmd = db.CreateCommand(Select + " WHERE w.status = 'active' AND w.visibility = 'public' AND ($2::timestamptz IS NULL OR w.created_at < $2) ORDER BY w.created_at DESC LIMIT $3");
        cmd.Parameters.Add(new() { Value = player });
        cmd.Parameters.Add(new() { Value = (object?)before ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new() { Value = Math.Clamp(limit, 1, 50) });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(Read(r));
        }

        return list;
    }

    public async Task<WorldError> RemoveMemberAsync(Guid owner, Guid world, Guid member, CancellationToken ct)
    {
        WorldView? view = await GetAsync(owner, world, ct);
        if (view == null)
        {
            return WorldError.NotFound;
        }

        if (view.Role != "owner")
        {
            return WorldError.Forbidden;
        }

        await using NpgsqlCommand cmd = db.CreateCommand("DELETE FROM world_members WHERE world_id = $1 AND player_id = $2");
        cmd.Parameters.Add(new() { Value = world });
        cmd.Parameters.Add(new() { Value = member });
        await cmd.ExecuteNonQueryAsync(ct);
        return WorldError.None;
    }

    /// <summary>
    /// A signed ticket for the player's role in this world (owner, member role, or visitor of a public world),
    /// once a server hosts it; otherwise the allocation status without a ticket.
    /// </summary>
    public async Task<(WorldError, AllocationResult?, JoinResponse?)> JoinAsync(Guid player, Guid world, CancellationToken ct)
    {
        WorldView? view = await GetAsync(player, world, ct);
        if (view == null)
        {
            return (WorldError.NotFound, null, null);
        }

        // Access is checked first: strangers must not be able to start servers.
        AllocationResult host = await hosts.EnsureAsync(world, ct);
        if (host.Status != AllocationStatus.Ready)
        {
            return (WorldError.None, host, null);
        }

        WorldAccessRole role = view.Role switch
        {
            "owner" => WorldAccessRole.Owner,
            "editor" => WorldAccessRole.Editor,
            _ => WorldAccessRole.Visitor,
        };
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var ticket = new JoinTicket(Guid.NewGuid(), player, world, role, now, now + options.TicketSeconds);
        byte[] signed = ticket.Sign(options.TicketKeyBytes());
        return (WorldError.None, host, new JoinResponse(TokenService.Base64Url(signed), host.Endpoint!, DateTimeOffset.FromUnixTimeSeconds(ticket.ExpiresAt), view.Role));
    }

    // Role: owner, the member's role, 'visitor' for public worlds, otherwise 'none' (invisible).
    private const string Select =
        "SELECT w.id, w.name, w.visibility, w.seed, w.generator_version, w.schema_version, " +
        "CASE WHEN w.owner_id = $1 THEN 'owner' WHEN m.role IS NOT NULL THEN m.role WHEN w.visibility = 'public' THEN 'visitor' ELSE 'none' END, " +
        "w.created_at, w.last_saved_at FROM worlds w LEFT JOIN world_members m ON m.world_id = w.id AND m.player_id = $1 " +
        "AND (m.expires_at IS NULL OR m.expires_at > now())";

    private static WorldView Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4), r.GetInt32(5), r.GetString(6),
        r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8));
}
