using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace VoxelRush.Api;

public sealed record AppleNotification(string? SignedPayload);

public sealed record PurchaseRequest(string? Platform, string? SignedTransaction, string? ProductId, string? PurchaseToken);

/// <param name="TransactionKey">Ledger id: one per original purchase, so restores on another device are idempotent.</param>
public sealed record VerifiedPurchase(string TransactionKey, string ProductId, bool Revoked, bool Pending, bool NeedsAcknowledge);

public enum PurchaseOutcome
{
    Granted,
    AlreadyOwned,
    Pending,
    Revoked,
    ReceiptInUse,
    UnknownProduct,
    Invalid,
    Unavailable,
    NotConfigured,
}

/// <summary>
/// ECON-002 App Store verification: StoreKit 2 signed transactions and App Store Server Notifications v2 are JWS whose
/// x5c chain must lead to Apple Root CA - G3 (pinned; overridable for tests), with Apple's marker OIDs on the leaf and
/// intermediate, signed ES256. Only then are bundle id and environment checked and the payload trusted.
/// </summary>
public sealed class AppleVerifier
{
    private const string LeafOid = "1.2.840.113635.100.6.11.1";
    private const string IntermediateOid = "1.2.840.113635.100.6.2.1";
    private const string AppleRootG3Sha256 = "63343ABFB89A6A03EBB57E9B3F5FA7BE7C4F5C756F3017B3A8C488C3653E9179";

    private readonly StoreOptions _options;
    private readonly X509Certificate2 _root;

    public AppleVerifier(ApiOptions options)
    {
        _options = options.Store;
        if (!string.IsNullOrEmpty(_options.AppleRootCertificate))
        {
            _root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(_options.AppleRootCertificate));
            return;
        }

        using Stream stream = typeof(AppleVerifier).Assembly.GetManifestResourceStream("VoxelRush.Api.AppleRootCA-G3.cer")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        _root = X509CertificateLoader.LoadCertificate(buffer.ToArray());
        if (!string.Equals(_root.GetCertHashString(HashAlgorithmName.SHA256), AppleRootG3Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Embedded Apple root certificate does not match its pinned fingerprint.");
        }
    }

    public VerifiedPurchase? VerifyTransaction(string? jws)
    {
        if (VerifyJws(jws) is not JsonElement t || !Matches(t))
        {
            return null;
        }

        string? product = Str(t, "productId");
        string? original = Str(t, "originalTransactionId");
        if (product == null || original == null)
        {
            return null;
        }

        return new VerifiedPurchase("apple:" + original, product, t.TryGetProperty("revocationDate", out _), Pending: false, NeedsAcknowledge: false);
    }

    /// <summary>A verified notification's type and its verified transaction (null when either fails).</summary>
    public (string Type, VerifiedPurchase Purchase)? VerifyNotification(string? signedPayload)
    {
        if (VerifyJws(signedPayload) is not JsonElement n || !n.TryGetProperty("data", out JsonElement data) || Str(n, "notificationType") is not string type
            || Str(data, "bundleId") != _options.AppleBundleId || VerifyTransaction(Str(data, "signedTransactionInfo")) is not VerifiedPurchase purchase)
        {
            return null;
        }

        return (type, purchase);
    }

    private bool Matches(JsonElement t) =>
        Str(t, "bundleId") == _options.AppleBundleId && Str(t, "environment") is string env && _options.AppleEnvironments.Contains(env);

    private JsonElement? VerifyJws(string? jws)
    {
        string[] parts = jws?.Split('.') ?? [];
        if (parts.Length != 3 || jws!.Length > 32 * 1024)
        {
            return null;
        }

        try
        {
            using JsonDocument header = JsonDocument.Parse(Base64Url(parts[0]));
            if (Str(header.RootElement, "alg") != "ES256" || !header.RootElement.TryGetProperty("x5c", out JsonElement x5c)
                || x5c.ValueKind != JsonValueKind.Array || x5c.GetArrayLength() is < 2 or > 4)
            {
                return null;
            }

            X509Certificate2[] certs = x5c.EnumerateArray().Select(c => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(c.GetString()!))).ToArray();
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(_root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ExtraStore.AddRange(certs.Skip(1).ToArray());
            if (!chain.Build(certs[0]) || chain.ChainElements.Count < 3
                || !chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(_root.RawData)
                || certs[0].Extensions[LeafOid] == null || chain.ChainElements[1].Certificate.Extensions[IntermediateOid] == null)
            {
                return null;
            }

            using ECDsa? key = certs[0].GetECDsaPublicKey();
            byte[] signature = Base64Url(parts[2]);
            if (key == null || signature.Length != 64 || !key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256))
            {
                return null;
            }

            using JsonDocument payload = JsonDocument.Parse(Base64Url(parts[1]));
            return payload.RootElement.Clone();
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException or InvalidOperationException)
        {
            return null;
        }
    }

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static byte[] Base64Url(string s)
    {
        string padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }
}

