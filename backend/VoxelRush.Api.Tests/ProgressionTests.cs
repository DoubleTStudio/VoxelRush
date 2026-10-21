using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VoxelRush.Api.Tests;

/// <summary>SAVE-006: cross-device continuity, server-only progression, no double grant, versioned settings.</summary>
[NonParallelizable]
public sealed class ProgressionTests
{
    private ApiHost _api = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _api = await ApiHost.StartAsync();

    [OneTimeTearDown]
    public async Task StopAsync() => await _api.DisposeAsync();

    private Task<HttpResponseMessage> GrantAsync(Guid player, string key, int amount = 150, string kind = "xp") =>
        _api.Service().PostAsJsonAsync($"/internal/v1/players/{player}/grants", new { grantKey = key, kind, amount, source = "race" });

    private async Task<ProgressView> ProgressAsync(TokenPair p) => (await _api.Client(p.AccessToken).GetFromJsonAsync<ProgressView>("/v1/players/me/progress"))!;

    [Test]
    public async Task NewPlayer_StartsAtLevelOne()
    {
        Assert.That(await ProgressAsync(await _api.GuestAsync()), Is.EqualTo(new ProgressView(0, 1, 0)));
    }

    [Test]
    public async Task Grants_ApplyOnce_PerKey()
    {
        TokenPair p = await _api.GuestAsync();

        var first = await (await GrantAsync(p.PlayerId, $"race:r1:{p.PlayerId}", 150)).Content.ReadFromJsonAsync<GrantResult>();
        var retry = await (await GrantAsync(p.PlayerId, $"race:r1:{p.PlayerId}", 150)).Content.ReadFromJsonAsync<GrantResult>();
        await GrantAsync(p.PlayerId, $"race:r2:{p.PlayerId}", 300);

        Assert.That(first!.Duplicate, Is.False);
        Assert.That(retry!.Duplicate, Is.True);
        Assert.That(retry.Progress.Xp, Is.EqualTo(150), "the retry changed nothing");
        Assert.That(await ProgressAsync(p), Is.EqualTo(new ProgressView(450, 3, 2)), "450 XP = level 3; two applied grants");
    }

