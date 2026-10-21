using System.Diagnostics;
using System.Globalization;

namespace VoxelRush.HostAgent;

/// <summary>A world server container; <see cref="Active"/> means created/running (it holds a port and a slot).</summary>
public sealed record WorldContainer(string Name, Guid WorldId, int Port, bool Active, DateTimeOffset StartedAt);

/// <summary>What to launch. <see cref="Environment"/> values are secrets: passed to the CLI's environment, never its arguments.</summary>
public sealed record ContainerSpec(
    string Name, string Image, Guid WorldId, int HostPort, string Volume, string? Network,
    IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Args, DateTimeOffset StartedAt);

public enum RunOutcome
{
    Started,
    NameInUse,
    PortInUse,
    Failed,
}

/// <summary>The container operations the agent needs; Docker in production, a fake in tests.</summary>
public interface IContainerRuntime
{
    Task<IReadOnlyList<WorldContainer>> ListAsync(CancellationToken ct);

    Task<(RunOutcome Outcome, string? Error)> RunAsync(ContainerSpec spec, CancellationToken ct);

    Task<bool> HasLoggedAsync(string name, string marker, CancellationToken ct);

    Task RemoveAsync(string name, CancellationToken ct);
}

/// <summary>Docker CLI against the mounted socket. Labels carry world, port and start time so state survives agent restarts.</summary>
public sealed class DockerCli(ILogger<DockerCli> log) : IContainerRuntime
{
    public const string WorldLabel = "voxelrush.world";
    public const string PortLabel = "voxelrush.port";
    public const string StartedLabel = "voxelrush.started";
    public const int ContainerPort = 7777;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<WorldContainer>> ListAsync(CancellationToken ct)
    {
        string format = $"{{{{.Names}}}}\t{{{{.State}}}}\t{{{{.Label \"{WorldLabel}\"}}}}\t{{{{.Label \"{PortLabel}\"}}}}\t{{{{.Label \"{StartedLabel}\"}}}}";
        (int exit, string stdout, string stderr) = await ExecAsync(["ps", "--all", "--no-trunc", "--filter", "label=" + WorldLabel, "--format", format], null, ct);
        if (exit != 0)
        {
            throw new InvalidOperationException("docker ps failed: " + stderr.Trim());
        }

        return ParseList(stdout);
    }

    public static IReadOnlyList<WorldContainer> ParseList(string output)
    {
        var list = new List<WorldContainer>();
        // No TrimEntries: empty labels are trailing tabs.
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 5)
            {
                continue;
            }

            // Unparseable labels still count as active, so a hand-started container can't be double-booked.
            Guid.TryParse(f[2], out Guid world);
            int.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out int port);
            long.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out long started);
            bool active = f[1] is "running" or "created" or "restarting" or "paused";
            list.Add(new WorldContainer(f[0], world, port, active, DateTimeOffset.FromUnixTimeSeconds(started)));
        }

        return list;
    }

    public async Task<(RunOutcome Outcome, string? Error)> RunAsync(ContainerSpec spec, CancellationToken ct)
    {
        (int exit, _, string stderr) = await ExecAsync(RunArguments(spec), spec.Environment, ct);
        if (exit == 0)
        {
            return (RunOutcome.Started, null);
        }

        if (stderr.Contains("is already in use by container", StringComparison.Ordinal))
        {
            return (RunOutcome.NameInUse, null);
        }

        if (stderr.Contains("port is already allocated", StringComparison.Ordinal) || stderr.Contains("address already in use", StringComparison.Ordinal))
        {
            return (RunOutcome.PortInUse, stderr.Trim());
        }

        return (RunOutcome.Failed, stderr.Trim());
    }

    /// <summary>The <c>docker run</c> argument list (public for tests: no secret values may appear here).</summary>
    public static List<string> RunArguments(ContainerSpec spec)
    {
        var args = new List<string>
        {
            "run", "--detach", "--rm", "--init", "--name", spec.Name,
            "--label", $"{WorldLabel}={spec.WorldId}",
            "--label", $"{PortLabel}={spec.HostPort.ToString(CultureInfo.InvariantCulture)}",
            "--label", $"{StartedLabel}={spec.StartedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
            "--publish", $"{spec.HostPort.ToString(CultureInfo.InvariantCulture)}:{ContainerPort.ToString(CultureInfo.InvariantCulture)}/udp",
            "--volume", spec.Volume + ":/data",
            "--read-only", "--tmpfs", "/tmp", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
        };
        if (!string.IsNullOrEmpty(spec.Network))
        {
            args.Add("--network");
            args.Add(spec.Network);
        }

        foreach (string name in spec.Environment.Keys.Order(StringComparer.Ordinal))
        {
            // Name only: docker reads the value from its own environment, so it never shows in ps or logs.
            args.Add("--env");
            args.Add(name);
        }

        args.Add(spec.Image);
        args.AddRange(spec.Args);
        return args;
    }

    public async Task<bool> HasLoggedAsync(string name, string marker, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);
        using Process process = Start(["logs", name], null);
        Task drain = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is string line)
            {
                if (line.Contains(marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Kill(process);
            await drain.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    public async Task RemoveAsync(string name, CancellationToken ct)
    {
        // Volume kept: it holds the world's journal.
        (int exit, _, string stderr) = await ExecAsync(["rm", "--force", name], null, ct);
        if (exit != 0 && !stderr.Contains("No such container", StringComparison.Ordinal))
        {
            log.LogWarning("docker_rm_failed container={Container} error={Error}", name, stderr.Trim());
        }
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> ExecAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);
        using Process process = Start(args, env);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private static Process Start(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        foreach ((string key, string value) in env ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }

        return Process.Start(info) ?? throw new InvalidOperationException("docker could not be started.");
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}
