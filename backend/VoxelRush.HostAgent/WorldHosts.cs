using System.Collections.Concurrent;
using System.Globalization;
using VoxelRush.Hosting;

namespace VoxelRush.HostAgent;

/// <summary>Starts at most one game server container per world on this host, within the port range and world cap.</summary>
public sealed class WorldHosts(IContainerRuntime docker, HostAgentOptions options, TimeProvider clock, ILogger<WorldHosts> log)
{
    public const string ListeningMarker = "event=server_listening";

    // Bounds a run of foreign processes squatting on our ports while the gate is held.
    private const int MaxPortAttempts = 8;

    // One gate for all worlds: launches are rare, and it makes the capacity check and port choice atomic too.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Containers already seen listening (name@start), so docker logs is read once per container.
    private readonly ConcurrentDictionary<string, byte> _listening = new();

    public static string ContainerName(Guid world) => "voxelrush-world-" + world.ToString("N");

    public async Task<HostEnsureResponse> EnsureAsync(Guid world, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await EnsureLockedAsync(world, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ActiveCountAsync(CancellationToken ct) => (await docker.ListAsync(ct)).Count(c => c.Active);

    private async Task<HostEnsureResponse> EnsureLockedAsync(Guid world, CancellationToken ct)
    {
        string name = ContainerName(world);
        IReadOnlyList<WorldContainer> containers = await docker.ListAsync(ct);
        foreach (string key in _listening.Keys.Except(containers.Where(c => c.Active).Select(Key)))
        {
            _listening.TryRemove(key, out _);
        }

        WorldContainer? mine = containers.FirstOrDefault(c => c.Name == name);
        if (mine is { Active: true })
        {
            return await StatusAsync(mine, ct);
        }

        if (mine != null)
        {
            // Exited but not yet removed: clear the name for a fresh start.
            await docker.RemoveAsync(name, ct);
        }

        List<WorldContainer> active = containers.Where(c => c.Active).ToList();
        if (active.Count >= options.MaxWorlds)
        {
            log.LogWarning("world_host_full world={World} active={Active} max={Max}", world, active.Count, options.MaxWorlds);
            return Unavailable(HostEnsureResponse.NoCapacity);
        }

        HashSet<int> used = active.Select(c => c.Port).ToHashSet();
        int attempts = 0;
        for (int port = options.PortRangeStart; port <= options.PortRangeEnd && attempts < MaxPortAttempts; port++)
        {
            if (used.Contains(port))
            {
                continue;
            }

            attempts++;
            (RunOutcome outcome, string? error) = await docker.RunAsync(Spec(world, name, port), ct);
            switch (outcome)
            {
                case RunOutcome.Started:
                    log.LogInformation("world_server_launched world={World} container={Container} port={Port}", world, name, port);
                    return Starting();
                case RunOutcome.NameInUse:
                    // Another agent process won the race for this world; it is booting.
                    return Starting();
                case RunOutcome.PortInUse:
                    log.LogWarning("world_port_in_use world={World} port={Port}", world, port);
                    await docker.RemoveAsync(name, ct);
                    continue;
                default:
                    log.LogError("world_server_launch_failed world={World} port={Port} error={Error}", world, port, error);
                    return Unavailable(HostEnsureResponse.StartFailed);
            }
        }

        log.LogWarning("world_host_no_port world={World} range={Start}-{End}", world, options.PortRangeStart, options.PortRangeEnd);
        return Unavailable(HostEnsureResponse.NoCapacity);
    }

    private async Task<HostEnsureResponse> StatusAsync(WorldContainer container, CancellationToken ct)
    {
        string key = Key(container);
        if (_listening.ContainsKey(key) || await docker.HasLoggedAsync(container.Name, ListeningMarker, ct))
        {
            _listening[key] = 0;
            return new HostEnsureResponse(HostEnsureResponse.Ready, Endpoint: $"{options.PublicHost}:{container.Port.ToString(CultureInfo.InvariantCulture)}");
        }

        if (clock.GetUtcNow() - container.StartedAt > TimeSpan.FromSeconds(options.StartTimeoutSeconds))
        {
            // Hung boot (or a lease held elsewhere looping): free the slot; the next join starts it again.
            log.LogError("world_server_start_timeout world={World} container={Container}", container.WorldId, container.Name);
            await docker.RemoveAsync(container.Name, ct);
            return Unavailable(HostEnsureResponse.StartFailed);
        }

        return Starting();
    }

    private ContainerSpec Spec(Guid world, string name, int port) => new(
        name,
        options.ServerImage,
        world,
        port,
        options.VolumePrefix + world.ToString("N"),
        string.IsNullOrEmpty(options.Network) ? null : options.Network,
        // Names match the server's TicketKeyEnvironment / RewardClient environment variables.
        new Dictionary<string, string>
        {
            ["VOXELRUSH_TICKET_KEY"] = options.TicketKey,
            ["VOXELRUSH_API_URL"] = options.ApiUrl,
            ["VOXELRUSH_SERVICE_TOKEN"] = options.ServiceToken,
        },
        [
            "-port", DockerCli.ContainerPort.ToString(CultureInfo.InvariantCulture),
            "-maxPlayers", options.MaxPlayers.ToString(CultureInfo.InvariantCulture),
            "-worldDir", "/data",
            "-worldId", world.ToString(),
            "-idleShutdownSeconds", options.IdleShutdownSeconds.ToString(CultureInfo.InvariantCulture),
        ],
        clock.GetUtcNow());

    private static string Key(WorldContainer c) => c.Name + "@" + c.StartedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private HostEnsureResponse Starting() => new(HostEnsureResponse.Starting, RetryAfterSeconds: options.RetryAfterSeconds);

    private HostEnsureResponse Unavailable(string reason) => new(HostEnsureResponse.Unavailable, RetryAfterSeconds: options.RetryAfterSeconds * 5, Reason: reason);
}
