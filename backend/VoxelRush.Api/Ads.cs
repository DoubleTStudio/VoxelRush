using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoxelRush.Api;

public enum AdRewardOutcome
{
    Granted,
    Duplicate,
    DailyCap,
    Invalid,
}

/// <summary>
/// ECON-002 rewarded ads, server-side verification only (AdMob SSV): AdMob calls back with a query string signed by
/// one of Google's published ECDSA keys; the reward is a small XP grant keyed by AdMob's transaction id, so replays
/// and retries never pay twice, and capped per day. Off unless Ads:Enabled (privacy/age review is a launch gate).
/// Rewards are progression XP only, which unlocks cosmetics: nothing that wins races.
/// </summary>
public sealed class AdRewardService(ApiOptions options, IHttpClientFactory http, ProgressionStore progression, Npgsql.NpgsqlDataSource db, TimeProvider clock)
{
    public const string HttpClientName = "admob-keys";

    private readonly SemaphoreSlim _keysLock = new(1, 1);
    private Dictionary<long, byte[]> _keys = new();
    private DateTimeOffset _keysFetched = DateTimeOffset.MinValue;

    public async Task<AdRewardOutcome> VerifyAndGrantAsync(string? rawQuery, CancellationToken ct)
    {
        // The signed content is the query string up to (not including) "&signature=".
        string query = (rawQuery ?? string.Empty).TrimStart('?');
        int cut = query.IndexOf("&signature=", StringComparison.Ordinal);
        if (cut <= 0 || query.Length > 4096)
        {
            return AdRewardOutcome.Invalid;
        }

        Dictionary<string, string> p = query.Split('&').Select(kv => kv.Split('=', 2)).Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0]).ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First()[1]));
        if (!p.TryGetValue("signature", out string? sig) || !p.TryGetValue("key_id", out string? keyText) || !long.TryParse(keyText, out long keyId)
            || !p.TryGetValue("transaction_id", out string? transaction) || transaction.Length is 0 or > 64
            || !p.TryGetValue("user_id", out string? user) || !Guid.TryParse(user, out Guid player)
            || !p.TryGetValue("timestamp", out string? ts) || !long.TryParse(ts, out long ms))
        {
            return AdRewardOutcome.Invalid;
        }

        DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ms, 0, 253402300799999));
        DateTimeOffset now = clock.GetUtcNow();
        if (at > now.AddMinutes(5) || at < now.AddHours(-options.Ads.MaxCallbackAgeHours))
        {
            return AdRewardOutcome.Invalid;
        }

        byte[]? key = await KeyAsync(keyId, ct);
        if (key == null || !Verify(key, query[..cut], sig))
        {
            return AdRewardOutcome.Invalid;
        }

        await using (Npgsql.NpgsqlCommand count = db.CreateCommand("SELECT count(*) FROM reward_grants WHERE player_id = $1 AND source = 'ad' AND created_at > $2"))
        {
            count.Parameters.Add(new() { Value = player });
            count.Parameters.Add(new() { Value = now.AddDays(-1) });
            if ((long)(await count.ExecuteScalarAsync(ct))! >= options.Ads.DailyCap)
            {
                return AdRewardOutcome.DailyCap;
            }
        }

        (bool ok, GrantResult? result) = await progression.GrantAsync(player, new GrantRequest("ad:admob:" + transaction, "xp", options.Ads.RewardXp, "ad"), ct);
        return !ok ? AdRewardOutcome.Invalid : result!.Duplicate ? AdRewardOutcome.Duplicate : AdRewardOutcome.Granted;
    }

    private static bool Verify(byte[] spki, string content, string signature)
    {
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(content), AppleVerifier.Base64Url(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
    }

    // Google rotates keys: refetch on an unknown key id, at most once a minute.
    private async Task<byte[]?> KeyAsync(long id, CancellationToken ct)
    {
        await _keysLock.WaitAsync(ct);
        try
        {
            DateTimeOffset now = clock.GetUtcNow();
            if ((!_keys.ContainsKey(id) && now - _keysFetched > TimeSpan.FromMinutes(1)) || now - _keysFetched > TimeSpan.FromHours(24))
            {
                try
                {
                    string json = await http.CreateClient(HttpClientName).GetStringAsync(options.Ads.AdMobKeysUrl, ct);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    _keys = doc.RootElement.GetProperty("keys").EnumerateArray()
                        .ToDictionary(k => k.GetProperty("keyId").GetInt64(), k => Convert.FromBase64String(k.GetProperty("base64").GetString()!));
                }
                catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
                {
                    // Keep the old keys; an unknown key is then refused.
                }

                _keysFetched = now;
            }

            return _keys.TryGetValue(id, out byte[]? key) ? key : null;
        }
        finally
        {
            _keysLock.Release();
        }
    }
}
