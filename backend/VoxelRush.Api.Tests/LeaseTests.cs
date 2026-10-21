using System.Net;
using System.Net.Http.Json;

namespace VoxelRush.Api.Tests;

/// <summary>SAVE-005: one authoritative server per world, with fencing against stale writers.</summary>
[NonParallelizable]
public sealed class LeaseTests
{
    private ApiHost _api = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _api = await ApiHost.StartAsync();

    [OneTimeTearDown]
    public async Task StopAsync() => await _api.DisposeAsync();

    private async Task<Guid> WorldAsync() => (await _api.CreateWorldAsync(await _api.GuestAsync(), "Leased")).Id;

    private Task<HttpResponseMessage> Acquire(Guid world, string holder, int ttl = 30) =>
        _api.Service().PostAsJsonAsync($"/internal/v1/worlds/{world}/lease/acquire", new { holder, ttlSeconds = ttl });

    [Test]
    public async Task ConcurrentAllocation_ExactlyOneServerWins()
    {
        Guid world = await WorldAsync();

        HttpResponseMessage[] attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Acquire(world, $"server-{i}")));

        Assert.That(attempts.Count(a => a.StatusCode == HttpStatusCode.OK), Is.EqualTo(1));
        Assert.That(attempts.Count(a => a.StatusCode == HttpStatusCode.Conflict), Is.EqualTo(19));
        LeaseView winner = (await attempts.Single(a => a.IsSuccessStatusCode).Content.ReadFromJsonAsync<LeaseView>())!;
        LeaseView seenByLoser = (await attempts.First(a => a.StatusCode == HttpStatusCode.Conflict).Content.ReadFromJsonAsync<LeaseView>())!;
        Assert.That(seenByLoser.Holder, Is.EqualTo(winner.Holder), "losers learn who holds it");
        Assert.That(winner.Fencing, Is.EqualTo(1));
    }

    [Test]
    public async Task Takeover_AfterExpiry_RaisesFencing_AndTheOldHolderIsFencedOff()
    {
        Guid world = await WorldAsync();
        LeaseView a = (await (await Acquire(world, "server-a", 10)).Content.ReadFromJsonAsync<LeaseView>())!;
        Assert.That((await Acquire(world, "server-b")).StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "a is alive");

        _api.Clock.Now = _api.Clock.Now.AddSeconds(11);
        try
        {
            LeaseView b = (await (await Acquire(world, "server-b")).Content.ReadFromJsonAsync<LeaseView>())!;
            Assert.That(b.Fencing, Is.EqualTo(a.Fencing + 1));

            // Server a wakes up (GC pause, network partition) and tries to renew and to write.
            Assert.That((await _api.Service().PostAsJsonAsync($"/internal/v1/worlds/{world}/lease/renew", new { holder = "server-a", fencing = a.Fencing, ttlSeconds = 30 })).StatusCode,
                Is.EqualTo(HttpStatusCode.Conflict));
            HttpClient stale = _api.Service();
            stale.DefaultRequestHeaders.Add("X-Lease-Holder", "server-a");
            stale.DefaultRequestHeaders.Add("X-Lease-Fencing", a.Fencing.ToString());
            var write = await stale.PostAsJsonAsync($"/internal/v1/worlds/{world}/snapshots/upload", new { x = 0, y = 0, z = 0, revision = 1, sha256 = new string('a', 64), sizeBytes = 10 });
            Assert.That(write.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "stale fencing number refused");
        }
        finally
        {
            _api.Clock.Now = DateTimeOffset.UtcNow;
        }
    }

    [Test]
    public async Task Renew_KeepsTheLease_AndAnExpiredLeaseIsLostEvenIfNobodyTookIt()
    {
        Guid world = await WorldAsync();
        LeaseView a = (await (await Acquire(world, "server-a", 10)).Content.ReadFromJsonAsync<LeaseView>())!;

        var renewed = await (await _api.Service().PostAsJsonAsync($"/internal/v1/worlds/{world}/lease/renew", new { holder = "server-a", fencing = a.Fencing, ttlSeconds = 60 }))
            .Content.ReadFromJsonAsync<LeaseView>();
        Assert.That(renewed!.ExpiresAt, Is.GreaterThan(a.ExpiresAt));

        _api.Clock.Now = _api.Clock.Now.AddSeconds(61);
        try
        {
            Assert.That((await _api.Service().PostAsJsonAsync($"/internal/v1/worlds/{world}/lease/renew", new { holder = "server-a", fencing = a.Fencing, ttlSeconds = 60 })).StatusCode,
                Is.EqualTo(HttpStatusCode.Conflict));
            LeaseView again = (await (await Acquire(world, "server-a")).Content.ReadFromJsonAsync<LeaseView>())!;
            Assert.That(again.Fencing, Is.EqualTo(a.Fencing + 1), "re-acquiring after expiry is a new term");
        }
        finally
        {
            _api.Clock.Now = DateTimeOffset.UtcNow;
        }
    }

    [Test]
    public async Task Release_LetsTheNextServerIn_AndReacquireByTheHolderKeepsItsTerm()
    {
        Guid world = await WorldAsync();
        LeaseView a = (await (await Acquire(world, "server-a")).Content.ReadFromJsonAsync<LeaseView>())!;
        LeaseView same = (await (await Acquire(world, "server-a")).Content.ReadFromJsonAsync<LeaseView>())!;
        Assert.That(same.Fencing, Is.EqualTo(a.Fencing), "idempotent acquire by the live holder");

        Assert.That((await _api.Service().PostAsJsonAsync($"/internal/v1/worlds/{world}/lease/release", new { holder = "server-a", fencing = a.Fencing })).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        LeaseView b = (await (await Acquire(world, "server-b")).Content.ReadFromJsonAsync<LeaseView>())!;
        Assert.That(b.Fencing, Is.EqualTo(a.Fencing + 1));
        Assert.That((await _api.Service().PostAsJsonAsync($"/internal/v1/worlds/{world}/lease/release", new { holder = "server-a", fencing = a.Fencing })).StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task InvalidRequests_AndUnknownWorlds()
    {
        Guid world = await WorldAsync();

        Assert.That((await Acquire(world, "", 30)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await Acquire(world, "s", 5)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "ttl below 10 s");
        Assert.That((await Acquire(world, "s", 3600)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "ttl above 300 s");
        Assert.That((await Acquire(Guid.NewGuid(), "s")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        TokenPair player = await _api.GuestAsync();
        Assert.That((await _api.Client(player.AccessToken).PostAsJsonAsync($"/internal/v1/worlds/{world}/lease/acquire", new { holder = "me", ttlSeconds = 30 })).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized), "players can't take worlds");
    }
}
