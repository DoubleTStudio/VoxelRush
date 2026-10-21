using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
builder.Services.AddSingleton<ProgressionStore>();
builder.Services.AddSingleton<EntitlementStore>();
builder.Services.AddSingleton<LeaseStore>();
builder.Services.AddSingleton<BackupStore>();
// ALLOC-001: a host agent starts one server per world; without one every world shares GameServerEndpoint.
if (string.IsNullOrEmpty(options.HostAgentUrl))
{
    builder.Services.AddSingleton<IWorldHostAllocator, StaticAllocator>();
}
else
{
    builder.Services.AddHttpClient(HostAgentAllocator.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(options.HostAgentTimeoutSeconds));
    builder.Services.AddSingleton<IWorldHostAllocator, HostAgentAllocator>();
}

builder.Services.AddHttpClient(GooglePlayVerifier.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient(AdRewardService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<AppleVerifier>();
builder.Services.AddSingleton<GooglePlayVerifier>();
builder.Services.AddSingleton<PurchaseService>();
builder.Services.AddSingleton<AdRewardService>();
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

// SAVE-006: link a second device to the same player.
auth.MapPost("/link-code", async (ClaimsPrincipal user, ProgressionStore store, CancellationToken ct) =>
    Results.Ok(await store.CreateLinkCodeAsync(PlayerId(user), ct))).RequireAuthorization();
auth.MapPost("/link", async (LinkRequest request, ProgressionStore store, AuthStore authStore, CancellationToken ct) =>
    await store.RedeemLinkCodeAsync(request.Code, ct) is Guid player ? Results.Ok(await authStore.SignInAsync(player, ct)) : Results.NotFound());

RouteGroupBuilder me = app.MapGroup("/v1/players/me").RequireAuthorization().RequireRateLimiting("player");
me.MapGet("/progress", async (ClaimsPrincipal user, ProgressionStore store, CancellationToken ct) => Results.Ok(await store.GetAsync(PlayerId(user), ct)));
me.MapGet("/settings", async (ClaimsPrincipal user, ProgressionStore store, CancellationToken ct) =>
{
    SettingsView view = await store.GetSettingsAsync(PlayerId(user), ct);
    return Results.Ok(view);
});
me.MapPut("/settings", async (HttpRequest http, JsonElement settings, ClaimsPrincipal user, ProgressionStore store, CancellationToken ct) =>
{
    // Optimistic concurrency: If-Match carries the version the device last read ("0" for none).
    if (!long.TryParse(http.Headers.IfMatch.ToString().Trim('"'), out long expected) || expected < 0)
    {
        return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "if_match_required");
    }

    (SettingsOutcome outcome, long version) = await store.PutSettingsAsync(PlayerId(user), settings, expected, ct);
    return outcome switch
    {
        SettingsOutcome.Saved => Results.Ok(new { version }),
        SettingsOutcome.VersionConflict => Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: "version_conflict"),
        _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_settings"),
    };
});

// ECON-001: cosmetic catalog and entitlements.
app.MapGet("/v1/catalog", (EntitlementStore store) => Results.Ok(store.Catalog));
me.MapGet("/entitlements", async (ClaimsPrincipal user, EntitlementStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(PlayerId(user), ct)));
me.MapPost("/entitlements/claim", async (ClaimRequest request, ClaimsPrincipal user, EntitlementStore store, CancellationToken ct) =>
    EntitlementResult(await store.ClaimAsync(PlayerId(user), request.Sku, ct)));

