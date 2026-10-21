using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace VoxelRush.Api.Tests;

/// <summary>A test stand-in for Apple's PKI: root → intermediate (Apple marker OID) → leaf (Apple marker OID).</summary>
internal sealed class TestAppleCa : IDisposable
{
    private readonly ECDsa _rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly (ECDsa Key, X509Certificate2[] Chain) _marked;
    private readonly (ECDsa Key, X509Certificate2[] Chain) _unmarked;

    public TestAppleCa()
    {
        var rootReq = new CertificateRequest("CN=Test Apple Root", _rootKey, HashAlgorithmName.SHA256);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        Root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        _marked = Issue(markers: true, serial: 1);
        // Same trusted root, but without Apple's marker OIDs: only the OID check can refuse it.
        _unmarked = Issue(markers: false, serial: 2);
    }

    private (ECDsa, X509Certificate2[]) Issue(bool markers, byte serial)
    {
        using ECDsa interKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var interReq = new CertificateRequest("CN=Test Apple WWDR " + serial, interKey, HashAlgorithmName.SHA256);
        interReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        interReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        if (markers)
        {
            interReq.CertificateExtensions.Add(new X509Extension("1.2.840.113635.100.6.2.1", [0x05, 0x00], false));
        }

        X509Certificate2 inter = interReq.Create(Root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2), [serial, 1]).CopyWithPrivateKey(interKey);
        ECDsa leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafReq = new CertificateRequest("CN=Test Prod ECC Mac App Store " + serial, leafKey, HashAlgorithmName.SHA256);
        if (markers)
        {
            leafReq.CertificateExtensions.Add(new X509Extension("1.2.840.113635.100.6.11.1", [0x05, 0x00], false));
        }

        X509Certificate2 leaf = leafReq.Create(inter, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), [serial, 2]);
        return (leafKey, [leaf, inter, Root]);
    }

    public X509Certificate2 Root { get; }

    public string RootBase64 => Convert.ToBase64String(Root.RawData);

    public string Sign(object payload, string alg = "ES256", bool markers = true)
    {
        (ECDsa key, X509Certificate2[] chain) = markers ? _marked : _unmarked;
        string header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg, x5c = chain.Select(c => Convert.ToBase64String(c.RawData)).ToArray() }));
        string body = B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        return header + "." + body + "." + B64(key.SignData(Encoding.ASCII.GetBytes(header + "." + body), HashAlgorithmName.SHA256));
    }

    public static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        _rootKey.Dispose();
        _marked.Key.Dispose();
        _unmarked.Key.Dispose();
    }
}

