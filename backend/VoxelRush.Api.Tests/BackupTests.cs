using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;

namespace VoxelRush.Api.Tests;

/// <summary>SAVE-009: point-in-time restore with audit, retention, quotas and storage metrics.</summary>
[NonParallelizable]
public sealed class BackupTests
{
    private ApiHost _api = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _api = await ApiHost.StartAsync();

    [OneTimeTearDown]
    public async Task StopAsync() => await _api.DisposeAsync();

    private async Task<Npgsql.NpgsqlConnection> DbAsync()
    {
        var c = new Npgsql.NpgsqlConnection(_api.ConnectionString);
        await c.OpenAsync();
        return c;
    }

    private async Task SqlAsync(string sql, params object[] args)
    {
        await using Npgsql.NpgsqlConnection c = await DbAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new() { Value = a });
        }

        await cmd.ExecuteNonQueryAsync();
    }

    // Moves a world's saves back in time (presigned URLs use real time, so the clock itself stays put).
    private Task AgeAsync(Guid world, TimeSpan by) =>
        SqlAsync("UPDATE chunk_snapshots SET committed_at = committed_at - $2 WHERE world_id = $1 AND status = 'committed'", world, by);

    // The fixture clock is manual; keep it at real time so commits, restores and leases line up with UtcNow.
    private void Tick() => _api.Clock.Now = DateTimeOffset.UtcNow;

    private async Task<HttpStatusCode> UploadAsync(HttpClient service, Guid world, int x, long revision, byte[] bytes)
    {
        Tick();
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        HttpResponseMessage response = await service.PostAsJsonAsync($"/internal/v1/worlds/{world}/snapshots/upload", new { x, y = 0, z = 0, revision, sha256 = sha, sizeBytes = bytes.Length });
        if (!response.IsSuccessStatusCode)
        {
            return response.StatusCode;
        }

        PresignedUrl url = (await response.Content.ReadFromJsonAsync<PresignedUrl>())!;
        using var storage = new HttpClient();
        (await storage.PutAsync(url.Url, new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        return (await service.PostAsJsonAsync($"/internal/v1/worlds/{world}/snapshots/commit", new { x, y = 0, z = 0, revision })).StatusCode;
    }

    private async Task<HttpClient> SessionAsync(Guid world)
    {
        Tick();
        return await _api.LeasedServiceAsync(world, "srv-" + Guid.NewGuid().ToString("N")[..6]);
    }

    private async Task EndSessionAsync(Guid world, HttpClient session) =>
        await _api.Service().PostAsJsonAsync($"/internal/v1/worlds/{world}/lease/release",
            new { holder = session.DefaultRequestHeaders.GetValues("X-Lease-Holder").Single(), fencing = long.Parse(session.DefaultRequestHeaders.GetValues("X-Lease-Fencing").Single()) });

    private async Task<(long Revision, byte[] Bytes)?> LatestAsync(Guid world, int x)
    {
        HttpResponseMessage response = await _api.Service().GetAsync($"/internal/v1/worlds/{world}/snapshots/latest?x={x}&y=0&z=0");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var storage = new HttpClient();
        return (body.GetProperty("revision").GetInt64(), await storage.GetByteArrayAsync(body.GetProperty("url").GetString()));
    }

    private Task<HttpResponseMessage> RestoreAsync(TokenPair who, Guid world, DateTimeOffset? asOf)
    {
        Tick();
        return _api.Client(who.AccessToken).PostAsJsonAsync($"/v1/worlds/{world}/restore", new { asOf });
    }

    [Test]
    public async Task PointInTimeRestore_RollsBackToTheChosenSave_IsAudited_AndCanBeUndone()
    {
        TokenPair owner = await _api.GuestAsync();
        Guid world = (await _api.CreateWorldAsync(owner, "Time Travel")).Id;
        byte[] a1 = RandomNumberGenerator.GetBytes(300), a2 = RandomNumberGenerator.GetBytes(300), b1 = RandomNumberGenerator.GetBytes(300), c1 = RandomNumberGenerator.GetBytes(300);

        HttpClient first = await SessionAsync(world);
        Assert.That(await UploadAsync(first, world, 0, 5, a1), Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await UploadAsync(first, world, 1, 3, b1), Is.EqualTo(HttpStatusCode.NoContent));
        await EndSessionAsync(world, first);
        await AgeAsync(world, TimeSpan.FromHours(2));
        HttpClient second = await SessionAsync(world);
        await UploadAsync(second, world, 0, 9, a2);
        await UploadAsync(second, world, 2, 1, c1);
        await EndSessionAsync(world, second);
        await AgeAsync(world, TimeSpan.FromHours(1));   // first save ≈ 3 h ago, second ≈ 1 h ago

        var points = await _api.Client(owner.AccessToken).GetFromJsonAsync<RestorePoint[]>($"/v1/worlds/{world}/backups");
        Assert.That(points!.Select(p => p.Chunks), Is.EqualTo(new[] { 2, 2 }), "two saves of two chunks");

        var restored = await (await RestoreAsync(owner, world, DateTimeOffset.UtcNow.AddHours(-2))).Content.ReadFromJsonAsync<RestoreView>();
        Assert.That((restored!.Epoch, restored.Chunks, restored.ActorId), Is.EqualTo((1, 2, owner.PlayerId)));
        Assert.That((await LatestAsync(world, 0))!.Value.Bytes, Is.EqualTo(a1), "back to the first save");
        Assert.That((await LatestAsync(world, 1))!.Value.Revision, Is.EqualTo(3));
        Assert.That(await LatestAsync(world, 2), Is.Null, "a chunk first saved later is back to its generated state");
        var lease = await (await _api.Service().PostAsJsonAsync($"/internal/v1/worlds/{world}/lease/acquire", new { holder = "after-restore", ttlSeconds = 30 })).Content.ReadFromJsonAsync<LeaseView>();
        Assert.That(lease!.Epoch, Is.EqualTo(1), "servers learn their disk is from another timeline");
        await _api.Service().PostAsJsonAsync($"/internal/v1/worlds/{world}/lease/release", new { holder = "after-restore", fencing = lease.Fencing });

        // Undo: restore to just before the restore.
        var undo = await (await RestoreAsync(owner, world, DateTimeOffset.UtcNow.AddMinutes(-30))).Content.ReadFromJsonAsync<RestoreView>();
        Assert.That((undo!.Epoch, undo.Chunks), Is.EqualTo((2, 3)));
        Assert.That((await LatestAsync(world, 0))!.Value.Bytes, Is.EqualTo(a2));
        Assert.That((await LatestAsync(world, 2))!.Value.Bytes, Is.EqualTo(c1));

        // The new timeline can reuse revision numbers without touching old objects.
        HttpClient third = await SessionAsync(world);
        Assert.That(await UploadAsync(third, world, 1, 3, RandomNumberGenerator.GetBytes(200)), Is.EqualTo(HttpStatusCode.BadRequest), "same revision already in this epoch");
        Assert.That(await UploadAsync(third, world, 1, 4, RandomNumberGenerator.GetBytes(200)), Is.EqualTo(HttpStatusCode.NoContent));
        await EndSessionAsync(world, third);

        var audit = await _api.Client(owner.AccessToken).GetFromJsonAsync<RestoreView[]>($"/v1/worlds/{world}/restores");
        Assert.That(audit!.Select(r => r.Epoch), Is.EqualTo(new[] { 2, 1 }));
        Assert.That(audit!.All(r => r.ActorId == owner.PlayerId), Is.True);
    }

    [Test]
    public async Task Restore_IsOwnerOnly_NeedsAStoppedWorld_AndStaysInsideRetention()
    {
        TokenPair owner = await _api.GuestAsync(), editor = await _api.GuestAsync();
        Guid world = (await _api.CreateWorldAsync(owner, "Guarded")).Id;
        await _api.Client(owner.AccessToken).PostAsJsonAsync($"/v1/worlds/{world}/members", new { playerId = editor.PlayerId, role = "editor" });

        Assert.That((await RestoreAsync(editor, world, DateTimeOffset.UtcNow)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _api.Client(editor.AccessToken).GetAsync($"/v1/worlds/{world}/restores")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await RestoreAsync(owner, world, null)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await RestoreAsync(owner, world, DateTimeOffset.UtcNow.AddMinutes(5))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "future");
        Assert.That((await RestoreAsync(owner, world, DateTimeOffset.UtcNow.AddDays(-8))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "outside retention");

        HttpClient running = await SessionAsync(world);
        HttpResponseMessage live = await RestoreAsync(owner, world, DateTimeOffset.UtcNow);
        Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await live.Content.ReadAsStringAsync(), Does.Contain("world_running"));
        await EndSessionAsync(world, running);
        Assert.That((await RestoreAsync(owner, world, DateTimeOffset.UtcNow)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Quotas_LimitStoredBytes_CountingSharedObjectsOnce_AndWorldsPerOwner()
    {
        TokenPair owner = await _api.GuestAsync();
        Guid world = (await _api.CreateWorldAsync(owner, "Quota")).Id;
        await SqlAsync("INSERT INTO world_quotas (owner_id, max_worlds, max_stored_bytes, idle_shutdown_seconds) VALUES ($1, 1, 2500, 300)", owner.PlayerId);

        Assert.That((await _api.Client(owner.AccessToken).PostAsJsonAsync("/v1/worlds", new { name = "Second" })).StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "max_worlds from the quota row");

        HttpClient session = await SessionAsync(world);
        Assert.That(await UploadAsync(session, world, 0, 1, RandomNumberGenerator.GetBytes(1500)), Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await UploadAsync(session, world, 1, 1, RandomNumberGenerator.GetBytes(1500)), Is.EqualTo(HttpStatusCode.Forbidden), "1500 + 1500 > 2500");
        await EndSessionAsync(world, session);

        Assert.That((await RestoreAsync(owner, world, DateTimeOffset.UtcNow)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        session = await SessionAsync(world);
        Assert.That(await UploadAsync(session, world, 1, 1, RandomNumberGenerator.GetBytes(900)), Is.EqualTo(HttpStatusCode.NoContent),
            "the restored epoch points at the same object: still 1500 bytes stored");
        await EndSessionAsync(world, session);
    }

    [Test]
    public async Task Retention_KeepsNewestRevisionsAndTheWindow_AndDeletesOnlyUnreferencedObjects()
    {
        TokenPair owner = await _api.GuestAsync();
        Guid world = (await _api.CreateWorldAsync(owner, "Retention")).Id;
        HttpClient session = await SessionAsync(world);
        for (long revision = 1; revision <= 5; revision++)
        {
            await UploadAsync(session, world, 0, revision, RandomNumberGenerator.GetBytes(100));
        }

        await EndSessionAsync(world, session);
        await AgeAsync(world, TimeSpan.FromDays(8));
        // Restore to now: the new epoch shares revision 5's object.
        Assert.That((await RestoreAsync(owner, world, DateTimeOffset.UtcNow)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        session = await SessionAsync(world);
        await UploadAsync(session, world, 0, 6, RandomNumberGenerator.GetBytes(100));
        await EndSessionAsync(world, session);

        await using Npgsql.NpgsqlConnection c = await DbAsync();
        await using var rows = new Npgsql.NpgsqlCommand("SELECT epoch, revision FROM chunk_snapshots WHERE world_id = $1 ORDER BY epoch, revision", c) { Parameters = { new() { Value = world } } };
        var kept = new List<(int, long)>();
        await using (var r = await rows.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                kept.Add((r.GetInt32(0), r.GetInt64(1)));
            }
        }

        Assert.That(kept, Is.EqualTo(new[] { (1, 5L), (1, 6L) }), "old-epoch rows past the window are gone");
        string bucket = "snapshots-" + new Npgsql.NpgsqlConnectionStringBuilder(_api.ConnectionString).Database!.Replace("_", "-");
        using var s3 = new AmazonS3Client(new BasicAWSCredentials(Containers.S3AccessKey, Containers.S3SecretKey),
            new AmazonS3Config { ServiceURL = Containers.S3Url, ForcePathStyle = true, AuthenticationRegion = "auto" });
        async Task<bool> Exists(long revision)
        {
            try
            {
                await s3.GetObjectMetadataAsync(bucket, SnapshotStore.KeyFor(world, 0, 0, 0, 0, revision));
                return true;
            }
            catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        Assert.That(await Exists(5), Is.True, "still referenced by the restored epoch");
        Assert.That(await Exists(1) || await Exists(4), Is.False, "unreferenced objects deleted");
    }

    [Test]
    public async Task StorageMetrics_AreServiceOnly_AndPriced()
    {
        TokenPair owner = await _api.GuestAsync();
        Guid world = (await _api.CreateWorldAsync(owner, "Metered")).Id;
        HttpClient session = await SessionAsync(world);
        await UploadAsync(session, world, 0, 1, RandomNumberGenerator.GetBytes(2000));
        await EndSessionAsync(world, session);

        var metrics = await _api.Service().GetFromJsonAsync<StorageMetrics>("/internal/v1/metrics/storage");

        Assert.That(metrics!.StoredBytes, Is.GreaterThanOrEqualTo(2000));
        Assert.That(metrics.TopWorlds.Any(w => w.WorldId == world && w.Bytes == 2000), Is.True);
        Assert.That(metrics.EstimatedMonthlyCostUsd, Is.EqualTo(Math.Round(metrics.StoredBytes / 1_000_000_000m * 0.015m, 6)));
        Assert.That((await _api.Client(owner.AccessToken).GetAsync("/internal/v1/metrics/storage")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
