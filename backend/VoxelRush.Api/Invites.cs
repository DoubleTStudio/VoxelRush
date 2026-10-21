using Npgsql;

namespace VoxelRush.Api;

public sealed record CreateInviteRequest(string? Role, int? ExpiresInHours, int? MaxUses);

public sealed record InviteCreated(string Code, DateTimeOffset ExpiresAt, string Role, int MaxUses);

public sealed record AcceptInviteRequest(string? Code);

public enum InviteOutcome
{
    Accepted,
    /// <summary>Unknown, expired, revoked or used up: indistinguishable to the caller on purpose.</summary>
    Invalid,
    AlreadyOwner,
}

/// <summary>
/// SAVE-004 invitations: owners issue codes (32 random bytes, shown once, stored hashed) with a role, expiry and use
/// limit; accepting one makes the caller a member. An existing member is never downgraded by an invite.
/// </summary>
public sealed class InviteStore(NpgsqlDataSource db, WorldStore worlds, TimeProvider clock)
{
    public async Task<(WorldError, InviteCreated?)> CreateAsync(Guid owner, Guid world, CreateInviteRequest r, CancellationToken ct)
    {
        WorldView? view = await worlds.GetAsync(owner, world, ct);
        if (view == null)
        {
            return (WorldError.NotFound, null);
        }

        if (view.Role != "owner")
        {
            return (WorldError.Forbidden, null);
        }

        string role = r.Role ?? "visitor";
        int hours = r.ExpiresInHours ?? 48, uses = r.MaxUses ?? 1;
        if ((role != "visitor" && role != "editor") || hours is < 1 or > 168 || uses is < 1 or > 50)
        {
            return (WorldError.Invalid, null);
        }

        string code = TokenService.NewRefreshToken();
        DateTimeOffset expires = clock.GetUtcNow().AddHours(hours);
        await using NpgsqlCommand cmd = db.CreateCommand(
            "INSERT INTO world_invites (code_hash, world_id, role, created_by, expires_at, max_uses) VALUES ($1, $2, $3, $4, $5, $6)");
        foreach (object v in new object[] { TokenService.Hash(code), world, role, owner, expires, uses })
        {
            cmd.Parameters.Add(new() { Value = v });
        }

        await cmd.ExecuteNonQueryAsync(ct);
        return (WorldError.None, new InviteCreated(code, expires, role, uses));
    }

    public async Task<(InviteOutcome, Guid World, string? Role)> AcceptAsync(Guid player, string? code, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 128)
        {
            return (InviteOutcome.Invalid, Guid.Empty, null);
        }

        await using NpgsqlConnection c = await db.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await c.BeginTransactionAsync(ct);
        Guid world, owner;
        string role;
        await using (var select = new NpgsqlCommand(
            "SELECT i.world_id, i.role, w.owner_id FROM world_invites i JOIN worlds w ON w.id = i.world_id " +
            "WHERE i.code_hash = $1 AND i.revoked_at IS NULL AND i.expires_at > $2 AND i.uses < i.max_uses AND w.status = 'active' FOR UPDATE OF i", c, tx)
            { Parameters = { new() { Value = TokenService.Hash(code) }, new() { Value = clock.GetUtcNow() } } })
        await using (NpgsqlDataReader r = await select.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct))
            {
                return (InviteOutcome.Invalid, Guid.Empty, null);
            }

            (world, role, owner) = (r.GetGuid(0), r.GetString(1), r.GetGuid(2));
        }

        if (owner == player)
        {
            return (InviteOutcome.AlreadyOwner, world, "owner");
        }

        await using (var use = new NpgsqlCommand("UPDATE world_invites SET uses = uses + 1 WHERE code_hash = $1", c, tx) { Parameters = { new() { Value = TokenService.Hash(code) } } })
        {
            await use.ExecuteNonQueryAsync(ct);
        }

        // Upgrade visitor -> editor if the invite grants more; never downgrade.
        await using (var member = new NpgsqlCommand(
            "INSERT INTO world_members (world_id, player_id, role, invited_by) VALUES ($1, $2, $3, $4) " +
            "ON CONFLICT (world_id, player_id) DO UPDATE SET role = CASE WHEN world_members.role = 'editor' THEN 'editor' ELSE EXCLUDED.role END", c, tx)
            { Parameters = { new() { Value = world }, new() { Value = player }, new() { Value = role }, new() { Value = owner } } })
        {
            await member.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (InviteOutcome.Accepted, world, role);
    }

    public async Task<WorldError> RevokeAllAsync(Guid owner, Guid world, CancellationToken ct)
    {
        WorldView? view = await worlds.GetAsync(owner, world, ct);
        if (view == null)
        {
            return WorldError.NotFound;
        }

        if (view.Role != "owner")
        {
            return WorldError.Forbidden;
        }

        await using NpgsqlCommand cmd = db.CreateCommand("UPDATE world_invites SET revoked_at = $2 WHERE world_id = $1 AND revoked_at IS NULL");
        cmd.Parameters.Add(new() { Value = world });
        cmd.Parameters.Add(new() { Value = clock.GetUtcNow() });
        await cmd.ExecuteNonQueryAsync(ct);
        return WorldError.None;
    }
}
