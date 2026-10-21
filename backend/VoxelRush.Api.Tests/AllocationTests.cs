using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VoxelRush.Core.Networking;

namespace VoxelRush.Api.Tests;

/// <summary>A fake host agent: answers ensure calls from a configurable function and records what the API sent.</summary>
internal sealed class FakeHostAgent : HttpMessageHandler
{
    public readonly List<(string Path, string? Token)> Calls = new();

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json(HttpStatusCode.OK, new { status = "ready", endpoint = "203.0.113.7:7801" });

    public static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status) { Content = JsonContent.Create(body) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add((request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("X-Service-Token", out var t) ? t.Single() : null));
        }

        return Task.FromResult(Respond(request));
    }
}

/// <summary>ALLOC-001: join asks the allocator for the world's server before issuing a ticket.</summary>
[NonParallelizable]
public sealed class AllocationTests
{
    private readonly FakeHostAgent _agent = new();
    private ApiHost _api = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _api = await ApiHost.StartAsync(
        storage: false,
        extra: new Dictionary<string, string?> { ["HostAgentUrl"] = "http://agent.test/" },
        services: s => s.AddHttpClient(HostAgentAllocator.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _agent));

    [OneTimeTearDown]
    public async Task StopAsync() => await _api.DisposeAsync();

    [SetUp]
    public void Reset()
    {
        _agent.Calls.Clear();
        _agent.Respond = _ => FakeHostAgent.Json(HttpStatusCode.OK, new { status = "ready", endpoint = "203.0.113.7:7801" });
    }

    private async Task<(TokenPair Owner, WorldView World)> WorldAsync()
    {
        TokenPair owner = await _api.GuestAsync();
        return (owner, await _api.CreateWorldAsync(owner, "Hosted"));
    }

    private static byte[] FromBase64Url(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4));

    [Test]
    public async Task WithoutAHostAgent_EveryWorldUsesTheStaticEndpoint()
    {
        await using ApiHost plain = await ApiHost.StartAsync(storage: false);
        Assert.That(plain.Services.GetRequiredService<IWorldHostAllocator>(), Is.InstanceOf<StaticAllocator>());
        Assert.That(_api.Services.GetRequiredService<IWorldHostAllocator>(), Is.InstanceOf<HostAgentAllocator>());

        TokenPair owner = await plain.GuestAsync();
        WorldView a = await plain.CreateWorldAsync(owner, "A");
        WorldView b = await plain.CreateWorldAsync(owner, "B");
        foreach (WorldView w in new[] { a, b })
        {
            var join = await (await plain.Client(owner.AccessToken).PostAsync($"/v1/worlds/{w.Id}/join", null)).Content.ReadFromJsonAsync<JoinResponse>();
            Assert.That(join!.Endpoint, Is.EqualTo("game.test:7777"));
        }
    }

    [Test]
    public async Task Ready_ReturnsTheWorldsOwnEndpointAndAValidTicket()
    {
        (TokenPair owner, WorldView world) = await WorldAsync();

        HttpResponseMessage response = await _api.Client(owner.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        JoinResponse join = (await response.Content.ReadFromJsonAsync<JoinResponse>())!;
        Assert.That((join.Endpoint, join.Role), Is.EqualTo(("203.0.113.7:7801", "owner")));
        Assert.That(new TicketVerifier(ApiHost.TicketKey, world.Id).Verify(FromBase64Url(join.Ticket), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out _), Is.EqualTo(TicketRejection.None));
        Assert.That(_agent.Calls, Is.EqualTo(new[] { ($"/internal/v1/hosts/{world.Id}/ensure", (string?)ApiHost.ServiceToken) }), "one ensure call, service token attached");
    }

    [Test]
    public async Task Starting_Is202WithRetryAfter_AndNoTicket()
    {
        (TokenPair owner, WorldView world) = await WorldAsync();
        _agent.Respond = _ => FakeHostAgent.Json(HttpStatusCode.Accepted, new { status = "starting", retryAfterSeconds = 3 });

        HttpResponseMessage response = await _api.Client(owner.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(response.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(3)));
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("starting"));
        Assert.That(body.GetProperty("retryAfterSeconds").GetInt32(), Is.EqualTo(3));
        Assert.That(body.TryGetProperty("ticket", out _), Is.False, "no ticket until the server is up");

        // The client retries once the server listens.
        Reset();
        Assert.That((await _api.Client(owner.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task NoCapacity_Is503()
    {
        (TokenPair owner, WorldView world) = await WorldAsync();
        _agent.Respond = _ => FakeHostAgent.Json(HttpStatusCode.ServiceUnavailable, new { status = "unavailable", reason = "no_capacity", retryAfterSeconds = 10 });

        HttpResponseMessage response = await _api.Client(owner.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(response.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString(), Is.EqualTo("no_capacity"));
    }

    [Test]
    public async Task AgentDown_Unauthorized_OrGarbled_Is503HostUnavailable()
    {
        (TokenPair owner, WorldView world) = await WorldAsync();
        var failures = new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => throw new HttpRequestException("connection refused"),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json") },
            _ => FakeHostAgent.Json(HttpStatusCode.OK, new { status = "ready" }),
        };

        foreach (var failure in failures)
        {
            _agent.Respond = failure;
            HttpResponseMessage response = await _api.Client(owner.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString(), Is.EqualTo("host_unavailable"));
        }
    }

    [Test]
    public async Task Strangers_CannotStartServers()
    {
        (_, WorldView world) = await WorldAsync();
        TokenPair stranger = await _api.GuestAsync();

        Assert.That((await _api.Client(stranger.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _api.Client().PostAsync($"/v1/worlds/{world.Id}/join", null)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(_agent.Calls, Is.Empty);
    }

    [TestCase("not a url")]
    [TestCase("ftp://agent.test/")]
    public void InvalidHostAgentUrl_FailsAtStartup(string url)
    {
        var options = new ApiOptions
        {
            ConnectionString = "Host=x",
            JwtSigningKey = Convert.ToBase64String(new byte[32]),
            TicketKey = Convert.ToBase64String(new byte[32]),
            ServiceToken = ApiHost.ServiceToken,
            HostAgentUrl = url,
        };

        Assert.That(options.Validate, Throws.InvalidOperationException.With.Message.Contains("HostAgentUrl"));
    }
}