/// <summary>ECON-002: App Store purchases are verified server-side; duplicates, shared and refunded receipts are handled.</summary>
[NonParallelizable]
public sealed class AppleStoreTests
{
    private readonly TestAppleCa _ca = new();
    private ApiHost _api = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _api = await ApiHost.StartAsync(extra: new Dictionary<string, string?> { ["Store__AppleRootCertificate"] = _ca.RootBase64 });

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        await _api.DisposeAsync();
        _ca.Dispose();
    }

    private static object Transaction(string original, string product = "voxelrush.paint.chrome", string bundle = "com.doubletstudio.voxelrush",
        string environment = "Production", bool revoked = false, string? transaction = null) => revoked
        ? new { transactionId = transaction ?? original, originalTransactionId = original, bundleId = bundle, productId = product, environment, type = "Non-Consumable", revocationDate = 1760000000000 }
        : new { transactionId = transaction ?? original, originalTransactionId = original, bundleId = bundle, productId = product, environment, type = "Non-Consumable" };

    private Task<HttpResponseMessage> SubmitAsync(TokenPair p, string jws) =>
        _api.Client(p.AccessToken).PostAsJsonAsync("/v1/players/me/purchases", new { platform = "apple", signedTransaction = jws });

    private async Task<string[]> OwnedAsync(TokenPair p) =>
        (await _api.Client(p.AccessToken).GetFromJsonAsync<EntitlementView[]>("/v1/players/me/entitlements"))!.Select(e => e.Sku).ToArray();

    [Test]
    public async Task VerifiedPurchase_Grants_AndRestoresOnAnotherDeviceAreIdempotent()
    {
        TokenPair p = await _api.GuestAsync();
        string tx = "2000000" + Random.Shared.Next(100000, 999999);

        HttpResponseMessage first = await SubmitAsync(p, _ca.Sign(Transaction(tx)));
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("granted").GetBoolean(), Is.True);
        // StoreKit restore: new transaction id, same original transaction.
        var restored = await (await SubmitAsync(p, _ca.Sign(Transaction(tx, transaction: tx + "9")))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.That(restored.GetProperty("granted").GetBoolean(), Is.False, "duplicate purchase grants nothing new");
        Assert.That(await OwnedAsync(p), Is.EqualTo(new[] { "paint.chrome" }));
    }

    [Test]
    public async Task SameReceipt_OnAnotherAccount_IsRefused()
    {
        TokenPair owner = await _api.GuestAsync(), other = await _api.GuestAsync();
        string jws = _ca.Sign(Transaction("3000000" + Random.Shared.Next(100000, 999999)));
        await SubmitAsync(owner, jws);

        HttpResponseMessage shared = await SubmitAsync(other, jws);

        Assert.That(shared.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await shared.Content.ReadAsStringAsync(), Does.Contain("receipt_in_use"));
        Assert.That(await OwnedAsync(other), Is.Empty);
    }

    [Test]
    public async Task RevokedTransaction_IsRefused_AndRemovesAnEarlierGrant()
    {
        TokenPair p = await _api.GuestAsync();
        string tx = "4000000" + Random.Shared.Next(100000, 999999);
        await SubmitAsync(p, _ca.Sign(Transaction(tx)));

        HttpResponseMessage revoked = await SubmitAsync(p, _ca.Sign(Transaction(tx, revoked: true)));

        Assert.That(revoked.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await revoked.Content.ReadAsStringAsync(), Does.Contain("purchase_revoked"));
        Assert.That(await OwnedAsync(p), Is.Empty);
        Assert.That((await SubmitAsync(p, _ca.Sign(Transaction(tx)))).StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "the old receipt can't be replayed after a refund");
    }

    [Test]
    public async Task RefundNotification_RevokesTheEntitlement()
    {
        TokenPair p = await _api.GuestAsync();
        string tx = "5000000" + Random.Shared.Next(100000, 999999);
        await SubmitAsync(p, _ca.Sign(Transaction(tx, product: "voxelrush.flag.checkered_gold")));
        Assert.That(await OwnedAsync(p), Is.EqualTo(new[] { "flag.checkered-gold" }));

        string notification = _ca.Sign(new
        {
            notificationType = "REFUND",
            notificationUUID = Guid.NewGuid().ToString(),
            data = new { bundleId = "com.doubletstudio.voxelrush", environment = "Production", signedTransactionInfo = _ca.Sign(Transaction(tx, product: "voxelrush.flag.checkered_gold", revoked: true)) },
        });
        HttpResponseMessage response = await _api.Client().PostAsJsonAsync("/v1/store/apple/notifications", new { signedPayload = notification });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await OwnedAsync(p), Is.Empty);
        Assert.That((await _api.Client().PostAsJsonAsync("/v1/store/apple/notifications", new { signedPayload = notification + "x" })).StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest), "forged notifications are refused");
    }

    [Test]
    public async Task UntrustedOrMalformedReceipts_AreRefused()
    {
        TokenPair p = await _api.GuestAsync();
        using var stranger = new TestAppleCa();
        string good = _ca.Sign(Transaction("6000000" + Random.Shared.Next(100000, 999999)));
        string[] parts = good.Split('.');
        string tamperedPayload = parts[0] + "." + TestAppleCa.B64(JsonSerializer.SerializeToUtf8Bytes(Transaction("1"))) + "." + parts[2];
        var cases = new Dictionary<string, string>
        {
            ["chain to another root"] = stranger.Sign(Transaction("6100001")),
            ["no Apple marker OIDs (trusted root)"] = _ca.Sign(Transaction("6100002"), markers: false),
            ["payload swapped after signing"] = tamperedPayload,
            ["alg none"] = _ca.Sign(Transaction("6100003"), alg: "none"),
            ["other app"] = _ca.Sign(Transaction("6100004", bundle: "com.example.other")),
            ["sandbox receipt in production"] = _ca.Sign(Transaction("6100005", environment: "Sandbox")),
            ["garbage"] = "not.a.jws",
        };

        foreach ((string name, string jws) in cases)
        {
            Assert.That((await SubmitAsync(p, jws)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), name);
        }

        Assert.That((await SubmitAsync(p, _ca.Sign(Transaction("6100006", product: "voxelrush.speed.boost")))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "unknown product");
        Assert.That(await OwnedAsync(p), Is.Empty);
    }

    [Test]
    public void EmbeddedAppleRoot_IsPinned()
    {
        // Default configuration (no override): loads the bundled Apple Root CA - G3 and checks its fingerprint.
        Assert.DoesNotThrow(() => _ = new AppleVerifier(new ApiOptions()));
    }
}

