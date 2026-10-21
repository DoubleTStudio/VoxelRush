using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VoxelRush.Hosting;

namespace VoxelRush.HostAgent.Tests;

/// <summary>Records launches instead of starting processes; tests drive output and exits by hand.</summary>
public sealed class FakeLauncher : IProcessLauncher
{
    public sealed class FakeProcess(ProcessSpec spec, Action<string> output, int id) : IServerProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProcessSpec Spec { get; } = spec;

        public int Id { get; } = id;

        public Task<int> Exited => _exit.Task;

        public bool Terminated { get; private set; }

        public bool Killed { get; private set; }

        /// <summary>False simulates a server that hangs on SIGTERM.</summary>
        public bool ExitsOnTerminate { get; set; } = true;

        public void Emit(string line) => output(line);

        public void Exit(int code = 0) => _exit.TrySetResult(code);

        public void Terminate()
        {
            Terminated = true;
            if (ExitsOnTerminate)
            {
                Exit();
            }
        }

        public void Kill()
        {
            Killed = true;
            Exit(137);
        }
    }

    public List<FakeProcess> Started { get; } = new();

    /// <summary>UDP ports another process holds.</summary>
    public HashSet<int> BusyPorts { get; } = new();

    public bool FailStart { get; set; }

    public IServerProcess Start(ProcessSpec spec, Action<string> onOutput)
    {
        if (FailStart)
        {
            throw new Win32Exception(2, "No such file or directory");
        }

        var process = new FakeProcess(spec, onOutput, 1000 + Started.Count);
        Started.Add(process);
        return process;
    }

    public bool IsUdpPortFree(int port) => !BusyPorts.Contains(port);
}

/// <summary>DEV-001: native server processes behind the same ensure contract as containers (fake launcher, no real processes).</summary>
public sealed class ProcessRuntimeTests
{
    private const string Executable = "/opt/voxelrush/server-mac/VoxelRush";
    private string _root = null!;
    private HostAgentOptions _options = null!;
    private FakeLauncher _launcher = null!;
    private ProcessRuntime _runtime = null!;
    private ManualClock _clock = null!;
    private WorldHosts _hosts = null!;

    [SetUp]
    public void Start()
    {
        _root = Path.Combine(Path.GetTempPath(), "voxelrush-agent-" + Guid.NewGuid().ToString("N"));
        _options = new HostAgentOptions
        {
            Runtime = HostAgentOptions.ProcessRuntime,
            ServiceToken = AgentHost.ServiceToken,
            TicketKey = AgentHost.TicketKey,
            ApiUrl = "http://127.0.0.1:18090",
            ServerExecutable = Executable,
            WorldDataRoot = _root,
            PublicHost = "play.test",
            PortRangeStart = 7800,
            PortRangeEnd = 7809,
            MaxWorlds = 2,
            IdleShutdownSeconds = 120,
            StartTimeoutSeconds = 60,
        };
        _options.Validate();
        _launcher = new FakeLauncher();
        _runtime = new ProcessRuntime(_launcher, _options, NullLogger<ProcessRuntime>.Instance) { StopGrace = TimeSpan.FromMilliseconds(200) };
        _clock = new ManualClock();
        _hosts = new WorldHosts(_runtime, _options, _clock, NullLogger<WorldHosts>.Instance);
    }

    [TearDown]
    public void Stop()
    {
        _runtime.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string ReadShared(string path)
    {
        // The runtime still has the log open for writing.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    [Test]
    public async Task Launch_UsesTheHostPortAndAWorldDirectory_WithSecretsOnlyInTheEnvironment()
    {
        Guid world = Guid.NewGuid();

        HostEnsureResponse response = await _hosts.EnsureAsync(world, CancellationToken.None);

        Assert.That(response, Is.EqualTo(new HostEnsureResponse("starting", RetryAfterSeconds: 2)));
        ProcessSpec spec = _launcher.Started.Single().Spec;
        string worldDir = Path.Combine(_root, "voxelrush-world-" + world.ToString("N"));
        Assert.That(spec.FileName, Is.EqualTo(Executable));
        Assert.That(spec.Args, Is.EqualTo(new[]
        {
            "-batchmode", "-nographics", "-logFile", "-",
            "-port", "7800", "-maxPlayers", "8", "-worldDir", worldDir, "-worldId", world.ToString(), "-idleShutdownSeconds", "120",
        }));
        Assert.That(Directory.Exists(worldDir), "world directory created before launch");
        Assert.That(spec.Environment, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["VOXELRUSH_TICKET_KEY"] = AgentHost.TicketKey,
            ["VOXELRUSH_API_URL"] = "http://127.0.0.1:18090",
            ["VOXELRUSH_SERVICE_TOKEN"] = AgentHost.ServiceToken,
        }));
        Assert.That(string.Join(' ', spec.Args), Does.Not.Contain(AgentHost.TicketKey).And.Not.Contain(AgentHost.ServiceToken));
    }

