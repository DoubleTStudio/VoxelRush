using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using VoxelRush.HostAgent;
using VoxelRush.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
HostAgentOptions options = builder.Configuration.GetSection(HostAgentOptions.Section).Get<HostAgentOptions>() ?? new HostAgentOptions();
options.Validate();

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 1024;
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IContainerRuntime, DockerCli>();
builder.Services.AddSingleton<WorldHosts>();
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

WebApplication app = builder.Build();

// Unauthenticated, so it says nothing about worlds; a failing docker socket shows up here.
app.MapGet("/healthz", async (WorldHosts hosts, CancellationToken ct) =>
{
    try
    {
        await hosts.ActiveCountAsync(ct);
        return Results.Ok(new { status = "ok" });
    }
    catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
    {
        app.Logger.LogError("host_agent_unhealthy error={Error}", e.Message);
        return Results.Json(new { status = "docker_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// Called by the API on join (service token); idempotent, so clients retrying join are harmless.
app.MapPost("/internal/v1/hosts/{worldId:guid}/ensure", async (Guid worldId, WorldHosts hosts, CancellationToken ct) =>
{
    if (worldId == Guid.Empty)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_request");
    }

    HostEnsureResponse result;
    try
    {
        result = await hosts.EnsureAsync(worldId, ct);
    }
    catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
    {
        app.Logger.LogError("world_ensure_failed world={World} error={Error}", worldId, e.Message);
        result = new HostEnsureResponse(HostEnsureResponse.Unavailable, RetryAfterSeconds: options.RetryAfterSeconds * 5, Reason: HostEnsureResponse.HostUnavailable);
    }

    return result.Status switch
    {
        HostEnsureResponse.Ready => Results.Ok(result),
        HostEnsureResponse.Starting => Results.Json(result, statusCode: StatusCodes.Status202Accepted),
        _ => Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable),
    };
}).AddEndpointFilter(async (context, next) => ServiceAuthorized(context.HttpContext, options) ? await next(context) : Results.Unauthorized());

app.Run();

static bool ServiceAuthorized(HttpContext http, HostAgentOptions options)
{
    string? presented = http.Request.Headers["X-Service-Token"];
    return presented != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(options.ServiceToken));
}

/// <summary>Entry point (public for WebApplicationFactory in tests).</summary>
public partial class Program;
