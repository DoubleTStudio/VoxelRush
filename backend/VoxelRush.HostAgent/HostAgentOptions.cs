namespace VoxelRush.HostAgent;

/// <summary>Configuration (environment variables in deployment, e.g. <c>HostAgent__ServerImage</c>). Secrets come from the environment only.</summary>
public sealed class HostAgentOptions
{
    public const string Section = "HostAgent";

    public const string DockerRuntime = "docker";

    public const string ProcessRuntime = "process";

    /// <summary>"docker" (default): one container per world. "process": native server processes, for development hosts that can't run the Linux image.</summary>
    public string Runtime { get; set; } = DockerRuntime;

    /// <summary>Process runtime only: absolute path of the native server executable (e.g. build/server-mac/VoxelRush).</summary>
    public string ServerExecutable { get; set; } = string.Empty;

    /// <summary>Process runtime only: absolute directory holding a world directory and a log file per world.</summary>
    public string WorldDataRoot { get; set; } = string.Empty;

    /// <summary>Token the API presents (X-Service-Token); game servers get the same one for their lease/snapshot calls.</summary>
    public string ServiceToken { get; set; } = string.Empty;

    /// <summary>Base64 join-ticket key handed to game servers (VOXELRUSH_TICKET_KEY).</summary>
    public string TicketKey { get; set; } = string.Empty;

    /// <summary>API base URL as game servers reach it (VOXELRUSH_API_URL), e.g. http://api:8080 on the compose network.</summary>
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>Game server image, e.g. ghcr.io/doubletstudio/voxelrush-server:production.</summary>
    public string ServerImage { get; set; } = string.Empty;

    /// <summary>Host name or IP clients connect to; endpoints are reported as PublicHost:port.</summary>
    public string PublicHost { get; set; } = string.Empty;

    /// <summary>Docker network game servers join so they can reach the API by name; empty uses the default bridge.</summary>
    public string Network { get; set; } = string.Empty;

    /// <summary>Host UDP ports handed out to world servers (inclusive); keep 7777 free for manual servers.</summary>
    public int PortRangeStart { get; set; } = 7800;

    public int PortRangeEnd { get; set; } = 7899;

    /// <summary>Concurrent world servers on this host.</summary>
    public int MaxWorlds { get; set; } = 4;

    public int MaxPlayers { get; set; } = 8;

    /// <summary>A server with no players flushes, releases its lease and exits after this long (frees the slot).</summary>
    public int IdleShutdownSeconds { get; set; } = 300;

    /// <summary>A server not listening after this long is removed and reported as failed.</summary>
    public int StartTimeoutSeconds { get; set; } = 90;

    /// <summary>Hint to clients while a server boots.</summary>
    public int RetryAfterSeconds { get; set; } = 2;

    /// <summary>Per world, a named volume with this prefix holds the journal and local snapshots across restarts.</summary>
    public string VolumePrefix { get; set; } = "voxelrush-world-";

    public void Validate()
    {
        if (ServiceToken.Length < 32)
        {
            throw new InvalidOperationException("HostAgent:ServiceToken must be at least 32 characters.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(TicketKey);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("HostAgent:TicketKey must be base64.");
        }

        if (key.Length < 32)
        {
            throw new InvalidOperationException("HostAgent:TicketKey must be at least 32 bytes.");
        }

        if (!Uri.TryCreate(ApiUrl, UriKind.Absolute, out Uri? api) || (api.Scheme != Uri.UriSchemeHttp && api.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("HostAgent:ApiUrl must be an absolute http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(PublicHost))
        {
            throw new InvalidOperationException("HostAgent:PublicHost is required.");
        }

        if (Runtime == ProcessRuntime)
        {
            if (!Path.IsPathFullyQualified(ServerExecutable) || !Path.IsPathFullyQualified(WorldDataRoot))
            {
                throw new InvalidOperationException("HostAgent:ServerExecutable and HostAgent:WorldDataRoot must be absolute paths.");
            }
        }
        else if (Runtime != DockerRuntime)
        {
            throw new InvalidOperationException("HostAgent:Runtime must be docker or process.");
        }
        else if (string.IsNullOrWhiteSpace(ServerImage))
        {
            throw new InvalidOperationException("HostAgent:ServerImage is required.");
        }

        if (PortRangeStart < 1 || PortRangeEnd > 65535 || PortRangeStart > PortRangeEnd)
        {
            throw new InvalidOperationException("HostAgent:PortRangeStart..PortRangeEnd must be a range within 1..65535.");
        }

        if (MaxWorlds < 1 || MaxWorlds > PortRangeEnd - PortRangeStart + 1)
        {
            throw new InvalidOperationException("HostAgent:MaxWorlds must be at least 1 and fit the port range.");
        }

        // Same bounds the server enforces (ServerLaunchOptions), so a bad value fails here, not on every launch.
        if (MaxPlayers < 1 || MaxPlayers > 8 || IdleShutdownSeconds < 5 || IdleShutdownSeconds > 86400)
        {
            throw new InvalidOperationException("HostAgent:MaxPlayers must be 1..8 and IdleShutdownSeconds 5..86400.");
        }

        if (StartTimeoutSeconds < 1 || RetryAfterSeconds < 1)
        {
            throw new InvalidOperationException("HostAgent:StartTimeoutSeconds and RetryAfterSeconds must be positive.");
        }
    }
}
