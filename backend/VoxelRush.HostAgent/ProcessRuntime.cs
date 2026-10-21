using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VoxelRush.HostAgent;

/// <summary>What to start. <see cref="Environment"/> holds secrets: set on the child's environment, never its arguments.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Environment, string WorkingDirectory);

/// <summary>A started server process; <see cref="Exited"/> completes with its exit code.</summary>
public interface IServerProcess
{
    int Id { get; }

    Task<int> Exited { get; }

    /// <summary>SIGTERM: the server flushes its journal and releases the world lease.</summary>
    void Terminate();

    void Kill();
}

/// <summary>Starts processes and probes ports; the OS in production, a fake in tests.</summary>
public interface IProcessLauncher
{
    /// <summary>Starts the process; <paramref name="onOutput"/> gets each stdout/stderr line.</summary>
    IServerProcess Start(ProcessSpec spec, Action<string> onOutput);

    bool IsUdpPortFree(int port);
}

/// <summary>Native world servers (DEV-001), for hosts where the Linux server image can't run, e.g. Apple Silicon development Macs.</summary>
/// <remarks>Same contract as <see cref="DockerCli"/>: an exited process is gone (like --rm). State lives in memory only, so
/// servers are stopped when the agent shuts down rather than adopted on restart.</remarks>
public sealed class ProcessRuntime(IProcessLauncher launcher, HostAgentOptions options, ILogger<ProcessRuntime> log) : IContainerRuntime, IDisposable
{
    // Unity player flags; the server's own arguments follow.
    private static readonly string[] PlayerArgs = ["-batchmode", "-nographics", "-logFile", "-"];

    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private int _disposed;

    /// <summary>How long servers get to flush and release their lease on agent shutdown before they are killed.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(15);

    public string LogPath(string name) => Path.Combine(options.WorldDataRoot, name + ".log");

    public (int Port, string WorldDir) ServerBinding(int hostPort, string volume) => (hostPort, Path.Combine(options.WorldDataRoot, volume));

    public Task<IReadOnlyList<WorldContainer>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WorldContainer>>(_entries.Values.Where(e => e.Running).Select(e => e.Info).ToList());

    public Task<(RunOutcome Outcome, string? Error)> RunAsync(ContainerSpec spec, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return Task.FromResult<(RunOutcome, string?)>((RunOutcome.Failed, "host agent is shutting down"));
        }

        if (_entries.TryGetValue(spec.Name, out Entry? existing) && existing.Running)
        {
            return Task.FromResult<(RunOutcome, string?)>((RunOutcome.NameInUse, null));
        }

        if (!launcher.IsUdpPortFree(spec.HostPort))
        {
            return Task.FromResult<(RunOutcome, string?)>((RunOutcome.PortInUse, $"udp port {spec.HostPort} is in use"));
        }

        var entry = new Entry(new WorldContainer(spec.Name, spec.WorldId, spec.HostPort, true, spec.StartedAt));
        try
        {
            Directory.CreateDirectory(ServerBinding(spec.HostPort, spec.Volume).WorldDir);
            entry.OpenLog(LogPath(spec.Name));
            entry.Process = launcher.Start(new ProcessSpec(options.ServerExecutable, [.. PlayerArgs, .. spec.Args], spec.Environment, options.WorldDataRoot), entry.Write);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            entry.Close();
            return Task.FromResult<(RunOutcome, string?)>((RunOutcome.Failed, e.Message));
        }

        _entries[spec.Name] = entry;
        _ = entry.Process.Exited.ContinueWith(t => OnExit(entry, t), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return Task.FromResult<(RunOutcome, string?)>((RunOutcome.Started, null));
    }

    // Only readiness is tracked: it is the one marker the agent asks about, so no log has to be kept in memory.
    public Task<bool> HasLoggedAsync(string name, string marker, CancellationToken ct) =>
        Task.FromResult(marker == WorldHosts.ListeningMarker && _entries.TryGetValue(name, out Entry? e) && e.Listening);

