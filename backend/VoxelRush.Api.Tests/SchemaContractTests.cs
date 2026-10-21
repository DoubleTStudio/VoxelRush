using Npgsql;

namespace VoxelRush.Api.Tests;

/// <summary>SAVE-001: migrations apply cleanly and idempotently, and the schema enforces the data contract.</summary>
[NonParallelizable]
public sealed class SchemaContractTests
{
    private NpgsqlDataSource _db = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        string database = "schema_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(Containers.Postgres.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
        }

        _db = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(Containers.Postgres.GetConnectionString()) { Database = database }.ConnectionString).Build();
        await Migrations.ApplyAsync(_db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    [OneTimeTearDown]
    public async Task StopAsync() => await _db.DisposeAsync();

    private async Task<long> ScalarAsync(string sql, params object[] args)
    {
        await using NpgsqlCommand cmd = _db.CreateCommand(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new() { Value = a });
        }

        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task ExecAsync(string sql, params object[] args)
    {
        await using NpgsqlCommand cmd = _db.CreateCommand(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new() { Value = a });
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> PlayerAsync()
    {
        var id = Guid.NewGuid();
        await ExecAsync("INSERT INTO players (id, kind) VALUES ($1, 'guest')", id);
        return id;
    }

    [Test]
    public async Task Migrations_AreRecorded_AndReapplyingIsANoOp()
    {
        long before = await ScalarAsync("SELECT count(*) FROM schema_migrations");
        await Migrations.ApplyAsync(_db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.That(before, Is.GreaterThanOrEqualTo(2));
        Assert.That(await ScalarAsync("SELECT count(*) FROM schema_migrations"), Is.EqualTo(before));
        Assert.That(await ScalarAsync("SELECT count(*) FROM schema_migrations WHERE id IN ('001_initial.sql', '002_progression_quotas.sql')"), Is.EqualTo(2));
    }

    [TestCase("players", "id,kind,created_at,last_seen_at")]
    [TestCase("worlds", "id,owner_id,name,seed,generator_version,schema_version,visibility,status,created_at,last_saved_at,revision")]
    [TestCase("world_members", "world_id,player_id,role,invited_by,expires_at")]
    [TestCase("chunk_snapshots", "world_id,chunk_x,chunk_y,chunk_z,revision,storage_key,sha256,size_bytes,status,created_at")]
    [TestCase("player_progress", "player_id,xp,level,version,updated_at")]
    [TestCase("player_settings", "player_id,settings,version,updated_at")]
    [TestCase("reward_grants", "grant_key,player_id,kind,amount,source,created_at")]
    [TestCase("world_quotas", "owner_id,max_worlds,max_stored_bytes,idle_shutdown_seconds")]
    public async Task Tables_HaveTheContractColumns(string table, string columns)
    {
        var actual = new List<string>();
        await using NpgsqlCommand cmd = _db.CreateCommand("SELECT column_name FROM information_schema.columns WHERE table_name = $1 ORDER BY ordinal_position");
        cmd.Parameters.Add(new() { Value = table });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            actual.Add(r.GetString(0));
        }

        Assert.That(actual, Is.EqualTo(columns.Split(',')));
    }

    [TestCase("INSERT INTO player_progress (player_id, xp) VALUES ($1, -1)")]
    [TestCase("INSERT INTO player_progress (player_id, level) VALUES ($1, 0)")]
    [TestCase("INSERT INTO reward_grants (grant_key, player_id, kind, amount, source) VALUES ('k', $1, 'gold', 5, 'race')")]
    [TestCase("INSERT INTO reward_grants (grant_key, player_id, kind, amount, source) VALUES ('k', $1, 'xp', 0, 'race')")]
    [TestCase("INSERT INTO world_quotas (owner_id, max_worlds, max_stored_bytes, idle_shutdown_seconds) VALUES ($1, 1, -5, 300)")]
    [TestCase("INSERT INTO worlds (id, owner_id, name, seed, generator_version, schema_version, visibility) VALUES (gen_random_uuid(), $1, 'x', 1, 1, 1, 'everyone')")]
    [TestCase("INSERT INTO player_settings (player_id, settings) VALUES ($1, jsonb_build_object('blob', repeat('x', 9000)))")]
    public async Task Constraints_RejectOutOfContractRows(string sql)
    {
        Guid player = await PlayerAsync();

        Assert.ThrowsAsync<PostgresException>(async () => await ExecAsync(sql, player));
    }

    [Test]
    public async Task RewardGrantKeys_AreUnique_SoGrantsCantDouble()
    {
        Guid player = await PlayerAsync();
        await ExecAsync("INSERT INTO reward_grants (grant_key, player_id, kind, amount, source) VALUES ('race:r1:p', $1, 'xp', 50, 'race')", player);

        var e = Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecAsync("INSERT INTO reward_grants (grant_key, player_id, kind, amount, source) VALUES ('race:r1:p', $1, 'xp', 50, 'race')", player));
        Assert.That(e!.SqlState, Is.EqualTo(PostgresErrorCodes.UniqueViolation));
    }

    [Test]
    public async Task DeletingAPlayer_RemovesTheirSecretsAndProgress()
    {
        Guid player = await PlayerAsync();
        await ExecAsync("INSERT INTO player_progress (player_id, xp) VALUES ($1, 10)", player);
        await ExecAsync("INSERT INTO refresh_tokens (token_hash, player_id, family_id, expires_at) VALUES (decode(md5($1::text), 'hex'), $1, gen_random_uuid(), now() + interval '1 day')", player);

        await ExecAsync("DELETE FROM players WHERE id = $1", player);

        Assert.That(await ScalarAsync("SELECT count(*) FROM refresh_tokens WHERE player_id = $1", player), Is.Zero);
        Assert.That(await ScalarAsync("SELECT count(*) FROM player_progress WHERE player_id = $1", player), Is.Zero);
    }
}
