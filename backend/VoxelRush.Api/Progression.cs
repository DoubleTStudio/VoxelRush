using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace VoxelRush.Api;

public sealed record ProgressView(long Xp, int Level, long Version);

public sealed record SettingsView(JsonElement Settings, long Version);

public sealed record GrantRequest(string? GrantKey, string? Kind, int Amount, string? Source);

public sealed record GrantResult(bool Duplicate, ProgressView Progress);

public sealed record LinkCode(string Code, DateTimeOffset ExpiresAt);

public enum SettingsOutcome
{
    Saved,
    VersionConflict,
    Invalid,
}

/// <summary>
/// SAVE-006: backend-authoritative progression. XP only changes through idempotent grants from trusted game servers
/// (one ledger row per grant key, in the same transaction as the XP change), never from clients.
/// </summary>
public sealed class ProgressionStore(NpgsqlDataSource db, TimeProvider clock)
{
    public const int MaxSettingsBytes = 8192;

    /// <summary>Level from XP: 1 + floor(sqrt(xp / 100)) (100 XP → 2, 400 → 3, 900 → 4 …).</summary>
    public static int LevelFor(long xp) => 1 + (int)Math.Floor(Math.Sqrt(xp / 100.0));

    public async Task<ProgressView> GetAsync(Guid player, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = db.CreateCommand("SELECT xp, level, version FROM player_progress WHERE player_id = $1");
        cmd.Parameters.Add(new() { Value = player });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ProgressView(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2)) : new ProgressView(0, 1, 0);
    }

    public async Task<(bool Ok, GrantResult? Result)> GrantAsync(Guid player, GrantRequest g, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(g.GrantKey) || g.GrantKey.Length > 128 || g.Kind != "xp" || g.Amount is <= 0 or > 100_000
            || string.IsNullOrEmpty(g.Source) || g.Source.Length > 64)
        {
            return (false, null);
        }

        await using NpgsqlConnection c = await db.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await c.BeginTransactionAsync(ct);
        await using (var exists = new NpgsqlCommand("SELECT 1 FROM players WHERE id = $1", c, tx) { Parameters = { new() { Value = player } } })
        {
            if (await exists.ExecuteScalarAsync(ct) == null)
            {
                return (false, null);
            }
        }

        int inserted;
        await using (var ledger = new NpgsqlCommand(
            "INSERT INTO reward_grants (grant_key, player_id, kind, amount, source) VALUES ($1, $2, $3, $4, $5) ON CONFLICT (grant_key) DO NOTHING", c, tx)
            { Parameters = { new() { Value = g.GrantKey }, new() { Value = player }, new() { Value = g.Kind }, new() { Value = g.Amount }, new() { Value = g.Source } } })
        {
            inserted = await ledger.ExecuteNonQueryAsync(ct);
        }

        if (inserted == 1)
        {
            // Same transaction as the ledger row: a grant is applied exactly when its key is recorded.
            await using var apply = new NpgsqlCommand(
                "INSERT INTO player_progress (player_id, xp, level, version, updated_at) VALUES ($1, $2, $3, 1, $4) " +
                "ON CONFLICT (player_id) DO UPDATE SET xp = player_progress.xp + $2, version = player_progress.version + 1, updated_at = $4", c, tx)
                { Parameters = { new() { Value = player }, new() { Value = (long)g.Amount }, new() { Value = LevelFor(g.Amount) }, new() { Value = clock.GetUtcNow() } } };
            await apply.ExecuteNonQueryAsync(ct);
            await using var level = new NpgsqlCommand("UPDATE player_progress SET level = 1 + floor(sqrt(xp / 100.0))::int WHERE player_id = $1", c, tx)
                { Parameters = { new() { Value = player } } };
            await level.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (true, new GrantResult(inserted == 0, await GetAsync(player, ct)));
    }

    public async Task<SettingsView> GetSettingsAsync(Guid player, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = db.CreateCommand("SELECT settings::text, version FROM player_settings WHERE player_id = $1");
        cmd.Parameters.Add(new() { Value = player });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return new SettingsView(JsonDocument.Parse("{}").RootElement, 0);
        }

        return new SettingsView(JsonDocument.Parse(r.GetString(0)).RootElement.Clone(), r.GetInt64(1));
    }

    /// <summary>Saves only if <paramref name="expectedVersion"/> matches (0 = none yet): a stale device can't overwrite a newer one.</summary>
    public async Task<(SettingsOutcome, long Version)> PutSettingsAsync(Guid player, JsonElement settings, long expectedVersion, CancellationToken ct)
    {
        string json = settings.GetRawText();
        if (settings.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(json) > MaxSettingsBytes)
        {
            return (SettingsOutcome.Invalid, 0);
        }

        await using NpgsqlCommand cmd = db.CreateCommand(expectedVersion == 0
            ? "INSERT INTO player_settings (player_id, settings) VALUES ($1, $2::jsonb) ON CONFLICT (player_id) DO NOTHING RETURNING version"
            : "UPDATE player_settings SET settings = $2::jsonb, version = version + 1, updated_at = now() WHERE player_id = $1 AND version = $3 RETURNING version");
        cmd.Parameters.Add(new() { Value = player });
        cmd.Parameters.Add(new() { Value = json });
        cmd.Parameters.Add(new() { Value = expectedVersion });
        object? version = await cmd.ExecuteScalarAsync(ct);
        return version == null ? (SettingsOutcome.VersionConflict, 0) : (SettingsOutcome.Saved, (long)version);
    }

    /// <summary>A one-time 10-minute code a second device redeems to sign in as this player.</summary>
    public async Task<LinkCode> CreateLinkCodeAsync(Guid player, CancellationToken ct)
    {
        // 10 characters from an unambiguous alphabet: easy to type, 50 bits, single use, rate-limited redemption.
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[10];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        string code = new string(chars);
        DateTimeOffset expires = clock.GetUtcNow().AddMinutes(10);
        await using NpgsqlCommand cmd = db.CreateCommand("INSERT INTO device_link_codes (code_hash, player_id, expires_at) VALUES ($1, $2, $3)");
        cmd.Parameters.Add(new() { Value = TokenService.Hash(code) });
        cmd.Parameters.Add(new() { Value = player });
        cmd.Parameters.Add(new() { Value = expires });
        await cmd.ExecuteNonQueryAsync(ct);
        return new LinkCode(code, expires);
    }

    /// <summary>Consumes a link code; returns the player it belongs to, or null.</summary>
    public async Task<Guid?> RedeemLinkCodeAsync(string? code, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 32)
        {
            return null;
        }

        await using NpgsqlCommand cmd = db.CreateCommand(
            "UPDATE device_link_codes SET used_at = $2 WHERE code_hash = $1 AND used_at IS NULL AND expires_at > $2 RETURNING player_id");
        cmd.Parameters.Add(new() { Value = TokenService.Hash(code.Trim().ToUpperInvariant()) });
        cmd.Parameters.Add(new() { Value = clock.GetUtcNow() });
        return await cmd.ExecuteScalarAsync(ct) is Guid player ? player : null;
    }
}