// ECON-002: verified store purchases and refund notifications; rewarded-ad callbacks (off until privacy review).
me.MapPost("/purchases", async (PurchaseRequest request, ClaimsPrincipal user, PurchaseService purchases, CancellationToken ct) =>
{
    (PurchaseOutcome outcome, string? sku) = await purchases.SubmitAsync(PlayerId(user), request, ct);
    return outcome switch
    {
        PurchaseOutcome.Granted => Results.Ok(new { sku, granted = true }),
        PurchaseOutcome.AlreadyOwned => Results.Ok(new { sku, granted = false }),
        PurchaseOutcome.Pending => Results.Accepted(value: new { sku, granted = false, pending = true }),
        PurchaseOutcome.Revoked => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "purchase_revoked"),
        PurchaseOutcome.ReceiptInUse => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "receipt_in_use"),
        PurchaseOutcome.UnknownProduct => Results.NotFound(),
        PurchaseOutcome.Unavailable => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "store_unavailable"),
        PurchaseOutcome.NotConfigured => Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "store_disabled"),
        _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_receipt"),
    };
});
// Store servers call these; authenticity comes from Apple's signature / the push secret, not a player token.
app.MapPost("/v1/store/apple/notifications", async (AppleNotification body, PurchaseService purchases, CancellationToken ct) =>
    await purchases.AppleNotificationAsync(body.SignedPayload, ct) is null ? Results.BadRequest() : Results.Ok())
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024));
app.MapPost("/v1/store/google/notifications", async (string? token, JsonElement body, PurchaseService purchases, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(options.Store.GooglePushToken))
    {
        return Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "store_disabled");
    }

    if (token == null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(options.Store.GooglePushToken)))
    {
        return Results.Unauthorized();
    }

    return await purchases.GoogleNotificationAsync(body, ct) is null ? Results.BadRequest() : Results.Ok();
});
app.MapGet("/v1/ads/admob/reward", async (HttpContext http, AdRewardService ads, CancellationToken ct) =>
{
    if (!options.Ads.Enabled)
    {
        return Results.NotFound();
    }

    AdRewardOutcome outcome = await ads.VerifyAndGrantAsync(http.Request.QueryString.Value, ct);
    return outcome == AdRewardOutcome.Invalid ? Results.BadRequest() : Results.Ok(new { granted = outcome == AdRewardOutcome.Granted, reason = outcome.ToString() });
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
// SAVE-009: restore points, owner-only point-in-time restore (world must be stopped), audit trail.
worlds.MapGet("/{id:guid}/backups", async (Guid id, ClaimsPrincipal user, WorldStore store, BackupStore backups, CancellationToken ct) =>
    await store.GetAsync(PlayerId(user), id, ct) is { Role: "owner" } ? Results.Ok(await backups.RestorePointsAsync(id, ct)) : Results.NotFound());
worlds.MapGet("/{id:guid}/restores", async (Guid id, ClaimsPrincipal user, WorldStore store, BackupStore backups, CancellationToken ct) =>
    await store.GetAsync(PlayerId(user), id, ct) is { Role: "owner" } ? Results.Ok(await backups.RestoresAsync(id, ct)) : Results.NotFound());
worlds.MapPost("/{id:guid}/restore", async (Guid id, RestoreRequest request, ClaimsPrincipal user, WorldStore store, BackupStore backups, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(options.Snapshots.ServiceUrl))
    {
        return Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "storage_disabled");
    }

    if (request.AsOf is not DateTimeOffset asOf)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_request");
    }

    // Members who aren't owners learn nothing beyond "not found", like other owner-only routes.
    if (await store.GetAsync(PlayerId(user), id, ct) is not { Role: "owner" })
    {
        return Results.NotFound();
    }

    (RestoreError error, RestoreView? view) = await backups.RestoreAsync(PlayerId(user), id, asOf, ct);
    return error switch
    {
        RestoreError.None => Results.Ok(view),
        RestoreError.WorldRunning => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "world_running"),
        RestoreError.OutsideRetention => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "outside_retention"),
        _ => Results.NotFound(),
    };
});

worlds.MapPost("/{id:guid}/join", async (Guid id, ClaimsPrincipal user, HttpResponse http, WorldStore store, CancellationToken ct) =>
{
    (WorldError error, AllocationResult? host, JoinResponse? join) = await store.JoinAsync(PlayerId(user), id, ct);
    if (error != WorldError.None)
    {
        return Problem(error);
    }

    if (host!.Status != AllocationStatus.Ready)
    {
        http.Headers.RetryAfter = host.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // ALLOC-001: no ticket until the world's server is up, so it can't expire while the server boots.
    return host.Status switch
    {
        AllocationStatus.Ready => Results.Ok(join),
        AllocationStatus.Starting => Results.Json(new JoinPending("starting", host.RetryAfterSeconds), statusCode: StatusCodes.Status202Accepted),
        _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: host.Reason == VoxelRush.Hosting.HostEnsureResponse.NoCapacity ? "no_capacity" : "host_unavailable"),
    };
});

// SAVE-005 leases (game servers only).
RouteGroupBuilder leases = app.MapGroup("/internal/v1/worlds/{id:guid}/lease").AddEndpointFilter(async (context, next) =>
    ServiceAuthorized(context.HttpContext, options) ? await next(context) : Results.Unauthorized());
leases.MapPost("/acquire", async (Guid id, LeaseRequest request, LeaseStore store, CancellationToken ct) => LeaseResult(await store.AcquireAsync(id, request, ct)));
leases.MapPost("/renew", async (Guid id, LeaseRequest request, LeaseStore store, CancellationToken ct) => LeaseResult(await store.RenewAsync(id, request, ct)));
leases.MapPost("/release", async (Guid id, LeaseRequest request, LeaseStore store, CancellationToken ct) => LeaseResult((await store.ReleaseAsync(id, request, ct), null)));