/// <summary>A fake Google OAuth + Android Publisher endpoint that checks the service-account assertion.</summary>
internal sealed class FakeGooglePlay : HttpMessageHandler
{
    public readonly RSA Key = RSA.Create(2048);
    public readonly Dictionary<string, object> Purchases = new();
    public readonly List<string> Acknowledged = new();
    public bool Down;

    public string ServiceAccountJson => JsonSerializer.Serialize(new
    {
        type = "service_account",
        client_email = "voxelrush@test.iam.gserviceaccount.com",
        private_key = Key.ExportPkcs8PrivateKeyPem(),
        token_uri = "https://oauth.test/token",
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string path = request.RequestUri!.AbsolutePath;
        if (request.RequestUri.Host == "oauth.test")
        {
            string form = await request.Content!.ReadAsStringAsync(ct);
            string assertion = Uri.UnescapeDataString(form.Split('&').Single(f => f.StartsWith("assertion=")).Substring(10));
            string[] jwt = assertion.Split('.');
            bool valid = Key.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), AppleVerifier.Base64Url(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return valid ? Json(new { access_token = "test-access-token", expires_in = 3600 }) : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        if (request.Headers.Authorization?.Parameter != "test-access-token")
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        if (Down)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }

        string token = Uri.UnescapeDataString(path.Split("/tokens/")[1]);
        if (token.EndsWith(":acknowledge"))
        {
            Acknowledged.Add(token[..^12]);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return Purchases.TryGetValue(token, out object? purchase) ? Json(purchase) : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };
}

/// <summary>ECON-002: Google Play purchases verified through the Publisher API; voided purchases revoke.</summary>
[NonParallelizable]
public sealed class GooglePlayTests
{
    private readonly FakeGooglePlay _play = new();
    private ApiHost _api = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _api = await ApiHost.StartAsync(
        extra: new Dictionary<string, string?>
        {
            ["Store__GoogleServiceAccountJson"] = _play.ServiceAccountJson,
            ["Store__GoogleApiBaseUrl"] = "https://play.test/",
            ["Store__GooglePushToken"] = "push-secret-0123456789",
        },
        services: s => s.AddHttpClient(GooglePlayVerifier.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _play));

    [OneTimeTearDown]
    public async Task StopAsync() => await _api.DisposeAsync();

    private string NewToken(int state = 0, int ack = 0, int? type = null)
    {
        string token = "tok-" + Guid.NewGuid().ToString("N");
        string order = "GPA.3399-" + Random.Shared.Next(1000, 9999) + "-" + Random.Shared.Next(1000, 9999);
        _play.Purchases[token] = type is int t
            ? new { purchaseState = state, acknowledgementState = ack, orderId = order, purchaseType = t }
            : new { purchaseState = state, acknowledgementState = ack, orderId = order };
        return token;
    }

    private Task<HttpResponseMessage> SubmitAsync(TokenPair p, string token, string product = "voxelrush.paint.chrome") =>
        _api.Client(p.AccessToken).PostAsJsonAsync("/v1/players/me/purchases", new { platform = "google", productId = product, purchaseToken = token });

    private async Task<string[]> OwnedAsync(TokenPair p) =>
        (await _api.Client(p.AccessToken).GetFromJsonAsync<EntitlementView[]>("/v1/players/me/entitlements"))!.Select(e => e.Sku).ToArray();

    [Test]
    public async Task Purchase_IsGranted_Acknowledged_AndDuplicatesGrantNothing()
    {
        TokenPair p = await _api.GuestAsync();
        string token = NewToken();

        Assert.That((await SubmitAsync(p, token)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(_play.Acknowledged, Does.Contain(token), "acknowledged so Google doesn't auto-refund");
        var again = await (await SubmitAsync(p, token)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.That(again.GetProperty("granted").GetBoolean(), Is.False);
        Assert.That(await OwnedAsync(p), Is.EqualTo(new[] { "paint.chrome" }));
    }

    [Test]
    public async Task CancelledPendingTestAndUnknownPurchases_GrantNothing()
    {
        TokenPair p = await _api.GuestAsync();

        Assert.That((await SubmitAsync(p, NewToken(state: 1))).StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "cancelled/refunded");
        Assert.That((await SubmitAsync(p, NewToken(state: 2))).StatusCode, Is.EqualTo(HttpStatusCode.Accepted), "pending: wait for payment");
        Assert.That((await SubmitAsync(p, NewToken(type: 0))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "license-tester purchase in production");
        Assert.That((await SubmitAsync(p, "tok-made-up")).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await OwnedAsync(p), Is.Empty);
    }

    [Test]
    public async Task SharedPurchaseToken_IsRefusedForASecondAccount()
    {
        TokenPair owner = await _api.GuestAsync(), other = await _api.GuestAsync();
        string token = NewToken();
        await SubmitAsync(owner, token);

        Assert.That((await SubmitAsync(other, token)).StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task VoidedPurchaseNotification_Revokes_OnlyWithThePushSecret()
    {
        TokenPair p = await _api.GuestAsync();
        string token = NewToken();
        await SubmitAsync(p, token);
        string order = JsonSerializer.SerializeToElement(_play.Purchases[token]).GetProperty("orderId").GetString()!;
        string data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = "1.0",
            packageName = "com.doubletstudio.voxelrush",
            voidedPurchaseNotification = new { purchaseToken = token, orderId = order, productType = 2, refundType = 1 },
        }));
        var push = new { message = new { data, messageId = "1" }, subscription = "projects/x/subscriptions/y" };

        Assert.That((await _api.Client().PostAsJsonAsync("/v1/store/google/notifications?token=wrong", push)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await OwnedAsync(p), Is.EqualTo(new[] { "paint.chrome" }));
        Assert.That((await _api.Client().PostAsJsonAsync("/v1/store/google/notifications?token=push-secret-0123456789", push)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await OwnedAsync(p), Is.Empty, "refund revoked it");
        Assert.That((await SubmitAsync(p, token)).StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "a voided token can't be replayed");
    }

    [Test]
    public async Task StoreOutage_GrantsNothing_AndSaysUnavailable()
    {
        TokenPair p = await _api.GuestAsync();
        _play.Down = true;
        try
        {
            Assert.That((await SubmitAsync(p, NewToken())).StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }
        finally
        {
            _play.Down = false;
        }

        Assert.That(await OwnedAsync(p), Is.Empty);
    }
}

/// <summary>ECON-002 rewarded ads: AdMob server-side verification, replay-safe and capped; off by default.</summary>
[NonParallelizable]
public sealed class AdRewardTests
{
    private const long KeyId = 3335741209;
    private readonly ECDsa _admob = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private ApiHost _api = null!;

    private sealed class Keys(ECDsa key) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { keys = new[] { new { keyId = KeyId, pem = key.ExportSubjectPublicKeyInfoPem(), base64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) } } }),
            });
    }

    [OneTimeSetUp]
    public async Task StartAsync() => _api = await ApiHost.StartAsync(
        extra: new Dictionary<string, string?> { ["Ads__Enabled"] = "true", ["Ads__AdMobKeysUrl"] = "https://keys.test/verifier-keys.json", ["Ads__DailyCap"] = "3" },
        services: s => s.AddHttpClient(AdRewardService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Keys(_admob)));

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        await _api.DisposeAsync();
        _admob.Dispose();
    }

    // AdMob's layout: the signed content is everything before "&signature="; signature and key_id come last.
    private string Callback(Guid player, string transaction, long? timestampMs = null, ECDsa? signer = null, string? userOverride = null)
    {
        _api.Clock.Now = DateTimeOffset.UtcNow;
        long ts = timestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string content = $"ad_network=5450213213286189855&ad_unit=1234567890&reward_amount=1&reward_item=xp&timestamp={ts}&transaction_id={transaction}&user_id={player}";
        string signature = TestAppleCa.B64((signer ?? _admob).SignData(Encoding.UTF8.GetBytes(content), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        if (userOverride != null)
        {
            content = content.Replace(player.ToString(), userOverride);
        }

        return $"/v1/ads/admob/reward?{content}&signature={signature}&key_id={KeyId}";
    }

    private async Task<long> XpAsync(TokenPair p) =>
        (await _api.Client(p.AccessToken).GetFromJsonAsync<JsonElement>("/v1/players/me/progress")).GetProperty("xp").GetInt64();

    [Test]
    public async Task VerifiedReward_GrantsXpOnce_EvenWhenReplayed()
    {
        TokenPair p = await _api.GuestAsync();
        string url = Callback(p.PlayerId, "tx" + Guid.NewGuid().ToString("N")[..16]);

        var first = await (await _api.Client().GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();
        var replay = await (await _api.Client().GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.That(first.GetProperty("granted").GetBoolean(), Is.True);
        Assert.That(replay.GetProperty("granted").GetBoolean(), Is.False);
        Assert.That(replay.GetProperty("reason").GetString(), Is.EqualTo("Duplicate"));
        Assert.That(await XpAsync(p), Is.EqualTo(25));
    }

    [Test]
    public async Task ForgedTamperedOrStaleCallbacks_AreRefused()
    {
        TokenPair p = await _api.GuestAsync(), thief = await _api.GuestAsync();
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.That((await _api.Client().GetAsync(Callback(p.PlayerId, "f1", signer: attacker))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "not AdMob's key");
        Assert.That((await _api.Client().GetAsync(Callback(p.PlayerId, "f2", userOverride: thief.PlayerId.ToString()))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "reward redirected");
        Assert.That((await _api.Client().GetAsync(Callback(p.PlayerId, "f3", timestampMs: DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds()))).StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest), "too old");
        Assert.That((await _api.Client().GetAsync(Callback(p.PlayerId, "f4").Replace($"key_id={KeyId}", "key_id=1"))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "unknown key");
        Assert.That((await _api.Client().GetAsync("/v1/ads/admob/reward?user_id=x")).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await XpAsync(p) + await XpAsync(thief), Is.Zero);
    }

    [Test]
    public async Task DailyCap_StopsFurtherRewards()
    {
        TokenPair p = await _api.GuestAsync();
        var reasons = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var r = await (await _api.Client().GetAsync(Callback(p.PlayerId, $"cap{i}-{Guid.NewGuid():N}"[..20]))).Content.ReadFromJsonAsync<JsonElement>();
            reasons.Add(r.GetProperty("reason").GetString()!);
        }

        Assert.That(reasons, Is.EqualTo(new[] { "Granted", "Granted", "Granted", "DailyCap" }));
        Assert.That(await XpAsync(p), Is.EqualTo(75));
    }

    [Test]
    public async Task AdsAreOffByDefault()
    {
        await using ApiHost bare = await ApiHost.StartAsync();
        TokenPair p = await bare.GuestAsync();

        Assert.That((await bare.Client().GetAsync(Callback(p.PlayerId, "off1"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "until the privacy review signs off");
    }
}
