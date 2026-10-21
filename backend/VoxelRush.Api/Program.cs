using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Amazon.S3;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using VoxelRush.Api;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
ApiOptions options = builder.Configuration.GetSection(ApiOptions.Section).Get<ApiOptions>() ?? new ApiOptions();
options.Validate();

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    // Public requests are tiny JSON bodies.
    k.Limits.MaxRequestBodySize = 16 * 1024;
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(options.ConnectionString).Build());
builder.Services.AddSingleton<IAmazonS3>(_ => SnapshotStore.CreateClient(options.Snapshots));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<AuthStore>();
builder.Services.AddSingleton<WorldStore>();
builder.Services.AddSingleton<SnapshotStore>();
builder.Services.AddSingleton<InviteStore>();
builder.Services.AddProblemDetails();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<TokenService, TimeProvider>((jwt, tokens, clock) =>
{
    jwt.MapInboundClaims = false;
    jwt.TimeProvider = clock;
    jwt.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = TokenService.Issuer,
        ValidAudience = TokenService.Audience,
        IssuerSigningKey = tokens.SigningKey,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        // IdentityModel checks lifetimes against wall time; use the app clock so expiry is consistent (and testable).
        LifetimeValidator = (notBefore, expires, _, parameters) =>
        {
            DateTime now = clock.GetUtcNow().UtcDateTime;
            return expires != null && now < expires.Value + parameters.ClockSkew && (notBefore == null || now + parameters.ClockSkew >= notBefore.Value);
        },
    };
});
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(limits =>
{
    limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limits.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = options.AuthRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    limits.AddPolicy("player", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = options.PlayerRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

WebApplication app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

await Migrations.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);
if (!string.IsNullOrEmpty(options.Snapshots.ServiceUrl))
{
    await app.Services.GetRequiredService<SnapshotStore>().EnsureBucketAsync(CancellationToken.None);
}

app.MapGet("/healthz", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using NpgsqlCommand ping = db.CreateCommand("SELECT 1");
    await ping.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "ok" });
});

RouteGroupBuilder auth = app.MapGroup("/v1/auth").RequireRateLimiting("auth");
auth.MapPost("/guest", async (AuthStore store, CancellationToken ct) => Results.Ok(await store.CreateGuestAsync(ct)));
auth.MapPost("/refresh", async (RefreshRequest request, AuthStore store, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(request.RefreshToken) || request.RefreshToken.Length > 128)
    {
        return Results.Unauthorized();
    }

    (RefreshOutcome outcome, TokenPair? tokens) = await store.RefreshAsync(request.RefreshToken, ct);
    return outcome == RefreshOutcome.Ok ? Results.Ok(tokens) : Results.Unauthorized();
});

RouteGroupBuilder worlds = app.MapGroup("/v1/worlds").RequireAuthorization().RequireRateLimiting("player");
worlds.MapPost("/", async (CreateWorldRequest request, ClaimsPrincipal user, WorldStore store, CancellationToken ct) =>
{
    (WorldError error, WorldView? world) = await store.CreateAsync(PlayerId(user), request, ct);
    return error == WorldError.None ? Results.Created($"/v1/worlds/{world!.Id}", world) : Problem(error);
});
worlds.MapGet("/public", async (int? limit, DateTimeOffset? before, ClaimsPrincipal user, WorldStore store, CancellationToken ct) =>
    Results.Ok(await store.PublicAsync(PlayerId(user), limit ?? 20, before, ct)));