    public async Task RemoveAsync(string name, CancellationToken ct)
    {
        // World directory kept: it holds the journal.
        if (!_entries.TryGetValue(name, out Entry? entry) || entry.Process == null)
        {
            return;
        }

        entry.Process.Kill();
        try
        {
            await entry.Process.Exited.WaitAsync(KillWait, ct);
        }
        catch (TimeoutException)
        {
            log.LogWarning("world_server_kill_timeout container={Container} pid={Pid}", name, entry.Process.Id);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        List<Entry> running = _entries.Values.Where(e => e.Running).ToList();
        if (running.Count == 0)
        {
            return;
        }

        log.LogInformation("world_servers_stopping count={Count}", running.Count);
        foreach (Entry e in running)
        {
            e.Process!.Terminate();
        }

        if (!Task.WhenAll(running.Select(e => e.Process!.Exited)).Wait(StopGrace))
        {
            foreach (Entry e in running.Where(e => e.Running))
            {
                log.LogWarning("world_server_killed container={Container} pid={Pid}", e.Info.Name, e.Process!.Id);
                e.Process.Kill();
            }
        }
    }

    private void OnExit(Entry entry, Task<int> exited)
    {
        log.LogInformation("world_server_exited world={World} container={Container} code={Code}", entry.Info.WorldId, entry.Info.Name, exited.IsCompletedSuccessfully ? exited.Result : -1);
        _entries.TryRemove(new KeyValuePair<string, Entry>(entry.Info.Name, entry));
        entry.Close();
    }

    private sealed class Entry(WorldContainer info)
    {
        private readonly object _lock = new();

        private StreamWriter? _log;

        public WorldContainer Info { get; } = info;

        public IServerProcess? Process { get; set; }

        public bool Running => Process is { Exited.IsCompleted: false };

        public bool Listening { get; private set; }

        public void OpenLog(string path) => _log = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };

        public void Write(string line)
        {
            lock (_lock)
            {
                Listening |= line.Contains(WorldHosts.ListeningMarker, StringComparison.Ordinal);
                _log?.WriteLine(line);
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                _log?.Dispose();
                _log = null;
            }
        }
    }
}

/// <summary>Real processes. Children get the secrets they need, but not the agent's own HostAgent__* configuration.</summary>
public sealed class OsProcessLauncher : IProcessLauncher
{
    private const int SigTerm = 15;

    public static ProcessStartInfo StartInfo(ProcessSpec spec)
    {
        var info = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = spec.WorkingDirectory,
        };
        foreach (string arg in spec.Args)
        {
            info.ArgumentList.Add(arg);
        }

        foreach (string key in info.Environment.Keys.Where(k => k.StartsWith(HostAgentOptions.Section + "__", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(key);
        }

        foreach ((string key, string value) in spec.Environment)
        {
            info.Environment[key] = value;
        }

        return info;
    }

    public IServerProcess Start(ProcessSpec spec, Action<string> onOutput)
    {
        var process = new Process { StartInfo = StartInfo(spec) };
        process.OutputDataReceived += (_, e) => Forward(e.Data, onOutput);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, onOutput);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException(spec.FileName + " could not be started.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new OsProcess(process);
    }

    public bool IsUdpPortFree(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void Forward(string? line, Action<string> onOutput)
    {
        if (line != null)
        {
            onOutput(line);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private sealed class OsProcess : IServerProcess
    {
        private readonly Process _process;

        public OsProcess(Process process)
        {
            _process = process;
            Id = process.Id;
            Exited = WatchAsync();
        }

        public int Id { get; }

        public Task<int> Exited { get; }

        public void Terminate()
        {
            if (Exited.IsCompleted)
            {
                return;
            }

            if (OperatingSystem.IsWindows() || kill(Id, SigTerm) != 0)
            {
                Kill();
            }
        }

        public void Kill()
        {
            try
            {
                if (!Exited.IsCompleted)
                {
                    _process.Kill();
                }
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
        }

        private async Task<int> WatchAsync()
        {
            // Also waits for the output to drain, so the last lines reach the log.
            await _process.WaitForExitAsync();
            int code = _process.ExitCode;
            _process.Dispose();
            return code;
        }
    }
}
