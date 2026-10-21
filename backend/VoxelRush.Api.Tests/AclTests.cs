using System.Net;
using System.Net.Http.Json;
using Npgsql;

namespace VoxelRush.Api.Tests;

/// <summary>SAVE-004: who can see, join, change, invite to and delete a world.</summary>
[NonParallelizable]
public sealed class AclTests
{
    private ApiHost _api = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _api = await ApiHost.StartAsync();

    [OneTimeTearDown]
    public async Task StopAsync() => await _api.DisposeAsync();

    private sealed record Cast(TokenPair Owner, TokenPair Editor, TokenPair Visitor, TokenPair Stranger, WorldView World);

    private async Task<Cast> WorldWithMembersAsync(string visibility)
    {
        TokenPair owner = await _api.GuestAsync(), editor = await _api.GuestAsync(), visitor = await _api.GuestAsync(), stranger = await _api.GuestAsync();
        WorldView world = await _api.CreateWorldAsync(owner, "Acl " + visibility, visibility);
        HttpClient o = _api.Client(owner.AccessToken);
        Assert.That((await o.PostAsJsonAsync($"/v1/worlds/{world.Id}/members", new { playerId = editor.PlayerId, role = "editor" })).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await o.PostAsJsonAsync($"/v1/worlds/{world.Id}/members", new { playerId = visitor.PlayerId, role = "visitor" })).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        return new Cast(owner, editor, visitor, stranger, world);
    }

    private TokenPair Who(Cast c, string who) => who switch
    {
        "owner" => c.Owner,
        "editor" => c.Editor,
        "visitor" => c.Visitor,
        _ => c.Stranger,
    };

    // visibility, caller, GET, join role ("-" = 404), PATCH, invite, DELETE
    [TestCase("private", "owner", 200, "owner", 204, 200, 204)]
    [TestCase("private", "editor", 200, "editor", 403, 403, 403)]
    [TestCase("private", "visitor", 200, "visitor", 403, 403, 403)]
    [TestCase("private", "stranger", 404, "-", 404, 404, 404)]
    [TestCase("friends", "visitor", 200, "visitor", 403, 403, 403)]
    [TestCase("friends", "stranger", 404, "-", 404, 404, 404)]
    [TestCase("public", "editor", 200, "editor", 403, 403, 403)]
    [TestCase("public", "stranger", 200, "visitor", 403, 403, 403)]
    public async Task Matrix(string visibility, string caller, int get, string join, int patch, int invite, int delete)
    {
        Cast c = await WorldWithMembersAsync(visibility);
        HttpClient client = _api.Client(Who(c, caller).AccessToken);
        string path = $"/v1/worlds/{c.World.Id}";

        Assert.That((int)(await client.GetAsync(path)).StatusCode, Is.EqualTo(get), "GET");
        HttpResponseMessage joined = await client.PostAsync(path + "/join", null);
        if (join == "-")
        {
            Assert.That(joined.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "join");
        }
        else
        {
            Assert.That((await joined.Content.ReadFromJsonAsync<JoinResponse>())!.Role, Is.EqualTo(join), "join role");
        }

        Assert.That((int)(await client.PostAsJsonAsync(path + "/invites", new { role = "editor" })).StatusCode, Is.EqualTo(invite), "invite");
        Assert.That((int)(await client.PatchAsJsonAsync(path, new { name = "Renamed" })).StatusCode, Is.EqualTo(patch), "PATCH");
        Assert.That((int)(await client.DeleteAsync(path)).StatusCode, Is.EqualTo(delete), "DELETE");
    }

