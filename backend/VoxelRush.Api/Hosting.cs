using System.Net.Http.Json;
using VoxelRush.Hosting;

namespace VoxelRush.Api;

public enum AllocationStatus
{
    Ready,
    Starting,
    Unavailable,
}

/// <summary>Where a world is served: Ready carries host:port; Starting and Unavailable tell the client when to retry.</summary>
public sealed record AllocationResult(AllocationStatus Status, string? Endpoint, int RetryAfterSeconds, string? Reason)
{
    public static AllocationResult Ready(string endpoint) => new(AllocationStatus.Ready, endpoint, 0, null);

    public static AllocationResult Starting(int retryAfterSeconds) => new(AllocationStatus.Starting, null, retryAfterSeconds, null);

    public static AllocationResult Unavailable(string reason, int retryAfterSeconds = 10) => new(AllocationStatus.Unavailable, null, retryAfterSeconds, reason);
}

/// <summary>ALLOC-001: makes sure a game server hosts the world before a ticket is issued.</summary>
public interface IWorldHostAllocator
{
    Task<AllocationResult> EnsureAsync(Guid worldId, CancellationToken ct);
}

/// <summary>One fixed server for every world (single-host development; the default when no host agent is configured).</summary>
public sealed class StaticAllocator(ApiOptions options) : IWorldHostAllocator
{
    public Task<AllocationResult> EnsureAsync(Guid worldId, CancellationToken ct) => Task.FromResult(AllocationResult.Ready(options.GameServerEndpoint));
}

/// <summary>Asks the host agent on the game VM to start (or find) the world's server container.</summary>
public sealed class HostAgentAllocator(IHttpClientFactory http, ApiOptions options, ILogger<HostAgentAllocator> log) : IWorldHostAllocator
{
    public const string HttpClientName = "host-agent";

    public async Task<AllocationResult> EnsureAsync(Guid worldId, CancellationToken ct)
    {
        HttpClient client = http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.HostAgentUrl), $"internal/v1/hosts/{worldId}/ensure"));
        request.Headers.Add("X-Service-Token", options.ServiceToken);
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            HostEnsureResponse? body = response.Content.Headers.ContentType?.MediaType == "application/json"
                ? await response.Content.ReadFromJsonAsync<HostEnsureResponse>(ct)
                : null;
            switch (body?.Status)
            {
                case HostEnsureResponse.Ready when response.IsSuccessStatusCode && !string.IsNullOrEmpty(body.Endpoint):
                    return AllocationResult.Ready(body.Endpoint);
                case HostEnsureResponse.Starting:
                    return AllocationResult.Starting(Math.Clamp(body.RetryAfterSeconds ?? 2, 1, 30));
                case HostEnsureResponse.Unavailable:
                    return AllocationResult.Unavailable(body.Reason ?? HostEnsureResponse.HostUnavailable, Math.Clamp(body.RetryAfterSeconds ?? 10, 1, 300));
            }

            log.LogError("host_agent_bad_response world={World} status={Status}", worldId, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            log.LogError("host_agent_unreachable world={World} error={Error}", worldId, e.Message);
        }

        return AllocationResult.Unavailable(HostEnsureResponse.HostUnavailable);
    }
}
