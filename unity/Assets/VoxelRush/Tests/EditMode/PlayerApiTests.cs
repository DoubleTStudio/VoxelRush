using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using VoxelRush.Networking;
using VoxelRush.Networking.Online;

namespace VoxelRush.Tests.EditMode
{
    /// <summary>MVP-004: the phone's API client against a scripted server.</summary>
    public sealed class PlayerApiTests
    {
        private const string Pair = "{\"playerId\":\"p1\",\"accessToken\":\"a1\",\"refreshToken\":\"r1\",\"accessExpiresAt\":\"2030-01-01T00:00:00Z\"}";
        private const string Rotated = "{\"playerId\":\"p1\",\"accessToken\":\"a2\",\"refreshToken\":\"r2\",\"accessExpiresAt\":\"2030-01-01T00:00:00Z\"}";

        // 32 random-looking bytes are not a ticket; build one of the real length.
        private static readonly string Ticket = new string('A', (VoxelRush.Core.Networking.JoinTicket.Length * 4 + 2) / 3);

        private sealed class FakeTransport : IApiTransport
        {
            public readonly Queue<(long, string)> Replies = new Queue<(long, string)>();
            public readonly List<(string Method, string Path, string? Json, string? Bearer)> Requests = new List<(string, string, string?, string?)>();

            public Task<(long Status, string Body)> SendAsync(string method, string path, string? json, string? bearer)
            {
                Requests.Add((method, path, json, bearer));
                return Task.FromResult(Replies.Count > 0 ? Replies.Dequeue() : (500L, string.Empty));
            }
        }

        private sealed class MemoryTokens : ITokenStore
        {
            public string? Value;

            public string? Load() => Value;

            public void Save(string? refreshToken) => Value = refreshToken;
        }

        private static PlayerApi Api(FakeTransport http, MemoryTokens tokens) => new PlayerApi(http, tokens) { Delay = _ => Task.CompletedTask };

        [Test]
        public void FirstLaunch_CreatesAGuest_AndKeepsItsRefreshToken()
        {
            var http = new FakeTransport();
            var tokens = new MemoryTokens();
            http.Replies.Enqueue((200, Pair));

            ApiResult<string> result = Api(http, tokens).SignInAsync().GetAwaiter().GetResult();

            Assert.That(result.Ok, Is.True);
            Assert.That(result.Value, Is.EqualTo("p1"));
            Assert.That(http.Requests[0].Path, Is.EqualTo("v1/auth/guest"));
            Assert.That(tokens.Value, Is.EqualTo("r1"));
        }

        [Test]
        public void NextLaunch_ResumesTheSameAccount_WithoutANewGuest()
        {
            var http = new FakeTransport();
            var tokens = new MemoryTokens { Value = "r1" };
            http.Replies.Enqueue((200, Rotated));

            Assert.That(Api(http, tokens).SignInAsync().GetAwaiter().GetResult().Ok, Is.True);
            Assert.That(http.Requests.Count, Is.EqualTo(1));
            Assert.That(http.Requests[0].Path, Is.EqualTo("v1/auth/refresh"));
            Assert.That(http.Requests[0].Json, Does.Contain("\"refreshToken\":\"r1\""));
            Assert.That(tokens.Value, Is.EqualTo("r2"), "rotated refresh token replaces the old one");
        }

        [Test]
        public void ExpiredAccess_IsRefreshedOnceAndTheCallRetried()
        {
            var http = new FakeTransport();
            var tokens = new MemoryTokens();
            PlayerApi api = Api(http, tokens);
            http.Replies.Enqueue((200, Pair));
            api.SignInAsync().GetAwaiter().GetResult();

            http.Replies.Enqueue((401, string.Empty));
            http.Replies.Enqueue((200, Rotated));
            http.Replies.Enqueue((200, "[{\"id\":\"w1\",\"name\":\"Plot\",\"visibility\":\"public\",\"role\":\"owner\"}]"));
            ApiResult<WorldEntry[]> worlds = api.MyWorldsAsync().GetAwaiter().GetResult();

            Assert.That(worlds.Ok, Is.True);
            Assert.That(worlds.Value!.Length, Is.EqualTo(1));
            Assert.That(worlds.Value[0].name, Is.EqualTo("Plot"));
            Assert.That(http.Requests[3].Bearer, Is.EqualTo("a2"), "retried with the refreshed access token");
        }

        [Test]
        public void Join_WaitsWhileTheServerStarts_ThenReturnsATicket()
        {
            var http = new FakeTransport();
            PlayerApi api = Api(http, new MemoryTokens());
            http.Replies.Enqueue((200, Pair));
            http.Replies.Enqueue((202, "{\"status\":\"starting\",\"retryAfterSeconds\":2}"));
            http.Replies.Enqueue((202, "{\"status\":\"starting\",\"retryAfterSeconds\":2}"));
            http.Replies.Enqueue((200, $"{{\"ticket\":\"{Ticket}\",\"endpoint\":\"10.0.0.5:7801\",\"role\":\"owner\"}}"));
            var progress = new List<string>();

            ApiResult<JoinGrant> grant = api.JoinAsync("w1", progress.Add).GetAwaiter().GetResult();

            Assert.That(grant.Ok, Is.True, grant.Error);
            Assert.That(grant.Value!.endpoint, Is.EqualTo("10.0.0.5:7801"));
            Assert.That(ClientNetwork.DecodeTicket(grant.Value.ticket), Is.Not.Null);
            Assert.That(progress.Count, Is.EqualTo(2));
            Assert.That(http.Requests[1].Path, Is.EqualTo("v1/worlds/w1/join"));
        }

        [Test]
        public void Failures_BecomePlayerReadableMessages()
        {
            var http = new FakeTransport();
            PlayerApi api = Api(http, new MemoryTokens());
            http.Replies.Enqueue((0, "Cannot resolve destination host"));
            Assert.That(api.SignInAsync().GetAwaiter().GetResult().Error, Does.Contain("Can't reach the server"));

            http.Replies.Enqueue((200, Pair));
            http.Replies.Enqueue((403, "{\"title\":\"forbidden\"}"));
            Assert.That(api.JoinAsync("w1").GetAwaiter().GetResult().Error, Is.EqualTo("Not allowed."));

            http.Replies.Enqueue((503, "{\"title\":\"no_capacity\"}"));
            Assert.That(api.JoinAsync("w1").GetAwaiter().GetResult().Error, Does.Contain("No game server is free"));
        }

        [Test]
        public void WorldNames_AreJsonEscaped()
        {
            var http = new FakeTransport();
            PlayerApi api = Api(http, new MemoryTokens());
            http.Replies.Enqueue((200, Pair));
            http.Replies.Enqueue((201, "{\"id\":\"w2\",\"name\":\"a\\\"b\",\"visibility\":\"public\",\"role\":\"owner\"}"));

            ApiResult<WorldEntry> created = api.CreateWorldAsync("a\"b\\c", "public").GetAwaiter().GetResult();

            Assert.That(created.Ok, Is.True);
            Assert.That(http.Requests[1].Json, Is.EqualTo("{\"name\":\"a\\\"b\\\\c\",\"visibility\":\"public\"}"));
        }
    }
}
