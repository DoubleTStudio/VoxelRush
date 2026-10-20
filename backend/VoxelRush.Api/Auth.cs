using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace VoxelRush.Api;

public sealed record TokenPair(Guid PlayerId, string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt);

/// <summary>Short-lived HS256 access tokens and opaque refresh tokens (only their SHA-256 is stored).</summary>
public sealed class TokenService(ApiOptions options, TimeProvider clock)
{
    public const string Issuer = "voxelrush-api";
    public const string Audience = "voxelrush-client";

    public SymmetricSecurityKey SigningKey { get; } = new(options.JwtKeyBytes());

    public (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(Guid playerId)
    {
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset expires = now.AddMinutes(options.AccessTokenMinutes);
        string token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, playerId.ToString())]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }

    public static string NewRefreshToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public enum RefreshOutcome
{
    Ok,
    Invalid,
    Expired,
    /// <summary>A rotated or revoked token came back: likely stolen, the whole login was revoked.</summary>
    Reused,
}

public sealed class AuthStore(NpgsqlDataSource db, TokenService tokens, ApiOptions options, TimeProvider clock)
{
    public async Task<TokenPair> CreateGuestAsync(CancellationToken ct)
    {
        var playerId = Guid.NewGuid();
        await using NpgsqlConnection c = await db.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await c.BeginTransactionAsync(ct);
        await using (var insert = new NpgsqlCommand("INSERT INTO players (id, kind) VALUES ($1, 'guest')", c, tx) { Parameters = { new() { Value = playerId } } })
        {
            await insert.ExecuteNonQueryAsync(ct);
        }

        string refresh = await InsertRefreshAsync(c, tx, playerId, Guid.NewGuid(), ct);
        await tx.CommitAsync(ct);
        (string access, DateTimeOffset expires) = tokens.IssueAccessToken(playerId);
        return new TokenPair(playerId, access, refresh, expires);
    }

    public async Task<(RefreshOutcome Outcome, TokenPair? Tokens)> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        byte[] hash = TokenService.Hash(refreshToken);
        DateTimeOffset now = clock.GetUtcNow();
        await using NpgsqlConnection c = await db.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await c.BeginTransactionAsync(ct);
        Guid player, family;
        DateTimeOffset expires;
        bool used;
        await using (var select = new NpgsqlCommand(
            "SELECT player_id, family_id, expires_at, (rotated_at IS NOT NULL OR revoked_at IS NOT NULL) FROM refresh_tokens WHERE token_hash = $1 FOR UPDATE", c, tx)
            { Parameters = { new() { Value = hash } } })
        await using (NpgsqlDataReader r = await select.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct))
            {
                return (RefreshOutcome.Invalid, null);
            }

            (player, family, expires, used) = (r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2), r.GetBoolean(3));
        }

        if (used)
        {
            await using var revoke = new NpgsqlCommand("UPDATE refresh_tokens SET revoked_at = $2 WHERE family_id = $1 AND revoked_at IS NULL", c, tx)
                { Parameters = { new() { Value = family }, new() { Value = now } } };
            await revoke.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return (RefreshOutcome.Reused, null);
        }

        if (now >= expires)
        {
            return (RefreshOutcome.Expired, null);
        }

        await using (var rotate = new NpgsqlCommand("UPDATE refresh_tokens SET rotated_at = $2 WHERE token_hash = $1", c, tx)
            { Parameters = { new() { Value = hash }, new() { Value = now } } })
        {
            await rotate.ExecuteNonQueryAsync(ct);
        }

        string next = await InsertRefreshAsync(c, tx, player, family, ct);
        await using (var seen = new NpgsqlCommand("UPDATE players SET last_seen_at = $2 WHERE id = $1", c, tx) { Parameters = { new() { Value = player }, new() { Value = now } } })
        {
            await seen.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        (string access, DateTimeOffset accessExpires) = tokens.IssueAccessToken(player);
        return (RefreshOutcome.Ok, new TokenPair(player, access, next, accessExpires));
    }

    private async Task<string> InsertRefreshAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid player, Guid family, CancellationToken ct)
    {
        string token = TokenService.NewRefreshToken();
        await using var insert = new NpgsqlCommand("INSERT INTO refresh_tokens (token_hash, player_id, family_id, expires_at) VALUES ($1, $2, $3, $4)", c, tx)
        {
            Parameters =
            {
                new() { Value = TokenService.Hash(token) },
                new() { Value = player },
                new() { Value = family },
                new() { Value = clock.GetUtcNow().AddDays(options.RefreshTokenDays) },
            },
        };
        await insert.ExecuteNonQueryAsync(ct);
        return token;
    }
}