/// <summary>
/// ECON-002 Google Play verification through the Android Publisher API, authenticated as a service account (JWT bearer
/// grant; the key lives in configuration, never in the game). Purchases are acknowledged after granting, otherwise
/// Google refunds them after three days.
/// </summary>
public sealed class GooglePlayVerifier(ApiOptions options, IHttpClientFactory http, TimeProvider clock)
{
    public const string HttpClientName = "google-play";

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public bool Configured => !string.IsNullOrEmpty(options.Store.GoogleServiceAccountJson);

    /// <summary>Null for tokens Google doesn't know; throws <see cref="HttpRequestException"/> when Google is unavailable.</summary>
    public async Task<VerifiedPurchase?> VerifyAsync(string productId, string purchaseToken, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, ProductPath(productId, purchaseToken), ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.BadRequest)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Google Play returned {(int)response.StatusCode}.");
        }

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        JsonElement p = doc.RootElement;
        int state = Int(p, "purchaseState") ?? -1;
        string? order = AppleVerifier.Str(p, "orderId");
        // purchaseType 0 = license-tester purchase: only accepted where configured (internal testing tracks).
        if (order == null || state is < 0 or > 2 || (Int(p, "purchaseType") == 0 && !options.Store.GoogleAllowTestPurchases))
        {
            return null;
        }

        return new VerifiedPurchase("google:" + order, productId, Revoked: state == 1, Pending: state == 2, NeedsAcknowledge: Int(p, "acknowledgementState") == 0);
    }

    public async Task AcknowledgeAsync(string productId, string purchaseToken, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, ProductPath(productId, purchaseToken) + ":acknowledge", ct);
        response.EnsureSuccessStatusCode();
    }

    private string ProductPath(string productId, string token) =>
        $"androidpublisher/v3/applications/{Uri.EscapeDataString(options.Store.GooglePackageName)}/purchases/products/{Uri.EscapeDataString(productId)}/tokens/{Uri.EscapeDataString(token)}";

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        HttpClient client = http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, new Uri(new Uri(options.Store.GoogleApiBaseUrl), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(client, ct));
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, ct);
    }

    private async Task<string> AccessTokenAsync(HttpClient client, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            DateTimeOffset now = clock.GetUtcNow();
            if (_token != null && now < _tokenExpires)
            {
                return _token;
            }

            using JsonDocument account = JsonDocument.Parse(options.Store.GoogleServiceAccountJson!);
            string email = AppleVerifier.Str(account.RootElement, "client_email")!;
            string tokenUri = AppleVerifier.Str(account.RootElement, "token_uri") ?? "https://oauth2.googleapis.com/token";
            using RSA key = RSA.Create();
            key.ImportFromPem(AppleVerifier.Str(account.RootElement, "private_key"));
            string Encode(object o) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(o)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            string unsigned = Encode(new { alg = "RS256", typ = "JWT" }) + "." + Encode(new
            {
                iss = email,
                scope = "https://www.googleapis.com/auth/androidpublisher",
                aud = tokenUri,
                iat = now.ToUnixTimeSeconds(),
                exp = now.AddMinutes(60).ToUnixTimeSeconds(),
            });
            byte[] signature = key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            string assertion = unsigned + "." + Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using HttpResponseMessage response = await client.PostAsync(tokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            }), ct);
            response.EnsureSuccessStatusCode();
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _token = AppleVerifier.Str(body.RootElement, "access_token") ?? throw new HttpRequestException("No access token.");
            _tokenExpires = now.AddSeconds((Int(body.RootElement, "expires_in") ?? 3600) - 60);
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;
}

