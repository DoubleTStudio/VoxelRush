using System.Reflection;
using System.Text.Json;
using Npgsql;

namespace VoxelRush.Api;

public sealed record CatalogUnlock(int? Level, bool Store);

/// <param name="ProductId">App Store / Google Play product id of a store item (ECON-002); null for level unlocks.</param>
public sealed record CatalogItem(string Sku, string Kind, string Name, CatalogUnlock Unlock, string? ProductId = null);

public sealed record Catalog(int Version, IReadOnlyList<CatalogItem> Items);

public sealed record EntitlementView(string Sku, string Source, DateTimeOffset GrantedAt);

public sealed record ClaimRequest(string? Sku);

public sealed record GrantEntitlementRequest(string? Sku, string? Source, string? TransactionId);

public sealed record RevokeEntitlementRequest(string? Source, string? TransactionId);

public enum EntitlementOutcome
{
    Granted,
    AlreadyOwned,
    UnknownSku,
    NotUnlocked,
    Invalid,
}

/// <summary>
/// ECON-001: cosmetic-only catalog (MONETIZATION_ECONOMY: core racing, building, destruction and standard vehicles
/// stay free; nothing sold or unlocked changes speed, damage, blocks or checkpoints) and the entitlement ledger.
/// Level unlocks are claimed by the player and checked against server progression; store items only arrive
/// through server grants (verified purchases, ECON-002).
/// </summary>
public sealed class EntitlementStore(NpgsqlDataSource db, ProgressionStore progression)
{
    /// <summary>The only kinds the catalog may contain: all purely visual or audio.</summary>
    public static readonly string[] CosmeticKinds = ["paint", "decal", "trail", "horn", "flag"];

    public static Catalog Load()
    {
        using Stream stream = typeof(EntitlementStore).Assembly.GetManifestResourceStream("VoxelRush.Api.catalog.json")
            ?? throw new InvalidOperationException("catalog.json missing");
        Catalog catalog = JsonSerializer.Deserialize<Catalog>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("catalog.json empty");
        foreach (CatalogItem item in catalog.Items)
        {
            if (!CosmeticKinds.Contains(item.Kind) || (item.Unlock.Level == null) == !item.Unlock.Store || item.Unlock.Store == string.IsNullOrEmpty(item.ProductId))
            {
                throw new InvalidOperationException($"Catalog item {item.Sku} must be cosmetic with exactly one unlock rule (store items with a product id).");
            }
        }

        return catalog;
    }

    public Catalog Catalog { get; } = Load();

    public async Task<IReadOnlyList<EntitlementView>> ListAsync(Guid player, CancellationToken ct)
    {
        var list = new List<EntitlementView>();
        await using NpgsqlCommand cmd = db.CreateCommand(
            "SELECT DISTINCT ON (sku) sku, source, granted_at FROM player_entitlements WHERE player_id = $1 AND status = 'active' ORDER BY sku, granted_at");
        cmd.Parameters.Add(new() { Value = player });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new EntitlementView(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)));
        }

        return list;
    }

    /// <summary>Player-initiated: level unlocks only, checked against backend progression; idempotent.</summary>
    public async Task<EntitlementOutcome> ClaimAsync(Guid player, string? sku, CancellationToken ct)
    {
        CatalogItem? item = Catalog.Items.FirstOrDefault(i => i.Sku == sku);
        if (item == null)
        {
            return EntitlementOutcome.UnknownSku;
        }

        if (item.Unlock.Level is not int level || (await progression.GetAsync(player, ct)).Level < level)
        {
            return EntitlementOutcome.NotUnlocked;
        }

        return await InsertAsync(player, item.Sku, "level", $"{player:N}:{item.Sku}", ct);
    }

    /// <summary>Server-initiated (service token): store items after verified purchase, promos. Idempotent per transaction.</summary>
    public async Task<EntitlementOutcome> GrantAsync(Guid player, GrantEntitlementRequest r, CancellationToken ct)
    {
        if (r.Source is not ("grant" or "purchase") || string.IsNullOrEmpty(r.TransactionId) || r.TransactionId.Length > 128)
        {
            return EntitlementOutcome.Invalid;
        }

        return Catalog.Items.Any(i => i.Sku == r.Sku) ? await InsertAsync(player, r.Sku!, r.Source, r.TransactionId, ct) : EntitlementOutcome.UnknownSku;
    }

    /// <summary>Refunds/chargebacks (ECON-002) revoke the entitlement from that transaction; others for the same SKU remain.</summary>
    public async Task<bool> RevokeAsync(RevokeEntitlementRequest r, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = db.CreateCommand(
            "UPDATE player_entitlements SET status = 'revoked', revoked_at = now() WHERE source = $1 AND transaction_id = $2 AND status = 'active'");
        cmd.Parameters.Add(new() { Value = r.Source ?? string.Empty });
        cmd.Parameters.Add(new() { Value = r.TransactionId ?? string.Empty });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> IsRevokedAsync(string source, string transaction, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = db.CreateCommand("SELECT 1 FROM player_entitlements WHERE source = $1 AND transaction_id = $2 AND status = 'revoked'");
        cmd.Parameters.Add(new() { Value = source });
        cmd.Parameters.Add(new() { Value = transaction });
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private async Task<EntitlementOutcome> InsertAsync(Guid player, string sku, string source, string transaction, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = db.CreateCommand(
            "INSERT INTO player_entitlements (player_id, sku, source, transaction_id) SELECT $1, $2, $3, $4 WHERE EXISTS (SELECT 1 FROM players WHERE id = $1) " +
            "ON CONFLICT (source, transaction_id) DO NOTHING");
        foreach (object v in new object[] { player, sku, source, transaction })
        {
            cmd.Parameters.Add(new() { Value = v });
        }

        if (await cmd.ExecuteNonQueryAsync(ct) == 1)
        {
            return EntitlementOutcome.Granted;
        }

        // Conflict (same transaction) or unknown player.
        await using NpgsqlCommand exists = db.CreateCommand("SELECT 1 FROM player_entitlements WHERE source = $1 AND transaction_id = $2 AND player_id = $3");
        exists.Parameters.Add(new() { Value = source });
        exists.Parameters.Add(new() { Value = transaction });
        exists.Parameters.Add(new() { Value = player });
        return await exists.ExecuteScalarAsync(ct) != null ? EntitlementOutcome.AlreadyOwned : EntitlementOutcome.Invalid;
    }
}
