namespace VoxelRush.Hosting;

/// <summary>Wire contract of <c>POST /internal/v1/hosts/{worldId}/ensure</c>; compiled into both the host agent and the API.</summary>
public sealed record HostEnsureResponse(string Status, string? Endpoint = null, int? RetryAfterSeconds = null, string? Reason = null)
{
    public const string Ready = "ready";
    public const string Starting = "starting";
    public const string Unavailable = "unavailable";

    public const string NoCapacity = "no_capacity";
    public const string StartFailed = "start_failed";
    public const string HostUnavailable = "host_unavailable";
}