/// <summary>
/// ECON-002 purchases: verify with the store, map the store product to a cosmetic SKU, grant through the entitlement
/// ledger (one row per original purchase, so duplicates and restores are idempotent and a receipt can't be shared
/// between accounts), and revoke on refunds. The client never decides what it owns.
/// </summary>
public sealed class PurchaseService(AppleVerifier apple, GooglePlayVerifier google, EntitlementStore entitlements, ILogger<PurchaseService> log)
{
    public async Task<(PurchaseOutcome, string? Sku)> SubmitAsync(Guid player, PurchaseRequest r, CancellationToken ct)
    {
        VerifiedPurchase? purchase;
        switch (r.Platform)
        {
            case "apple":
                purchase = apple.VerifyTransaction(r.SignedTransaction);
                break;
            case "google":
                if (!google.Configured)
                {
                    return (PurchaseOutcome.NotConfigured, null);
                }

                if (string.IsNullOrEmpty(r.ProductId) || r.ProductId.Length > 128 || string.IsNullOrEmpty(r.PurchaseToken) || r.PurchaseToken.Length > 1024)
                {
                    return (PurchaseOutcome.Invalid, null);
                }

                try
                {
                    purchase = await google.VerifyAsync(r.ProductId, r.PurchaseToken, ct);
                }
                catch (HttpRequestException e)
                {
                    log.LogWarning("google_play_unavailable {Error}", e.Message);
                    return (PurchaseOutcome.Unavailable, null);
                }

                break;
            default:
                return (PurchaseOutcome.Invalid, null);
        }

        if (purchase == null)
        {
            return (PurchaseOutcome.Invalid, null);
        }

        CatalogItem? item = entitlements.Catalog.Items.FirstOrDefault(i => i.Unlock.Store && i.ProductId == purchase.ProductId);
        if (item == null)
        {
            return (PurchaseOutcome.UnknownProduct, null);
        }

        if (purchase.Revoked)
        {
            // Refunded or cancelled: make sure nothing stays granted for it.
            await entitlements.RevokeAsync(new RevokeEntitlementRequest("purchase", purchase.TransactionKey), ct);
            return (PurchaseOutcome.Revoked, item.Sku);
        }

        if (purchase.Pending)
        {
            return (PurchaseOutcome.Pending, item.Sku);
        }

        EntitlementOutcome granted = await entitlements.GrantAsync(player, new GrantEntitlementRequest(item.Sku, "purchase", purchase.TransactionKey), ct);
        if (granted == EntitlementOutcome.Invalid)
        {
            // The transaction is already recorded for another account.
            return (PurchaseOutcome.ReceiptInUse, item.Sku);
        }

        if (granted == EntitlementOutcome.AlreadyOwned && await entitlements.IsRevokedAsync("purchase", purchase.TransactionKey, ct))
        {
            return (PurchaseOutcome.Revoked, item.Sku);
        }

        if (purchase.NeedsAcknowledge)
        {
            try
            {
                await google.AcknowledgeAsync(r.ProductId!, r.PurchaseToken!, ct);
            }
            catch (HttpRequestException e)
            {
                // Granted anyway; the client resubmits unacknowledged purchases, which acknowledges idempotently.
                log.LogWarning("google_play_acknowledge_failed {Error}", e.Message);
            }
        }

        return (granted == EntitlementOutcome.Granted ? PurchaseOutcome.Granted : PurchaseOutcome.AlreadyOwned, item.Sku);
    }

    /// <summary>App Store Server Notification v2: refunds and revocations remove the entitlement.</summary>
    public async Task<bool?> AppleNotificationAsync(string? signedPayload, CancellationToken ct)
    {
        if (apple.VerifyNotification(signedPayload) is not (string type, VerifiedPurchase purchase))
        {
            return null;
        }

        if (type is "REFUND" or "REVOKE")
        {
            return await entitlements.RevokeAsync(new RevokeEntitlementRequest("purchase", purchase.TransactionKey), ct);
        }

        return false;
    }

    /// <summary>Google Play real-time developer notification (Pub/Sub push): voided purchases are revoked.</summary>
    public async Task<bool?> GoogleNotificationAsync(JsonElement push, CancellationToken ct)
    {
        try
        {
            string? data = push.GetProperty("message").GetProperty("data").GetString();
            using JsonDocument notification = JsonDocument.Parse(Convert.FromBase64String(data ?? string.Empty));
            if (!notification.RootElement.TryGetProperty("voidedPurchaseNotification", out JsonElement voided))
            {
                return false;
            }

            return AppleVerifier.Str(voided, "orderId") is string order
                && await entitlements.RevokeAsync(new RevokeEntitlementRequest("purchase", "google:" + order), ct);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or JsonException)
        {
            return null;
        }
    }
}
