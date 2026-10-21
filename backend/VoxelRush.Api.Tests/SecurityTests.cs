using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using VoxelRush.Core.Networking;

namespace VoxelRush.Api.Tests;

/// <summary>VR-080 DoD: authentication, authorization, ticket and storage security against real PostgreSQL/MinIO.</summary>
[NonParallelizable]
public sealed class SecurityTests
{
    private ApiHost _api = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _api = await ApiHost.StartAsync();

    [OneTimeTearDown]
    public async Task StopAsync() => await _api.DisposeAsync();

    private static byte[] FromBase64Url(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4));

    // ---------- authentication ----------

    [TestCase("GET", "/v1/worlds/mine")]
    [TestCase("POST", "/v1/worlds")]
    [TestCase("POST", "/v1/worlds/00000000-0000-0000-0000-000000000001/join")]
    public async Task ProtectedEndpoints_RequireAToken(string method, string path)
    {
        var response = await _api.Client().SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { name = "x" }) });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ForgedTampered_AndAlgNoneTokens_AreRejected()
    {
        TokenPair guest = await _api.GuestAsync();
        string[] parts = guest.AccessToken.Split('.');
        string otherSub = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { sub = Guid.NewGuid(), aud = "voxelrush-client", iss = "voxelrush-api", exp = 4102444800 }))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string none = Convert.ToBase64String("{\"alg\":\"none\",\"typ\":\"JWT\"}"u8.ToArray()).TrimEnd('=');

        foreach (string token in new[] { $"{parts[0]}.{otherSub}.{parts[2]}", $"{none}.{parts[1]}.", guest.AccessToken[..^4] + "AAAA", "garbage" })
        {
            Assert.That((await _api.Client(token).GetAsync("/v1/worlds/mine")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), token);
        }

        Assert.That((await _api.Client(guest.AccessToken).GetAsync("/v1/worlds/mine")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task AccessTokens_Expire()
    {
        TokenPair guest = await _api.GuestAsync();
        _api.Clock.Now = _api.Clock.Now.AddMinutes(16);
        try
        {
            Assert.That((await _api.Client(guest.AccessToken).GetAsync("/v1/worlds/mine")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
        finally
        {
            _api.Clock.Now = DateTimeOffset.UtcNow;
        }
    }

    [Test]
    public async Task RefreshTokens_Rotate_AndReuseRevokesTheWholeLogin()
    {
        TokenPair first = await _api.GuestAsync();
        var refreshed = await (await _api.Client().PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = first.RefreshToken })).Content.ReadFromJsonAsync<TokenPair>();
        Assert.That(refreshed!.PlayerId, Is.EqualTo(first.PlayerId));
        Assert.That(refreshed.RefreshToken, Is.Not.EqualTo(first.RefreshToken));

        // An attacker replays the old token: refused, and the legitimate new token dies with it.
        Assert.That((await _api.Client().PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = first.RefreshToken })).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await _api.Client().PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refreshed.RefreshToken })).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await _api.Client().PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = "not-a-token" })).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await _api.Client().PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = new string('a', 500) })).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task RefreshTokens_Expire()
    {
        TokenPair guest = await _api.GuestAsync();
        _api.Clock.Now = _api.Clock.Now.AddDays(31);
        try
        {
            Assert.That((await _api.Client().PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = guest.RefreshToken })).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
        finally
        {
            _api.Clock.Now = DateTimeOffset.UtcNow;
        }
    }

    // ---------- authorization ----------

    [Test]
    public async Task PrivateWorlds_AreInvisible_AndOnlyOwnersChangeThem()
    {
        TokenPair owner = await _api.GuestAsync(), stranger = await _api.GuestAsync();
        WorldView world = await _api.CreateWorldAsync(owner, "Secret Base");

        Assert.That((await _api.Client(stranger.AccessToken).GetAsync($"/v1/worlds/{world.Id}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "existence not leaked");
        Assert.That((await _api.Client(stranger.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _api.Client(stranger.AccessToken).PatchAsJsonAsync($"/v1/worlds/{world.Id}", new { name = "Mine now" })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _api.Client(stranger.AccessToken).PostAsJsonAsync($"/v1/worlds/{world.Id}/members", new { playerId = stranger.PlayerId, role = "editor" })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _api.Client(stranger.AccessToken).PatchAsJsonAsync($"/v1/worlds/{world.Id}", new { name = "<bad>" })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "even with an invalid payload");
        Assert.That((await _api.Client(stranger.AccessToken).GetFromJsonAsync<WorldView[]>("/v1/worlds/mine"))!, Is.Empty);
    }

    [Test]
    public async Task Members_GetTheirRole_EditorsCantAdminister_PublicWorldsAdmitVisitors()
    {
        TokenPair owner = await _api.GuestAsync(), editor = await _api.GuestAsync(), visitor = await _api.GuestAsync();
        WorldView world = await _api.CreateWorldAsync(owner, "Team Track");
        Assert.That((await _api.Client(owner.AccessToken).PostAsJsonAsync($"/v1/worlds/{world.Id}/members", new { playerId = editor.PlayerId, role = "editor" })).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var join = await (await _api.Client(editor.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null)).Content.ReadFromJsonAsync<JoinResponse>();
        Assert.That(join!.Role, Is.EqualTo("editor"));
        Assert.That((await _api.Client(editor.AccessToken).PatchAsJsonAsync($"/v1/worlds/{world.Id}", new { visibility = "public" })).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await _api.Client(editor.AccessToken).PostAsJsonAsync($"/v1/worlds/{world.Id}/members", new { playerId = visitor.PlayerId, role = "editor" })).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        Assert.That((await _api.Client(owner.AccessToken).PatchAsJsonAsync($"/v1/worlds/{world.Id}", new { visibility = "public" })).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        var visit = await (await _api.Client(visitor.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null)).Content.ReadFromJsonAsync<JoinResponse>();
        Assert.That(visit!.Role, Is.EqualTo("visitor"), "public world: visitors only, never editors");

        Assert.That((await _api.Client(owner.AccessToken).DeleteAsync($"/v1/worlds/{world.Id}/members/{editor.PlayerId}")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        var demoted = await (await _api.Client(editor.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null)).Content.ReadFromJsonAsync<JoinResponse>();
        Assert.That(demoted!.Role, Is.EqualTo("visitor"), "removed member falls back to public visitor");
    }

    [Test]
    public async Task Quota_LimitsWorldsPerOwner()
    {
        TokenPair owner = await _api.GuestAsync();
        for (int i = 0; i < 3; i++)
        {
            await _api.CreateWorldAsync(owner, $"World {i}");
        }

        var fourth = await _api.Client(owner.AccessToken).PostAsJsonAsync("/v1/worlds", new { name = "One too many" });
        Assert.That(fourth.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [TestCase("")]
    [TestCase("   padded  ")]
    [TestCase("x'); DROP TABLE worlds; --")]
    [TestCase("<script>alert(1)</script>")]
    [TestCase("a-name-that-is-far-too-long-for-a-world-name")]
    [TestCase("tab\tname")]
    public async Task InvalidWorldInput_IsRejected(string name)
    {
        TokenPair owner = await _api.GuestAsync();

        var response = await _api.Client(owner.AccessToken).PostAsJsonAsync("/v1/worlds", new { name, visibility = "private" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await _api.Client(owner.AccessToken).PostAsJsonAsync("/v1/worlds", new { name = "Ok", visibility = "everyone" })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task OversizedBodies_AreRejected()
    {
        TokenPair owner = await _api.GuestAsync();
        var huge = new StringContent("{\"name\":\"" + new string('a', 64 * 1024) + "\"}", System.Text.Encoding.UTF8, "application/json");

        var response = await _api.Client(owner.AccessToken).PostAsync("/v1/worlds", huge);

        Assert.That((int)response.StatusCode, Is.EqualTo(413).Or.EqualTo(400));
    }

    // ---------- join tickets ----------

    [Test]
    public async Task JoinTicket_IsSignedForThisPlayerAndWorld_AndVerifiesOnTheGameServerOnce()
    {
        TokenPair owner = await _api.GuestAsync();
        WorldView world = await _api.CreateWorldAsync(owner, "Ticket World");

        var join = await (await _api.Client(owner.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null)).Content.ReadFromJsonAsync<JoinResponse>();
        byte[] ticket = FromBase64Url(join!.Ticket);
        var verifier = new TicketVerifier(ApiHost.TicketKey, world.Id);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.That(join.Endpoint, Is.EqualTo("game.test:7777"));
        Assert.That(verifier.Verify(ticket, now, out JoinTicket t), Is.EqualTo(TicketRejection.None));
        Assert.That((t.PlayerId, t.WorldId, t.Role), Is.EqualTo((owner.PlayerId, world.Id, WorldAccessRole.Owner)));
        Assert.That(t.ExpiresAt - t.IssuedAt, Is.EqualTo(60), "short-lived");
        Assert.That(verifier.Verify(ticket, now, out _), Is.EqualTo(TicketRejection.Replayed));
        Assert.That(new TicketVerifier(ApiHost.TicketKey, Guid.NewGuid()).Verify(ticket, now, out _), Is.EqualTo(TicketRejection.WrongWorld));
        Assert.That(new TicketVerifier(RandomNumberGenerator.GetBytes(32), world.Id).Verify(ticket, now, out _), Is.EqualTo(TicketRejection.BadSignature), "key never leaves the server side");
        Assert.That(join.Ticket, Does.Not.Contain(Convert.ToBase64String(ApiHost.TicketKey)));
    }

    // ---------- snapshots (internal) ----------

    [Test]
    public async Task InternalEndpoints_RequireTheServiceToken()
    {
        string path = $"/internal/v1/worlds/{Guid.NewGuid()}/snapshots/latest?x=0&y=0&z=0";

        Assert.That((await _api.Client().GetAsync(path)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await _api.Service("wrong-token-wrong-token-wrong-token").GetAsync(path)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        TokenPair player = await _api.GuestAsync();
        Assert.That((await _api.Client(player.AccessToken).GetAsync(path)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "a player token is not a service token");
    }

    [Test]
    public async Task Snapshot_UploadCommitDownload_VerifiesChecksum()
    {
        TokenPair owner = await _api.GuestAsync();
        WorldView world = await _api.CreateWorldAsync(owner, "Saved World");
        byte[] snapshot = RandomNumberGenerator.GetBytes(1500);
        string sha = Convert.ToHexString(SHA256.HashData(snapshot)).ToLowerInvariant();
        HttpClient service = _api.Service();

        var upload = await (await service.PostAsJsonAsync($"/internal/v1/worlds/{world.Id}/snapshots/upload", new { x = -3, y = 0, z = 7, revision = 42, sha256 = sha, sizeBytes = snapshot.Length }))
            .Content.ReadFromJsonAsync<PresignedUrl>();
        Assert.That(upload!.StorageKey, Is.EqualTo($"worlds/{world.Id:N}/chunks/-3_0_7/42.snap"));
        using var storage = new HttpClient();
        string tampered = upload.Url.Replace("/42.snap", "/43.snap");
        Assert.That((await storage.PutAsync(tampered, new ByteArrayContent(snapshot))).IsSuccessStatusCode, Is.False, "the signature covers the key");
        HttpResponseMessage put = await storage.PutAsync(upload.Url, new ByteArrayContent(snapshot));
        Assert.That(put.IsSuccessStatusCode, Is.True, $"{(int)put.StatusCode} {await put.Content.ReadAsStringAsync()}");

        Assert.That((await service.PostAsJsonAsync($"/internal/v1/worlds/{world.Id}/snapshots/commit", new { x = -3, y = 0, z = 7, revision = 42 })).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        var latest = await service.GetFromJsonAsync<JsonElement>($"/internal/v1/worlds/{world.Id}/snapshots/latest?x=-3&y=0&z=7");
        Assert.That(latest.GetProperty("revision").GetInt64(), Is.EqualTo(42));
        Assert.That(await storage.GetByteArrayAsync(latest.GetProperty("url").GetString()), Is.EqualTo(snapshot));
        Assert.That((await service.PostAsJsonAsync($"/internal/v1/worlds/{world.Id}/snapshots/upload", new { x = -3, y = 0, z = 7, revision = 42, sha256 = sha, sizeBytes = snapshot.Length })).StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest), "committed snapshots are immutable");
    }

    [Test]
    public async Task Snapshot_WithWrongContent_IsNeverIndexed()
    {
        TokenPair owner = await _api.GuestAsync();
        WorldView world = await _api.CreateWorldAsync(owner, "Corrupt World");
        byte[] promised = RandomNumberGenerator.GetBytes(800);
        HttpClient service = _api.Service();
        var upload = await (await service.PostAsJsonAsync($"/internal/v1/worlds/{world.Id}/snapshots/upload",
            new { x = 1, y = 1, z = 1, revision = 3, sha256 = Convert.ToHexString(SHA256.HashData(promised)), sizeBytes = promised.Length })).Content.ReadFromJsonAsync<PresignedUrl>();
        using var storage = new HttpClient();
        await storage.PutAsync(upload!.Url, new ByteArrayContent(RandomNumberGenerator.GetBytes(800)));

        Assert.That((await service.PostAsJsonAsync($"/internal/v1/worlds/{world.Id}/snapshots/commit", new { x = 1, y = 1, z = 1, revision = 3 })).StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await service.GetAsync($"/internal/v1/worlds/{world.Id}/snapshots/latest?x=1&y=1&z=1")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await service.PostAsJsonAsync($"/internal/v1/worlds/{world.Id}/snapshots/commit", new { x = 9, y = 9, z = 9, revision = 3 })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "nothing pending");
    }

    [TestCase(2_000_000, 0, 0, 1, 100)]
    [TestCase(0, 0, 0, -1, 100)]
    [TestCase(0, 0, 0, 1, 0)]
    [TestCase(0, 0, 0, 1, 1_000_000)]
    public async Task Snapshot_UploadRequests_AreBounded(int x, int y, int z, long revision, int size)
    {
        TokenPair owner = await _api.GuestAsync();
        WorldView world = await _api.CreateWorldAsync(owner, "Bounds");

        var response = await _api.Service().PostAsJsonAsync($"/internal/v1/worlds/{world.Id}/snapshots/upload",
            new { x, y, z, revision, sha256 = new string('a', 64), sizeBytes = size });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public void StorageKeys_AreBuiltFromIdsAndIntegersOnly()
    {
        string key = SnapshotStore.KeyFor(Guid.Parse("11111111-2222-3333-4444-555555555555"), -1, 2, -3, 9);

        Assert.That(key, Is.EqualTo("worlds/11111111222233334444555555555555/chunks/-1_2_-3/9.snap"));
        Assert.That(key, Does.Not.Contain(".."));
    }
}

/// <summary>Rate limiting needs its own host (low limits).</summary>
[NonParallelizable]
public sealed class RateLimitTests
{
    [Test]
    public async Task AuthEndpoints_AreRateLimited()
    {
        await using ApiHost api = await ApiHost.StartAsync(authPerMinute: 3);
        HttpClient client = api.Client();
        var codes = new List<HttpStatusCode>();
        for (int i = 0; i < 5; i++)
        {
            codes.Add((await client.PostAsync("/v1/auth/guest", null)).StatusCode);
        }

        Assert.That(codes.Take(3), Is.All.EqualTo(HttpStatusCode.OK));
        Assert.That(codes.Skip(3), Is.All.EqualTo(HttpStatusCode.TooManyRequests));
    }
}
