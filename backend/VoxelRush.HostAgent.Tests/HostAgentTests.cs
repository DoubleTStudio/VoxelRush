using System.Net;
using System.Net.Http.Json;
using VoxelRush.Hosting;

namespace VoxelRush.HostAgent.Tests;

/// <summary>ALLOC-001: one server container per world, port range, world cap, races and auth (fake Docker).</summary>
[NonParallelizable]
public sealed class HostAgentTests
{
    private AgentHost _agent = null!;

    [SetUp]
    public void Start() => _agent = AgentHost.Start();

    [TearDown]
    public async Task StopAsync() => await _agent.DisposeAsync();

    private static async Task<HostEnsureResponse> Body(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<HostEnsureResponse>())!;

    [Test]
    public async Task Ensure_RequiresTheServiceToken()
    {
        Guid world = Guid.NewGuid();

        Assert.That((await _agent.EnsureAsync(world, token: null)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await _agent.EnsureAsync(world, token: "wrong-token-0123456789abcdef0123456789")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await _agent.EnsureAsync(world, token: AgentHost.ServiceToken[..^1])).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(_agent.Docker.Runs, Is.Empty, "nothing launched for unauthenticated callers");
        Assert.That((await _agent.Client(token: null).GetAsync("/healthz")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task FirstEnsure_LaunchesTheServer_ThenReadyOnceItListens()
    {
        Guid world = Guid.NewGuid();

        HttpResponseMessage first = await _agent.EnsureAsync(world);

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(await Body(first), Is.EqualTo(new HostEnsureResponse("starting", RetryAfterSeconds: 2)));
        ContainerSpec spec = _agent.Docker.Runs.Single();
        Assert.That(spec.Name, Is.EqualTo("voxelrush-world-" + world.ToString("N")));
        Assert.That((spec.HostPort, spec.Image, spec.Network, spec.Volume), Is.EqualTo((7800, "voxelrush-server:test", "voxelrush-backend", "voxelrush-world-" + world.ToString("N"))));
        Assert.That(spec.Args, Is.EqualTo(new[] { "-port", "7777", "-maxPlayers", "8", "-worldDir", "/data", "-worldId", world.ToString(), "-idleShutdownSeconds", "120" }));
        Assert.That(spec.Environment.Keys, Is.EquivalentTo(new[] { "VOXELRUSH_TICKET_KEY", "VOXELRUSH_API_URL", "VOXELRUSH_SERVICE_TOKEN" }));

        Assert.That((await _agent.EnsureAsync(world)).StatusCode, Is.EqualTo(HttpStatusCode.Accepted), "still booting");
        _agent.Docker.MarkListening(world);
        HttpResponseMessage ready = await _agent.EnsureAsync(world);

        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await Body(ready), Is.EqualTo(new HostEnsureResponse("ready", Endpoint: "play.test:7800")));
        Assert.That(_agent.Docker.Runs, Has.Count.EqualTo(1), "launched once");
    }

    [Test]
    public async Task RunningContainer_IsReused_AndItsLogIsReadOnce()
    {
        Guid world = Guid.NewGuid();
        await _agent.EnsureAsync(world);
        _agent.Docker.MarkListening(world);

        for (int i = 0; i < 5; i++)
        {
            Assert.That((await Body(await _agent.EnsureAsync(world))).Endpoint, Is.EqualTo("play.test:7800"));
        }

        Assert.That(_agent.Docker.Runs, Has.Count.EqualTo(1));
        Assert.That(_agent.Docker.LogReads, Is.EqualTo(1), "listening state cached per container");
    }

    [Test]
    public async Task Ports_ComeFromTheRange_SkippingUsedAndForeignPorts()
    {
        _agent.Docker.ForeignPorts.Add(7801);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();

        await _agent.EnsureAsync(a);
        await _agent.EnsureAsync(b);
        _agent.Docker.Exit(a);
        await _agent.EnsureAsync(c);

        Assert.That(_agent.Docker.Runs.Select(r => (r.WorldId, r.HostPort)), Is.EqualTo(new[] { (a, 7800), (b, 7801), (b, 7802), (c, 7800) }),
            "7801 held by another process; 7800 reused after a's server exited");
        Assert.That(_agent.Docker.Removed, Is.EqualTo(new[] { WorldHosts.ContainerName(b) }), "the failed attempt is cleaned up");
    }

    [Test]
    public async Task MaxWorlds_ReportsNoCapacity_UntilAServerExits()
    {
        Guid[] worlds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        foreach (Guid w in worlds)
        {
            Assert.That((await _agent.EnsureAsync(w)).StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        }

        Guid late = Guid.NewGuid();
        HttpResponseMessage full = await _agent.EnsureAsync(late);

        Assert.That(full.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That((await Body(full)).Reason, Is.EqualTo("no_capacity"));
        Assert.That((await _agent.EnsureAsync(worlds[0])).StatusCode, Is.EqualTo(HttpStatusCode.Accepted), "worlds already hosted are unaffected");

        _agent.Docker.Exit(worlds[1]);
        Assert.That((await _agent.EnsureAsync(late)).StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(_agent.Docker.Runs.Last().HostPort, Is.EqualTo(7801));
    }

    [Test]
    public async Task ConcurrentEnsure_ForOneWorld_LaunchesOneServer()
    {
        Guid world = Guid.NewGuid();
        _agent.Docker.RunGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<HttpResponseMessage>[] calls = Enumerable.Range(0, 10).Select(_ => _agent.EnsureAsync(world)).ToArray();
        await Task.Delay(100);
        _agent.Docker.RunGate.SetResult();
        HttpResponseMessage[] responses = await Task.WhenAll(calls);

        Assert.That(responses.Select(r => r.StatusCode), Is.All.EqualTo(HttpStatusCode.Accepted));
        Assert.That(_agent.Docker.Runs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentEnsure_ForDifferentWorlds_GetDistinctPorts_AndRespectTheCap()
    {
        Guid[] worlds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();

        HttpResponseMessage[] responses = await Task.WhenAll(worlds.Select(w => _agent.EnsureAsync(w)));

        Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Accepted), Is.EqualTo(3));
        Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.ServiceUnavailable), Is.EqualTo(3));
        Assert.That(_agent.Docker.Runs.Select(r => r.HostPort), Is.EquivalentTo(new[] { 7800, 7801, 7802 }));
    }

    [Test]
    public async Task NameConflict_FromAnotherAgentProcess_MeansStarting()
    {
        _agent.Docker.NameTakenElsewhere = true;

        HttpResponseMessage response = await _agent.EnsureAsync(Guid.NewGuid());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(_agent.Docker.Removed, Is.Empty, "never remove a container another caller started");
    }

    [Test]
    public async Task HungBoot_IsRemovedAfterTheTimeout_AndTheNextJoinRelaunches()
    {
        Guid world = Guid.NewGuid();
        await _agent.EnsureAsync(world);

        _agent.Clock.Now += TimeSpan.FromSeconds(61);
        HttpResponseMessage failed = await _agent.EnsureAsync(world);

        Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That((await Body(failed)).Reason, Is.EqualTo("start_failed"));
        Assert.That(_agent.Docker.Removed, Is.EqualTo(new[] { WorldHosts.ContainerName(world) }));
        Assert.That((await _agent.EnsureAsync(world)).StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(_agent.Docker.Runs, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task ExitedContainer_IsClearedAndRelaunched()
    {
        Guid world = Guid.NewGuid();
        string name = WorldHosts.ContainerName(world);
        _agent.Docker.Containers[name] = new FakeRuntime.Container(new WorldContainer(name, world, 7800, false, _agent.Clock.Now));

        Assert.That((await _agent.EnsureAsync(world)).StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(_agent.Docker.Removed, Is.EqualTo(new[] { name }));
        Assert.That(_agent.Docker.Runs.Single().HostPort, Is.EqualTo(7800));
    }

    [Test]
    public async Task DockerDown_IsUnavailable_AndUnhealthy()
    {
        _agent.Docker.Broken = true;

        HttpResponseMessage response = await _agent.EnsureAsync(Guid.NewGuid());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That((await Body(response)).Reason, Is.EqualTo("host_unavailable"));
        Assert.That((await _agent.Client(token: null).GetAsync("/healthz")).StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task EmptyWorldId_IsRejected()
    {
        Assert.That((await _agent.EnsureAsync(Guid.Empty)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await _agent.Client().PostAsync("/internal/v1/hosts/not-a-guid/ensure", null)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}

/// <summary>The docker CLI adapter's pure parts: argument list (no secrets) and <c>docker ps</c> parsing.</summary>
public sealed class DockerCliTests
{
    [Test]
    public void RunArguments_PassSecretsByNameOnly()
    {
        var world = Guid.NewGuid();
        var spec = new ContainerSpec("voxelrush-world-x", "img:1", world, 7805, "voxelrush-world-x", "net",
            // Dummy values: the test checks they never reach the argument list.
            new Dictionary<string, string> { ["VOXELRUSH_TICKET_KEY"] = "c2VjcmV0LXRpY2tldC1rZXk=", ["VOXELRUSH_SERVICE_TOKEN"] = "secret-service-token" }, // gitleaks:allow
            ["-port", "7777"], DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

        List<string> args = DockerCli.RunArguments(spec);

        Assert.That(string.Join(' ', args), Does.Not.Contain("c2VjcmV0").And.Not.Contain("secret-service-token"));
        Assert.That(string.Join(' ', args), Does.Contain("--env VOXELRUSH_SERVICE_TOKEN --env VOXELRUSH_TICKET_KEY img:1 -port 7777"));
        Assert.That(string.Join(' ', args), Does.Contain("--publish 7805:7777/udp").And.Contain("--volume voxelrush-world-x:/data").And.Contain("--network net"));
        Assert.That(args, Does.Contain($"voxelrush.world={world}").And.Contain("voxelrush.port=7805").And.Contain("voxelrush.started=1700000000"));
        Assert.That(args, Does.Contain("--read-only").And.Contain("--rm").And.Contain("ALL"));
    }

    [Test]
    public void ParseList_ReadsLabelsAndStates()
    {
        var world = Guid.NewGuid();
        string output = $"voxelrush-world-a\trunning\t{world}\t7801\t1700000000\nvoxelrush-world-b\texited\t{world}\t7802\t1700000001\nhand-started\tcreated\t\t\t\n";

        IReadOnlyList<WorldContainer> list = DockerCli.ParseList(output);

        Assert.That(list, Has.Count.EqualTo(3));
        Assert.That(list[0], Is.EqualTo(new WorldContainer("voxelrush-world-a", world, 7801, true, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000))));
        Assert.That(list[1].Active, Is.False);
        Assert.That((list[2].Active, list[2].Port), Is.EqualTo((true, 0)), "unlabelled containers still take a slot");
    }
}

/// <summary>Bad configuration fails at startup rather than on the first join.</summary>
public sealed class HostAgentOptionsTests
{
    private static HostAgentOptions Valid() => new()
    {
        ServiceToken = AgentHost.ServiceToken,
        TicketKey = AgentHost.TicketKey,
        ApiUrl = "http://api:8080",
        ServerImage = "img",
        PublicHost = "play.test",
    };

    [Test]
    public void Defaults_WithSecrets_AreValid() => Assert.DoesNotThrow(Valid().Validate);

    [TestCase(nameof(HostAgentOptions.ServiceToken), "short")]
    [TestCase(nameof(HostAgentOptions.TicketKey), "not base64!")]
    [TestCase(nameof(HostAgentOptions.ApiUrl), "api:8080")]
    [TestCase(nameof(HostAgentOptions.ServerImage), "")]
    [TestCase(nameof(HostAgentOptions.MaxWorlds), "101")]
    [TestCase(nameof(HostAgentOptions.PortRangeEnd), "7000")]
    [TestCase(nameof(HostAgentOptions.MaxPlayers), "9")]
    [TestCase(nameof(HostAgentOptions.IdleShutdownSeconds), "1")]
    public void Invalid_Throws(string property, string value)
    {
        HostAgentOptions options = Valid();
        System.Reflection.PropertyInfo p = typeof(HostAgentOptions).GetProperty(property)!;
        p.SetValue(options, p.PropertyType == typeof(int) ? int.Parse(value) : value);

        Assert.That(options.Validate, Throws.InvalidOperationException);
    }
}