    [Test]
    public async Task Invite_GrantsItsRole_ThenIsUsedUp()
    {
        TokenPair owner = await _api.GuestAsync(), a = await _api.GuestAsync(), b = await _api.GuestAsync();
        WorldView world = await _api.CreateWorldAsync(owner, "Invite World");
        var invite = await (await _api.Client(owner.AccessToken).PostAsJsonAsync($"/v1/worlds/{world.Id}/invites", new { role = "editor", maxUses = 1 }))
            .Content.ReadFromJsonAsync<InviteCreated>();

        Assert.That((await _api.Client(a.AccessToken).PostAsJsonAsync("/v1/invites/accept", new { code = invite!.Code })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var join = await (await _api.Client(a.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null)).Content.ReadFromJsonAsync<JoinResponse>();
        Assert.That(join!.Role, Is.EqualTo("editor"));
        Assert.That((await _api.Client(b.AccessToken).PostAsJsonAsync("/v1/invites/accept", new { code = invite.Code })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "used up");
    }

    [Test]
    public async Task Invites_Expire_CanBeRevoked_AndUnknownCodesRevealNothing()
    {
        TokenPair owner = await _api.GuestAsync(), guest = await _api.GuestAsync();
        WorldView world = await _api.CreateWorldAsync(owner, "Expiring World");
        HttpClient o = _api.Client(owner.AccessToken);
        var shortLived = await (await o.PostAsJsonAsync($"/v1/worlds/{world.Id}/invites", new { expiresInHours = 1 })).Content.ReadFromJsonAsync<InviteCreated>();
        var revoked = await (await o.PostAsJsonAsync($"/v1/worlds/{world.Id}/invites", new { maxUses = 5 })).Content.ReadFromJsonAsync<InviteCreated>();
        Assert.That((await o.DeleteAsync($"/v1/worlds/{world.Id}/invites")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        HttpClient g = _api.Client(guest.AccessToken);
        Assert.That((await g.PostAsJsonAsync("/v1/invites/accept", new { code = revoked!.Code })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "revoked");
        Assert.That((await g.PostAsJsonAsync("/v1/invites/accept", new { code = "not-a-code" })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        _api.Clock.Now = _api.Clock.Now.AddHours(2);
        try
        {
            // A fresh token at the later time (the earlier one has expired too).
            TokenPair later = await _api.GuestAsync();
            Assert.That((await _api.Client(later.AccessToken).PostAsJsonAsync("/v1/invites/accept", new { code = shortLived!.Code })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "expired");
        }
        finally
        {
            _api.Clock.Now = DateTimeOffset.UtcNow;
        }

        Assert.That((await g.GetAsync($"/v1/worlds/{world.Id}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "still not a member");
    }

    [Test]
    public async Task Invites_NeverDowngrade_AndOwnersCantInviteThemselves()
    {
        TokenPair owner = await _api.GuestAsync(), editor = await _api.GuestAsync();
        WorldView world = await _api.CreateWorldAsync(owner, "Roles World");
        HttpClient o = _api.Client(owner.AccessToken);
        await o.PostAsJsonAsync($"/v1/worlds/{world.Id}/members", new { playerId = editor.PlayerId, role = "editor" });
        var visitorInvite = await (await o.PostAsJsonAsync($"/v1/worlds/{world.Id}/invites", new { role = "visitor", maxUses = 3 })).Content.ReadFromJsonAsync<InviteCreated>();

        await _api.Client(editor.AccessToken).PostAsJsonAsync("/v1/invites/accept", new { code = visitorInvite!.Code });
        Assert.That((await (await _api.Client(editor.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null)).Content.ReadFromJsonAsync<JoinResponse>())!.Role, Is.EqualTo("editor"));
        Assert.That((await o.PostAsJsonAsync("/v1/invites/accept", new { code = visitorInvite.Code })).StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await o.PostAsJsonAsync($"/v1/worlds/{world.Id}/invites", new { role = "owner" })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "can't invite owners");
        Assert.That((await o.PostAsJsonAsync($"/v1/worlds/{world.Id}/invites", new { expiresInHours = 10000 })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task InviteCodes_AreStoredOnlyAsHashes()
    {
        TokenPair owner = await _api.GuestAsync();
        WorldView world = await _api.CreateWorldAsync(owner, "Hash World");
        var invite = await (await _api.Client(owner.AccessToken).PostAsJsonAsync($"/v1/worlds/{world.Id}/invites", new { })).Content.ReadFromJsonAsync<InviteCreated>();

        await using var db = new NpgsqlConnection(_api.ConnectionString);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM world_invites WHERE encode(code_hash, 'escape') LIKE '%' || $1 || '%'", db) { Parameters = { new() { Value = invite!.Code } } };
        Assert.That((long)(await cmd.ExecuteScalarAsync())!, Is.Zero);
    }

    [Test]
    public async Task DeletedWorlds_DisappearFromListsAndJoins()
    {
        TokenPair owner = await _api.GuestAsync(), other = await _api.GuestAsync();
        WorldView world = await _api.CreateWorldAsync(owner, "Short Lived", "public");
        Assert.That((await _api.Client(other.AccessToken).GetFromJsonAsync<WorldView[]>("/v1/worlds/public?limit=50"))!.Select(w => w.Id), Does.Contain(world.Id));

        Assert.That((await _api.Client(owner.AccessToken).DeleteAsync($"/v1/worlds/{world.Id}")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        Assert.That((await _api.Client(other.AccessToken).GetFromJsonAsync<WorldView[]>("/v1/worlds/public?limit=50"))!.Select(w => w.Id), Does.Not.Contain(world.Id));
        Assert.That((await _api.Client(owner.AccessToken).PostAsync($"/v1/worlds/{world.Id}/join", null)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _api.Client(owner.AccessToken).GetFromJsonAsync<WorldView[]>("/v1/worlds/mine"))!, Is.Empty);
        // A deleted world frees its quota slot.
        for (int i = 0; i < 3; i++)
        {
            await _api.CreateWorldAsync(owner, $"Again {i}");
        }
    }

    [Test]
    public async Task PublicDiscovery_ListsOnlyPublicWorlds()
    {
        TokenPair owner = await _api.GuestAsync(), viewer = await _api.GuestAsync();
        WorldView pub = await _api.CreateWorldAsync(owner, "Open Track", "public");
        WorldView priv = await _api.CreateWorldAsync(owner, "Closed Track", "private");
        WorldView friends = await _api.CreateWorldAsync(owner, "Friends Track", "friends");

        var ids = (await _api.Client(viewer.AccessToken).GetFromJsonAsync<WorldView[]>("/v1/worlds/public?limit=50"))!.Select(w => w.Id).ToList();

        Assert.That(ids, Does.Contain(pub.Id));
        Assert.That(ids, Does.Not.Contain(priv.Id).And.Not.Contain(friends.Id));
    }
}
