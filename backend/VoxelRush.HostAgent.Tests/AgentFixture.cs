using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace VoxelRush.HostAgent.Tests;

public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>In-memory Docker: containers by name, ports held by other processes, and a gate to hold launches mid-flight.</summary>
public sealed class FakeRuntime : IContainerRuntime
{
    public sealed class Container(WorldContainer info)
    {
        public WorldContainer Info { get; set; } = info;

        public bool Listening { get; set; }
    }

    private readonly object _lock = new();

    public Dictionary<string, Container> Containers { get; } = new();

    public List<ContainerSpec> Runs { get; } = new();

    public List<string> Removed { get; } = new();

    /// <summary>Ports another process holds: docker run reports "port is already allocated".</summary>
    public HashSet<int> ForeignPorts { get; } = new();

    public int LogReads { get; private set; }

    /// <summary>When set, launches wait on it (to overlap concurrent ensure calls).</summary>
    public TaskCompletionSource? RunGate { get; set; }

    /// <summary>Simulates another agent process creating the same container name first.</summary>
    public bool NameTakenElsewhere { get; set; }

    public bool Broken { get; set; }

    public void MarkListening(Guid world)
    {
        lock (_lock)
        {
            Containers[WorldHosts.ContainerName(world)].Listening = true;
        }
    }

    /// <summary>The server exited (idle shutdown, crash); --rm removed it.</summary>
    public void Exit(Guid world)
    {
        lock (_lock)
        {
            Containers.Remove(WorldHosts.ContainerName(world));
        }
    }

    public Task<IReadOnlyList<WorldContainer>> ListAsync(CancellationToken ct)
    {
        if (Broken)
        {
            throw new InvalidOperationException("docker ps failed: Cannot connect to the Docker daemon");
        }

        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<WorldContainer>>(Containers.Values.Select(c => c.Info).ToList());
        }
    }

    public async Task<(RunOutcome Outcome, string? Error)> RunAsync(ContainerSpec spec, CancellationToken ct)
    {
        if (RunGate != null)
        {
            await RunGate.Task.WaitAsync(ct);
        }

        lock (_lock)
        {
            Runs.Add(spec);
            if (NameTakenElsewhere || Containers.ContainsKey(spec.Name))
            {
                return (RunOutcome.NameInUse, null);
            }

            if (ForeignPorts.Contains(spec.HostPort))
            {
                return (RunOutcome.PortInUse, "Bind for 0.0.0.0:" + spec.HostPort + " failed: port is already allocated");
            }

            Containers[spec.Name] = new Container(new WorldContainer(spec.Name, spec.WorldId, spec.HostPort, true, spec.StartedAt));
            return (RunOutcome.Started, null);
        }
    }

    public Task<bool> HasLoggedAsync(string name, string marker, CancellationToken ct)
    {
        lock (_lock)
        {
            LogReads++;
            return Task.FromResult(marker == WorldHosts.ListeningMarker && Containers.TryGetValue(name, out Container? c) && c.Listening);
        }
    }

    public Task RemoveAsync(string name, CancellationToken ct)
    {
        lock (_lock)
        {
            Removed.Add(name);
            Containers.Remove(name);
            return Task.CompletedTask;
        }
    }
}

public sealed class AgentHost : IAsyncDisposable
{
    public const string ServiceToken = "test-service-token-0123456789abcdef";
    public static readonly string TicketKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly WebApplicationFactory<Program> _factory;

    private AgentHost(WebApplicationFactory<Program> factory, FakeRuntime docker, ManualClock clock)
    {
        _factory = factory;
        Docker = docker;
        Clock = clock;
    }

    public FakeRuntime Docker { get; }

    public ManualClock Clock { get; }

    public static Dictionary<string, string?> DefaultSettings() => new()
    {
        ["ServiceToken"] = ServiceToken,
        ["TicketKey"] = TicketKey,
        ["ApiUrl"] = "http://api:8080",
        ["ServerImage"] = "voxelrush-server:test",
        ["PublicHost"] = "play.test",
        ["Network"] = "voxelrush-backend",
        ["PortRangeStart"] = "7800",
        ["PortRangeEnd"] = "7809",
        ["MaxWorlds"] = "3",
        ["IdleShutdownSeconds"] = "120",
        ["StartTimeoutSeconds"] = "60",
        ["RetryAfterSeconds"] = "2",
    };

    /// <param name="extra">HostAgent__* overrides.</param>
    public static AgentHost Start(IDictionary<string, string?>? extra = null)
    {
        Dictionary<string, string?> settings = DefaultSettings();
        foreach ((string key, string? value) in extra ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        // Options are read before the host builds, so pass them as environment variables (restored right after).
        var env = settings.ToDictionary(kv => "HostAgent__" + kv.Key, kv => kv.Value);
        var previous = env.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach ((string key, string? value) in env)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        var docker = new FakeRuntime();
        var clock = new ManualClock();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IContainerRuntime>(docker);
            s.AddSingleton<TimeProvider>(clock);
        }));
        try
        {
            factory.CreateClient().Dispose();
        }
        finally
        {
            foreach ((string key, string? value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        return new AgentHost(factory, docker, clock);
    }

    public HttpClient Client(string? token = ServiceToken)
    {
        HttpClient client = _factory.CreateClient();
        if (token != null)
        {
            client.DefaultRequestHeaders.Add("X-Service-Token", token);
        }

        return client;
    }

    public Task<HttpResponseMessage> EnsureAsync(Guid world, string? token = ServiceToken) => Client(token).PostAsync($"/internal/v1/hosts/{world}/ensure", null);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();
}
