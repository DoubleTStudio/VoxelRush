namespace VoxelRush.Api;

/// <summary>Configuration (environment variables in deployment, e.g. <c>VoxelRush__TicketKey</c>). Secrets are base64.</summary>
public sealed class ApiOptions
{
    public const string Section = "VoxelRush";

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>HMAC key for access tokens (≥ 32 bytes, base64).</summary>
    public string JwtSigningKey { get; set; } = string.Empty;

    /// <summary>HMAC key shared with game servers for join tickets (≥ 32 bytes, base64). Never sent to clients.</summary>
    public string TicketKey { get; set; } = string.Empty;

    /// <summary>Token game servers present on internal endpoints (≥ 32 characters).</summary>
    public string ServiceToken { get; set; } = string.Empty;

    /// <summary>Where joiners connect (host:port) until server allocation exists.</summary>
    public string GameServerEndpoint { get; set; } = "127.0.0.1:7777";

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;

    public int TicketSeconds { get; set; } = 60;

    public int MaxWorldsPerOwner { get; set; } = 3;

    /// <summary>Per client IP on /v1/auth.</summary>
    public int AuthRequestsPerMinute { get; set; } = 20;

    /// <summary>Per player on /v1/worlds.</summary>
    public int PlayerRequestsPerMinute { get; set; } = 60;

    public int GeneratorVersion { get; set; } = 1;

    public int SchemaVersion { get; set; } = 1;

    public SnapshotStoreOptions Snapshots { get; set; } = new();

    public byte[] JwtKeyBytes() => Key(JwtSigningKey, nameof(JwtSigningKey));

    public byte[] TicketKeyBytes() => Key(TicketKey, nameof(TicketKey));

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("VoxelRush:ConnectionString is required.");
        }

        JwtKeyBytes();
        TicketKeyBytes();
        if (ServiceToken.Length < 32)
        {
            throw new InvalidOperationException("VoxelRush:ServiceToken must be at least 32 characters.");
        }
    }

    private static byte[] Key(string value, string name)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"VoxelRush:{name} must be base64.");
        }

        return bytes.Length >= 32 ? bytes : throw new InvalidOperationException($"VoxelRush:{name} must be at least 32 bytes.");
    }
}

/// <summary>S3-compatible object storage (Cloudflare R2, MinIO, AWS S3).</summary>
public sealed class SnapshotStoreOptions
{
    public string ServiceUrl { get; set; } = string.Empty;

    public string Bucket { get; set; } = "voxelrush-snapshots";

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    /// <summary>R2 uses "auto"; MinIO accepts any region.</summary>
    public string Region { get; set; } = "auto";

    public int PresignMinutes { get; set; } = 10;

    public int MaxSnapshotBytes { get; set; } = 1024 * 1024;

    /// <summary>SAVE-009 retention: the newest revisions per chunk always kept, plus everything inside the window.</summary>
    public int RetainRevisions { get; set; } = 3;

    /// <summary>Point-in-time restore window.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>Default when the owner has no world_quotas row.</summary>
    public long MaxStoredBytesPerOwner { get; set; } = 64L * 1024 * 1024;

    /// <summary>For the storage cost metric (Cloudflare R2 standard storage, USD per GB-month).</summary>
    public decimal CostPerGbMonthUsd { get; set; } = 0.015m;
}
