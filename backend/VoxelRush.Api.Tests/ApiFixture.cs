using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;

namespace VoxelRush.Api.Tests;

/// <summary>A controllable clock so token, ticket and refresh expiry can be tested without waiting.</summary>
public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// One PostgreSQL and one S3-compatible store for the whole run; each API host gets its own database and bucket.
/// SeaweedFS stands in for R2 (MinIO images left Docker Hub); an identity is configured so request signatures,
/// presigned URLs included, are really checked.
/// </summary>
[SetUpFixture]
public sealed class Containers
{
    public const string S3AccessKey = "voxelrush-test";
    // Dummy credential for the throwaway test container only.
    public const string S3SecretKey = "voxelrush-test-secret-0123456789"; // gitleaks:allow

    public static PostgreSqlContainer Postgres { get; private set; } = null!;

    public static IContainer S3 { get; private set; } = null!;

    public static string S3Url => $"http://{S3.Hostname}:{S3.GetMappedPublicPort(8333)}";

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        Postgres = new PostgreSqlBuilder("postgres:17.6-alpine").Build();
        string identities = "{\"identities\":[{\"name\":\"api\",\"credentials\":[{\"accessKey\":\"" + S3AccessKey + "\",\"secretKey\":\"" + S3SecretKey +
            "\"}],\"actions\":[\"Admin\",\"Read\",\"Write\",\"List\",\"Tagging\"]}]}";
        S3 = new ContainerBuilder("chrislusf/seaweedfs:3.97")
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(identities), "/etc/seaweedfs/s3.json")
            // Small volumes and a low free-space floor: the default 1% floor makes it read-only on a nearly full disk.
            .WithCommand("server", "-s3", "-s3.config=/etc/seaweedfs/s3.json", "-dir=/data", "-master.volumeSizeLimitMB=64", "-volume.minFreeSpace=50MiB")
            .WithPortBinding(8333, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8333))
            .Build();
        await Task.WhenAll(Postgres.StartAsync(), S3.StartAsync());
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        await Postgres.DisposeAsync();
        await S3.DisposeAsync();
    }
}

public sealed class ApiHost : IAsyncDisposable
{
    public static readonly byte[] TicketKey = RandomNumberGenerator.GetBytes(32);
    public const string ServiceToken = "test-service-token-0123456789abcdef";

    private readonly WebApplicationFactory<Program> _factory;

    private ApiHost(WebApplicationFactory<Program> factory, ManualClock clock, string connectionString)
    {
        _factory = factory;
        Clock = clock;
        ConnectionString = connectionString;
    }

    public ManualClock Clock { get; }

    /// <summary>This host's own database (tests may inspect stored rows).</summary>
    public string ConnectionString { get; }

    /// <summary>The host's container, to check which implementations configuration selected.</summary>
    public IServiceProvider Services => _factory.Services;

    /// <param name="extra">More VoxelRush__* settings for this host.</param>
    /// <param name="services">Service overrides, e.g. fake store endpoints behind named HttpClients.</param>
    public static async Task<ApiHost> StartAsync(int authPerMinute = 10_000, int playerPerMinute = 10_000, bool storage = true,
        IDictionary<string, string?>? extra = null, Action<IServiceCollection>? services = null)
    {
        string database = "api_" + Guid.NewGuid().ToString("N");
        await using (var admin = new Npgsql.NpgsqlConnection(Containers.Postgres.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new Npgsql.NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connection = new Npgsql.NpgsqlConnectionStringBuilder(Containers.Postgres.GetConnectionString()) { Database = database }.ConnectionString;
        var settings = new Dictionary<string, string?>
        {
            ["VoxelRush__ConnectionString"] = connection,
            ["VoxelRush__JwtSigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["VoxelRush__TicketKey"] = Convert.ToBase64String(TicketKey),
            ["VoxelRush__ServiceToken"] = ServiceToken,
            ["VoxelRush__GameServerEndpoint"] = "game.test:7777",
            ["VoxelRush__AuthRequestsPerMinute"] = authPerMinute.ToString(),
            ["VoxelRush__PlayerRequestsPerMinute"] = playerPerMinute.ToString(),
            ["VoxelRush__Snapshots__ServiceUrl"] = storage ? Containers.S3Url : string.Empty,
            ["VoxelRush__Snapshots__AccessKey"] = Containers.S3AccessKey,
            ["VoxelRush__Snapshots__SecretKey"] = Containers.S3SecretKey,
            ["VoxelRush__Snapshots__Bucket"] = "snapshots-" + database.Replace("_", "-"),
            ["VoxelRush__Snapshots__MaxSnapshotBytes"] = "4096",
        };

        foreach ((string key, string? value) in extra ?? new Dictionary<string, string?>())
        {
            settings["VoxelRush__" + key] = value;
        }

        // Options are read before the host builds, so pass them as environment variables (restored right after).
        var previous = settings.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach ((string key, string? value) in settings)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        var clock = new ManualClock();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<TimeProvider>(clock);
                services?.Invoke(s);
            }));
        try
        {
            factory.CreateClient().Dispose();   // boots the host: migrations + bucket
        }
        finally
        {
            foreach ((string key, string? value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        return new ApiHost(factory, clock, connection);
    }

    public HttpClient Client(string? accessToken = null)
    {
        HttpClient client = _factory.CreateClient();
        if (accessToken != null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    public HttpClient Service(string token = ServiceToken)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Service-Token", token);
        return client;
    }

    /// <summary>A service client holding the world's lease (headers set), as a game server would.</summary>
    public async Task<HttpClient> LeasedServiceAsync(Guid world, string holder = "test-server")
    {
        HttpResponseMessage response = await Service().PostAsJsonAsync($"/internal/v1/worlds/{world}/lease/acquire", new { holder, ttlSeconds = 120 });
        response.EnsureSuccessStatusCode();
        LeaseView lease = (await response.Content.ReadFromJsonAsync<LeaseView>())!;
        HttpClient client = Service();
        client.DefaultRequestHeaders.Add("X-Lease-Holder", holder);
        client.DefaultRequestHeaders.Add("X-Lease-Fencing", lease.Fencing.ToString());
        return client;
    }

    public async Task<TokenPair> GuestAsync()
    {
        HttpResponseMessage response = await Client().PostAsync("/v1/auth/guest", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenPair>())!;
    }

    public async Task<WorldView> CreateWorldAsync(TokenPair owner, string name = "My World", string visibility = "private")
    {
        HttpResponseMessage response = await Client(owner.AccessToken).PostAsJsonAsync("/v1/worlds", new { name, visibility });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorldView>())!;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();
}
