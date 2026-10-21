using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VoxelRush.Api.Tests;

/// <summary>ECON-001: cosmetic-only catalog, free core gameplay, backend entitlement ledger across devices.</summary>
[NonParallelizable]
public sealed class EconomyTests
{
    private ApiHost _api = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _api = await ApiHost.StartAsync();

    [OneTimeTearDown]
    public async Task StopAsync() => await _api.DisposeAsync();

    private async Task<string[]> OwnedAsync(TokenPair p) =>
        (await _api.Client(p.AccessToken).GetFromJsonAsync<EntitlementView[]>("/v1/players/me/entitlements"))!.Select(e => e.Sku).ToArray();

    private Task<HttpResponseMessage> ClaimAsync(TokenPair p, string sku) =>
        _api.Client(p.AccessToken).PostAsJsonAsync("/v1/players/me/entitlements/claim", new { sku });

    private Task<HttpResponseMessage> ServerGrantAsync(Guid player, string sku, string tx, string source = "purchase") =>
        _api.Service().PostAsJsonAsync($"/internal/v1/players/{player}/entitlements", new { sku, source, transactionId = tx });

    [Test]
    public async Task Catalog_IsCosmeticOnly()
    {
        var catalog = await _api.Client().GetFromJsonAsync<JsonElement>("/v1/catalog");
        JsonElement[] items = catalog.GetProperty("items").EnumerateArray().ToArray();

        Assert.That(items, Is.Not.Empty);
        Assert.That(items.Select(i => i.GetProperty("kind").GetString()), Is.All.AnyOf(EntitlementStore.CosmeticKinds));
        // Nothing in the catalog carries gameplay stats.
        string[] gameplayWords = ["speed", "boost", "damage", "hp", "armor", "block", "checkpoint", "power"];
        Assert.That(items.SelectMany(i => i.EnumerateObject().Select(p => p.Name.ToLowerInvariant())), Has.None.AnyOf(gameplayWords));
        Assert.That(items.Count(i => i.GetProperty("unlock").TryGetProperty("level", out JsonElement l) && l.ValueKind == JsonValueKind.Number && l.GetInt32() == 1), Is.GreaterThanOrEqualTo(1),
            "something is free from the start");
    }

    [Test]
    public async Task CoreGameplay_NeedsNoEntitlements()
    {
        TokenPair p = await _api.GuestAsync();
        Assert.That(await OwnedAsync(p), Is.Empty);

        WorldView world = await _api.CreateWorldAsync(p, "Free Play");
        var join = await _api.Client(p.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null);

        Assert.That(join.StatusCode, Is.EqualTo(HttpStatusCode.OK), "build, race and destroy in your own world for free");
    }

    [Test]
    public async Task LevelUnlocks_AreCheckedAgainstServerProgression_AndIdempotent()
    {
        TokenPair p = await _api.GuestAsync();

        Assert.That((await ClaimAsync(p, "paint.racing-red")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await ClaimAsync(p, "paint.ocean-blue")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "needs level 2");
        await _api.Service().PostAsJsonAsync($"/internal/v1/players/{p.PlayerId}/grants", new { grantKey = $"race:x:{p.PlayerId}", kind = "xp", amount = 100, source = "race" });
        Assert.That((await ClaimAsync(p, "paint.ocean-blue")).StatusCode, Is.EqualTo(HttpStatusCode.OK), "level 2 now");
        var again = await (await ClaimAsync(p, "paint.ocean-blue")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.That(again.GetProperty("granted").GetBoolean(), Is.False, "claiming twice changes nothing");
        Assert.That(await OwnedAsync(p), Is.EquivalentTo(new[] { "paint.racing-red", "paint.ocean-blue" }));
    }

    [Test]
    public async Task StoreItems_CantBeSelfClaimed_UnknownSkusRejected()
    {
        TokenPair p = await _api.GuestAsync();

        Assert.That((await ClaimAsync(p, "paint.chrome")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await ClaimAsync(p, "speed.boost-x2")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _api.Client(p.AccessToken).PostAsJsonAsync($"/internal/v1/players/{p.PlayerId}/entitlements", new { sku = "paint.chrome", source = "purchase", transactionId = "fake" })).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized), "players can't use the server grant path");
        Assert.That(await OwnedAsync(p), Is.Empty);
    }

    [Test]
    public async Task ServerGrants_AreIdempotentPerTransaction_AndRevocable()
    {
        TokenPair p = await _api.GuestAsync();

        Assert.That((await (await ServerGrantAsync(p.PlayerId, "paint.chrome", "gpa.1001")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("granted").GetBoolean(), Is.True);
        Assert.That((await (await ServerGrantAsync(p.PlayerId, "paint.chrome", "gpa.1001")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("granted").GetBoolean(), Is.False, "replayed receipt");
        Assert.That((await ServerGrantAsync(Guid.NewGuid(), "paint.chrome", "gpa.1001")).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "a receipt belongs to one player");
        Assert.That((await ServerGrantAsync(p.PlayerId, "paint.chrome", "gpa.1002", "free-money")).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await OwnedAsync(p), Is.EqualTo(new[] { "paint.chrome" }));

        Assert.That((await _api.Service().PostAsJsonAsync("/internal/v1/entitlements/revoke", new { source = "purchase", transactionId = "gpa.1001" })).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await OwnedAsync(p), Is.Empty, "refund revokes it");
        Assert.That((await _api.Service().PostAsJsonAsync("/internal/v1/entitlements/revoke", new { source = "purchase", transactionId = "gpa.1001" })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await (await ServerGrantAsync(p.PlayerId, "paint.chrome", "gpa.1001")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("granted").GetBoolean(), Is.False,
            "a revoked receipt can't be replayed into a fresh grant");
        Assert.That(await OwnedAsync(p), Is.Empty);
    }

    [Test]
    public async Task Entitlements_FollowThePlayer_ToALinkedDevice()
    {
        TokenPair android = await _api.GuestAsync();
        await ClaimAsync(android, "paint.racing-red");
        await ServerGrantAsync(android.PlayerId, "flag.checkered-gold", "ios.tx.77");
        var code = await (await _api.Client(android.AccessToken).PostAsync("/v1/auth/link-code", null)).Content.ReadFromJsonAsync<LinkCode>();
        var ios = await (await _api.Client().PostAsJsonAsync("/v1/auth/link", new { code = code!.Code })).Content.ReadFromJsonAsync<TokenPair>();

        Assert.That(await OwnedAsync(ios!), Is.EquivalentTo(await OwnedAsync(android)));
        Assert.That(await OwnedAsync(ios!), Is.EquivalentTo(new[] { "paint.racing-red", "flag.checkered-gold" }));
    }

    [Test]
    public void CatalogValidation_RejectsNonCosmeticItems()
    {
        Assert.That(EntitlementStore.Load().Items.All(i => EntitlementStore.CosmeticKinds.Contains(i.Kind)), Is.True);
    }
}