    [Test]
    public async Task ConcurrentRetries_OfOneGrant_StillApplyOnce()
    {
        TokenPair p = await _api.GuestAsync();
        string key = $"race:storm:{p.PlayerId}";

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => GrantAsync(p.PlayerId, key, 100)));

        Assert.That(responses.Count(r => r.IsSuccessStatusCode), Is.GreaterThanOrEqualTo(1));
        Assert.That((await ProgressAsync(p)).Xp, Is.EqualTo(100));
    }

    [Test]
    public async Task OnlyGameServers_Grant_AndClientsCantWriteProgress()
    {
        TokenPair p = await _api.GuestAsync();

        Assert.That((await _api.Client(p.AccessToken).PostAsJsonAsync($"/internal/v1/players/{p.PlayerId}/grants", new { grantKey = "x", kind = "xp", amount = 999, source = "me" })).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((int)(await _api.Client(p.AccessToken).PutAsJsonAsync("/v1/players/me/progress", new { xp = 1_000_000 })).StatusCode, Is.EqualTo(405).Or.EqualTo(404));
        Assert.That((await GrantAsync(p.PlayerId, "k", 0)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await GrantAsync(p.PlayerId, "k", 5, "gold")).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await GrantAsync(Guid.NewGuid(), "k", 5)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "unknown player");
        Assert.That((await ProgressAsync(p)).Xp, Is.Zero);
    }

    [Test]
    public async Task SecondDevice_LinksToTheSamePlayer_AndSeesTheSameProgress()
    {
        TokenPair android = await _api.GuestAsync();
        await GrantAsync(android.PlayerId, $"race:a:{android.PlayerId}", 250);

        var code = await (await _api.Client(android.AccessToken).PostAsync("/v1/auth/link-code", null)).Content.ReadFromJsonAsync<LinkCode>();
        Assert.That(code!.Code, Has.Length.EqualTo(10));
        var ios = await (await _api.Client().PostAsJsonAsync("/v1/auth/link", new { code = code.Code.ToLowerInvariant() })).Content.ReadFromJsonAsync<TokenPair>();

        Assert.That(ios!.PlayerId, Is.EqualTo(android.PlayerId));
        Assert.That(ios.RefreshToken, Is.Not.EqualTo(android.RefreshToken), "own login on the new device");
        Assert.That(await ProgressAsync(ios), Is.EqualTo(await ProgressAsync(android)));
        Assert.That((await _api.Client(android.AccessToken).GetAsync("/v1/worlds/mine")).StatusCode, Is.EqualTo(HttpStatusCode.OK), "first device stays signed in");
        Assert.That((await _api.Client().PostAsJsonAsync("/v1/auth/link", new { code = code.Code })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "single use");
    }

    [Test]
    public async Task LinkCodes_Expire_AndUnknownCodesFail()
    {
        TokenPair p = await _api.GuestAsync();
        var code = await (await _api.Client(p.AccessToken).PostAsync("/v1/auth/link-code", null)).Content.ReadFromJsonAsync<LinkCode>();
        Assert.That((await _api.Client().PostAsJsonAsync("/v1/auth/link", new { code = "AAAAAAAAAA" })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _api.Client().PostAsync("/v1/auth/link-code", null)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "needs a signed-in device");

        _api.Clock.Now = _api.Clock.Now.AddMinutes(11);
        try
        {
            Assert.That((await _api.Client().PostAsJsonAsync("/v1/auth/link", new { code = code!.Code })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally
        {
            _api.Clock.Now = DateTimeOffset.UtcNow;
        }
    }

    private static HttpRequestMessage PutSettings(string token, object body, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/v1/players/me/settings") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (ifMatch != null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    [Test]
    public async Task Settings_UseOptimisticConcurrency_AcrossDevices()
    {
        TokenPair a = await _api.GuestAsync();
        var code = await (await _api.Client(a.AccessToken).PostAsync("/v1/auth/link-code", null)).Content.ReadFromJsonAsync<LinkCode>();
        var b = await (await _api.Client().PostAsJsonAsync("/v1/auth/link", new { code = code!.Code })).Content.ReadFromJsonAsync<TokenPair>();
        HttpClient client = _api.Client();

        Assert.That((await client.SendAsync(PutSettings(a.AccessToken, new { sensitivity = 1.2 }, null))).StatusCode, Is.EqualTo((HttpStatusCode)428));
        Assert.That((await client.SendAsync(PutSettings(a.AccessToken, new { sensitivity = 1.2 }, "0"))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        // Device B still thinks there are no settings: its write is refused instead of silently overwriting A's.
        Assert.That((await client.SendAsync(PutSettings(b!.AccessToken, new { sensitivity = 0.5 }, "0"))).StatusCode, Is.EqualTo(HttpStatusCode.PreconditionFailed));
        var latest = await _api.Client(b.AccessToken).GetFromJsonAsync<JsonElement>("/v1/players/me/settings");
        Assert.That(latest.GetProperty("version").GetInt64(), Is.EqualTo(1));
        Assert.That(latest.GetProperty("settings").GetProperty("sensitivity").GetDouble(), Is.EqualTo(1.2));
        Assert.That((await client.SendAsync(PutSettings(b.AccessToken, new { sensitivity = 0.5 }, "1"))).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.That((await client.SendAsync(PutSettings(a.AccessToken, new { blob = new string('x', 9000) }, "2"))).StatusCode, Is.EqualTo((HttpStatusCode)400).Or.EqualTo((HttpStatusCode)413));
        Assert.That((await client.SendAsync(PutSettings(a.AccessToken, new[] { 1, 2 }, "2"))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "must be an object");
    }

    [Test]
    public void LevelCurve()
    {
        Assert.That(new[] { 0L, 99, 100, 399, 400, 900 }.Select(ProgressionStore.LevelFor), Is.EqualTo(new[] { 1, 1, 2, 2, 3, 4 }));
    }
}