// Game servers only (service token); clients never see storage URLs. World-scoped writes must carry the live
// lease (X-Lease-Holder / X-Lease-Fencing) so a server that lost its world can't write over the new holder.
RouteGroupBuilder internalApi = app.MapGroup("/internal/v1/worlds/{id:guid}/snapshots").AddEndpointFilter(async (context, next) =>
{
    if (!ServiceAuthorized(context.HttpContext, options))
    {
        return Results.Unauthorized();
    }

    // Explicit, not an error: game servers then keep worlds on local disk only (development, single-host).
    if (string.IsNullOrEmpty(options.Snapshots.ServiceUrl))
    {
        return Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "storage_disabled");
    }

    HttpRequest http = context.HttpContext.Request;
    if (!HttpMethods.IsGet(http.Method))
    {
        Guid world = Guid.Parse((string)context.HttpContext.GetRouteValue("id")!);
        long? fencing = long.TryParse(http.Headers["X-Lease-Fencing"], out long f) ? f : null;
        if (!await context.HttpContext.RequestServices.GetRequiredService<LeaseStore>().IsCurrentAsync(world, http.Headers["X-Lease-Holder"], fencing, context.HttpContext.RequestAborted))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "lease_required");
        }
    }

    return await next(context);
});
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

app.MapGet("/internal/v1/metrics/storage", async (BackupStore backups, CancellationToken ct) => Results.Ok(await backups.MetricsAsync(ct)))
    .AddEndpointFilter(async (context, next) => ServiceAuthorized(context.HttpContext, options) ? await next(context) : Results.Unauthorized());

// Game servers grant rewards after server-validated results; the grant key makes retries harmless.
app.MapPost("/internal/v1/players/{id:guid}/grants", async (Guid id, GrantRequest request, ProgressionStore store, CancellationToken ct) =>
{
    (bool ok, GrantResult? result) = await store.GrantAsync(id, request, ct);
    return ok ? Results.Ok(result) : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_grant");
}).AddEndpointFilter(async (context, next) => ServiceAuthorized(context.HttpContext, options) ? await next(context) : Results.Unauthorized());

// COSM-001: game servers check what a player owns before showing it to others (unknown players own nothing).
app.MapGet("/internal/v1/players/{id:guid}/entitlements", async (Guid id, EntitlementStore store, CancellationToken ct) =>
    Results.Ok(new OwnedSkusView((await store.ListAsync(id, ct)).Select(e => e.Sku).ToArray())))
    .AddEndpointFilter(async (context, next) => ServiceAuthorized(context.HttpContext, options) ? await next(context) : Results.Unauthorized());
app.MapPost("/internal/v1/players/{id:guid}/entitlements", async (Guid id, GrantEntitlementRequest request, EntitlementStore store, CancellationToken ct) =>
    EntitlementResult(await store.GrantAsync(id, request, ct)))
    .AddEndpointFilter(async (context, next) => ServiceAuthorized(context.HttpContext, options) ? await next(context) : Results.Unauthorized());
app.MapPost("/internal/v1/entitlements/revoke", async (RevokeEntitlementRequest request, EntitlementStore store, CancellationToken ct) =>
    await store.RevokeAsync(request, ct) ? Results.NoContent() : Results.NotFound())
    .AddEndpointFilter(async (context, next) => ServiceAuthorized(context.HttpContext, options) ? await next(context) : Results.Unauthorized());

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
    SnapshotError.QuotaExceeded => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "quota_exceeded"),
    _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_request"),
};

static IResult LeaseResult((LeaseOutcome Outcome, LeaseView? Lease) r) => r.Outcome switch
{
    LeaseOutcome.Granted => r.Lease == null ? Results.NoContent() : Results.Ok(r.Lease),
    LeaseOutcome.Held => Results.Json(r.Lease, statusCode: StatusCodes.Status409Conflict),
    LeaseOutcome.Lost => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "lease_lost"),
    LeaseOutcome.NotFound => Results.NotFound(),
    _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_request"),
};

static IResult EntitlementResult(EntitlementOutcome outcome) => outcome switch
{
    EntitlementOutcome.Granted => Results.Ok(new { granted = true }),
    EntitlementOutcome.AlreadyOwned => Results.Ok(new { granted = false }),
    EntitlementOutcome.UnknownSku => Results.NotFound(),
    EntitlementOutcome.NotUnlocked => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "not_unlocked"),
    _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_request"),
};

static bool ServiceAuthorized(HttpContext http, ApiOptions options)
{
    string? presented = http.Request.Headers["X-Service-Token"];
    return presented != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(options.ServiceToken));
}

public sealed record RefreshRequest(string? RefreshToken);

public sealed record LinkRequest(string? Code);

/// <summary>Entry point (public for WebApplicationFactory in tests).</summary>
public partial class Program;