    [Test]
    public async Task Ready_OnceStdoutShowsListening_AndOutputGoesToTheWorldLog()
    {
        Guid world = Guid.NewGuid();
        await _hosts.EnsureAsync(world, CancellationToken.None);
        FakeLauncher.FakeProcess server = _launcher.Started.Single();

        server.Emit("[Server] event=server_tickets_required worldId=" + world);
        Assert.That((await _hosts.EnsureAsync(world, CancellationToken.None)).Status, Is.EqualTo("starting"));
        server.Emit("[Server] event=server_listening port=7800");
        HostEnsureResponse ready = await _hosts.EnsureAsync(world, CancellationToken.None);

        Assert.That(ready, Is.EqualTo(new HostEnsureResponse("ready", Endpoint: "play.test:7800")));
        Assert.That(_launcher.Started, Has.Count.EqualTo(1), "launched once");
        string log = ReadShared(_runtime.LogPath(WorldHosts.ContainerName(world)));
        Assert.That(log, Does.Contain("event=server_tickets_required").And.Contain("event=server_listening"));
    }

    [Test]
    public async Task ProcessExit_FreesTheSlotAndPort_AndTheNextJoinRelaunches()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), late = Guid.NewGuid();
        await _hosts.EnsureAsync(a, CancellationToken.None);
        await _hosts.EnsureAsync(b, CancellationToken.None);

        Assert.That((await _hosts.EnsureAsync(late, CancellationToken.None)).Reason, Is.EqualTo("no_capacity"));

        _launcher.Started[0].Exit();
        Assert.That((await _hosts.EnsureAsync(late, CancellationToken.None)).Status, Is.EqualTo("starting"));
        _launcher.Started[1].Exit();
        Assert.That((await _hosts.EnsureAsync(a, CancellationToken.None)).Status, Is.EqualTo("starting"), "an exited world starts again");

        Assert.That(_launcher.Started.Select(p => p.Spec.Args[5]), Is.EqualTo(new[] { "7800", "7801", "7800", "7801" }), "freed ports are reused");
    }

    [Test]
    public async Task PortHeldByAnotherProcess_IsSkipped()
    {
        _launcher.BusyPorts.Add(7800);

        await _hosts.EnsureAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.That(_launcher.Started.Single().Spec.Args[5], Is.EqualTo("7801"));
    }

    [Test]
    public async Task HungBoot_IsKilled_AndTheNextJoinRelaunches()
    {
        Guid world = Guid.NewGuid();
        await _hosts.EnsureAsync(world, CancellationToken.None);

        _clock.Now += TimeSpan.FromSeconds(61);
        HostEnsureResponse failed = await _hosts.EnsureAsync(world, CancellationToken.None);

        Assert.That(failed.Reason, Is.EqualTo("start_failed"));
        Assert.That(_launcher.Started[0].Killed);
        Assert.That((await _hosts.EnsureAsync(world, CancellationToken.None)).Status, Is.EqualTo("starting"));
        Assert.That(_launcher.Started, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task LaunchFailure_IsStartFailed_AndHoldsNoSlot()
    {
        _launcher.FailStart = true;

        HostEnsureResponse response = await _hosts.EnsureAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.That((response.Status, response.Reason), Is.EqualTo(("unavailable", "start_failed")));
        Assert.That(await _runtime.ListAsync(CancellationToken.None), Is.Empty);
    }

    [Test]
    public async Task Shutdown_TerminatesServers_KillsThoseThatHang_AndRefusesNewLaunches()
    {
        Guid polite = Guid.NewGuid(), stuck = Guid.NewGuid();
        await _hosts.EnsureAsync(polite, CancellationToken.None);
        await _hosts.EnsureAsync(stuck, CancellationToken.None);
        _launcher.Started[1].ExitsOnTerminate = false;

        _runtime.Dispose();

        Assert.That(_launcher.Started.Select(p => (p.Terminated, p.Killed)), Is.EqualTo(new[] { (true, false), (true, true) }));
        Assert.That((await _hosts.EnsureAsync(Guid.NewGuid(), CancellationToken.None)).Reason, Is.EqualTo("start_failed"));
        Assert.That(_launcher.Started, Has.Count.EqualTo(2));
    }

    [Test]
    public void StartInfo_KeepsSecretsInTheEnvironment_AndDropsTheAgentsOwnConfig()
    {
        var spec = new ProcessSpec(Executable, ["-port", "7800"],
            new Dictionary<string, string> { ["VOXELRUSH_SERVICE_TOKEN"] = AgentHost.ServiceToken }, _root);
        Environment.SetEnvironmentVariable("HostAgent__ServiceToken", AgentHost.ServiceToken);
        try
        {
            System.Diagnostics.ProcessStartInfo info = OsProcessLauncher.StartInfo(spec);

            Assert.That(info.ArgumentList, Is.EqualTo(new[] { "-port", "7800" }));
            Assert.That(info.Environment["VOXELRUSH_SERVICE_TOKEN"], Is.EqualTo(AgentHost.ServiceToken));
            Assert.That(info.Environment.Keys, Has.None.StartsWith("HostAgent__"));
            Assert.That((info.UseShellExecute, info.RedirectStandardOutput, info.WorkingDirectory), Is.EqualTo((false, true, _root)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HostAgent__ServiceToken", null);
        }
    }
}

/// <summary>Runtime selection and its configuration checks.</summary>
[NonParallelizable]
public sealed class RuntimeSelectionTests
{
    [Test]
    public async Task ProcessRuntime_IsChosenByConfig_AndNeedsNoImage()
    {
        string exe = Path.GetTempFileName();
        try
        {
            await using AgentHost agent = AgentHost.Start(new Dictionary<string, string?>
            {
                ["Runtime"] = "process",
                ["ServerImage"] = "",
                ["ServerExecutable"] = exe,
                ["WorldDataRoot"] = Path.GetTempPath(),
            }, fakeRuntime: false);

            Assert.That(agent.Services.GetRequiredService<IContainerRuntime>(), Is.InstanceOf<ProcessRuntime>());
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Test]
    public async Task DockerRuntime_IsTheDefault()
    {
        await using AgentHost agent = AgentHost.Start(fakeRuntime: false);

        Assert.That(agent.Services.GetRequiredService<IContainerRuntime>(), Is.InstanceOf<DockerCli>());
    }

    [Test]
    public void ProcessRuntime_WithAMissingExecutable_FailsAtStartup() =>
        Assert.That(() => AgentHost.Start(new Dictionary<string, string?>
        {
            ["Runtime"] = "process",
            ["ServerExecutable"] = "/nonexistent/VoxelRush",
            ["WorldDataRoot"] = Path.GetTempPath(),
        }, fakeRuntime: false), Throws.InvalidOperationException.With.Message.Contains("ServerExecutable"));

    [TestCase("process", "relative/VoxelRush", "/tmp/worlds")]
    [TestCase("process", "/opt/VoxelRush", "worlds")]
    [TestCase("process", "", "/tmp/worlds")]
    [TestCase("kubernetes", "/opt/VoxelRush", "/tmp/worlds")]
    public void InvalidRuntimeConfig_Throws(string runtime, string executable, string root)
    {
        var options = new HostAgentOptions
        {
            Runtime = runtime,
            ServiceToken = AgentHost.ServiceToken,
            TicketKey = AgentHost.TicketKey,
            ApiUrl = "http://127.0.0.1:18090",
            PublicHost = "play.test",
            ServerExecutable = executable,
            WorldDataRoot = root,
        };

        Assert.That(options.Validate, Throws.InvalidOperationException);
    }
}