worlds.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, WorldStore store, CancellationToken ct) => Problem(await store.DeleteAsync(PlayerId(user), id, ct)));
worlds.MapPost("/{id:guid}/invites", async (Guid id, CreateInviteRequest request, ClaimsPrincipal user, InviteStore invites, CancellationToken ct) =>
{
    (WorldError error, InviteCreated? invite) = await invites.CreateAsync(PlayerId(user), id, request, ct);
    return error == WorldError.None ? Results.Ok(invite) : Problem(error);
});
worlds.MapDelete("/{id:guid}/invites", async (Guid id, ClaimsPrincipal user, InviteStore invites, CancellationToken ct) => Problem(await invites.RevokeAllAsync(PlayerId(user), id, ct)));
app.MapPost("/v1/invites/accept", async (AcceptInviteRequest request, ClaimsPrincipal user, InviteStore invites, CancellationToken ct) =>
{
    (InviteOutcome outcome, Guid world, string? role) = await invites.AcceptAsync(PlayerId(user), request.Code, ct);
    return outcome switch
    {
        InviteOutcome.Accepted => Results.Ok(new { worldId = world, role }),
        InviteOutcome.AlreadyOwner => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "already_owner"),
        _ => Results.NotFound(),
    };
}).RequireAuthorization().RequireRateLimiting("player");
worlds.MapGet("/mine", async (ClaimsPrincipal user, WorldStore store, CancellationToken ct) => Results.Ok(await store.MineAsync(PlayerId(user), ct)));
worlds.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, WorldStore store, CancellationToken ct) =>
    await store.GetAsync(PlayerId(user), id, ct) is WorldView view ? Results.Ok(view) : Results.NotFound());
worlds.MapPatch("/{id:guid}", async (Guid id, UpdateWorldRequest request, ClaimsPrincipal user, WorldStore store, CancellationToken ct) =>
    Problem(await store.UpdateAsync(PlayerId(user), id, request, ct)));
worlds.MapPost("/{id:guid}/members", async (Guid id, AddMemberRequest request, ClaimsPrincipal user, WorldStore store, CancellationToken ct) =>
    Problem(await store.AddMemberAsync(PlayerId(user), id, request, ct)));
worlds.MapDelete("/{id:guid}/members/{member:guid}", async (Guid id, Guid member, ClaimsPrincipal user, WorldStore store, CancellationToken ct) =>
    Problem(await store.RemoveMemberAsync(PlayerId(user), id, member, ct)));
worlds.MapPost("/{id:guid}/join", async (Guid id, ClaimsPrincipal user, WorldStore store, CancellationToken ct) =>
{
    (WorldError error, JoinResponse? join) = await store.JoinAsync(PlayerId(user), id, ct);
    return error == WorldError.None ? Results.Ok(join) : Problem(error);
});

// Game servers only (service token); clients never see storage URLs.
RouteGroupBuilder internalApi = app.MapGroup("/internal/v1/worlds/{id:guid}/snapshots").AddEndpointFilter(async (context, next) =>
    ServiceAuthorized(context.HttpContext, options) ? await next(context) : Results.Unauthorized());
internalApi.MapPost("/upload", async (Guid id, SnapshotUploadRequest request, SnapshotStore store, CancellationToken ct) =>
{
    (SnapshotError error, PresignedUrl? url) = await store.CreateUploadAsync(id, request, ct);
    return error == SnapshotError.None ? Results.Ok(url) : SnapshotProblem(error);
});
internalApi.MapPost("/commit", async (Guid id, SnapshotCommitRequest request, SnapshotStore store, CancellationToken ct) => SnapshotProblem(await store.CommitAsync(id, request, ct)));
internalApi.MapGet("/latest", async (Guid id, int x, int y, int z, SnapshotStore store, CancellationToken ct) =>
{
    (SnapshotError error, PresignedUrl? url, long revision, string? sha) = await store.LatestAsync(id, x, y, z, ct);
    return error == SnapshotError.None ? Results.Ok(new { url = url!.Url, revision, sha256 = sha }) : SnapshotProblem(error);
});

app.Run();

static Guid PlayerId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub)!);

static IResult Problem(WorldError error) => error switch
{
    WorldError.None => Results.NoContent(),
    WorldError.NotFound => Results.NotFound(),
    WorldError.Forbidden => Results.Forbid(),
    WorldError.QuotaExceeded => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "quota_exceeded"),
    _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_request"),
};

static IResult SnapshotProblem(SnapshotError error) => error switch
{
    SnapshotError.None => Results.NoContent(),
    SnapshotError.NotFound => Results.NotFound(),
    SnapshotError.Missing => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "object_missing"),
    SnapshotError.ChecksumMismatch => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "checksum_mismatch"),
    _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_request"),
};

static bool ServiceAuthorized(HttpContext http, ApiOptions options)
{
    string? presented = http.Request.Headers["X-Service-Token"];
    return presented != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(options.ServiceToken));
}

public sealed record RefreshRequest(string? RefreshToken);

/// <summary>Entry point (public for WebApplicationFactory in tests).</summary>
public partial class Program;
