using System.Net;
using System.Text.Json;

namespace Nodus.Tests.Integration;

public class SharingTests(TestApp app) : IClassFixture<TestApp>
{
    private static string Key() => Guid.NewGuid().ToString();

    private static object Node(Guid id) => new { id, type = "one_time", title = "Купить подарок", timezone = "Europe/Moscow" };

    private static async Task<JsonElement> InviteAsync(TestUser user) =>
        await (await user.Client.PostJsonAsync("/v1/connections/invites", null, Key())).ReadJsonAsync();

    private async Task<(TestUser Owner, TestUser Member)> ConnectedPairAsync()
    {
        var owner = await app.CreateUserAsync();
        var member = await app.CreateUserAsync();
        var invite = await InviteAsync(owner);
        var accepted = await (await member.Client.PostJsonAsync(
            $"/v1/connections/invites/{invite.GetProperty("token").GetString()}/accept", null, Key())).ReadJsonAsync();
        await owner.Client.PostJsonAsync($"/v1/connections/{accepted.GetProperty("id").GetGuid()}/confirm", null, Key());
        return (owner, member);
    }

    private static async Task<HttpResponseMessage> ShareAsync(TestUser owner, Guid nodeId, Guid userId) =>
        await owner.Client.PostJsonAsync($"/v1/nodes/{nodeId}/members", new { user_id = userId }, Key());

    [Fact]
    public async Task Link_invite_requires_both_sides_and_is_single_use()
    {
        var inviter = await app.CreateUserAsync();
        var invitee = await app.CreateUserAsync();
        var third = await app.CreateUserAsync();
        var token = (await InviteAsync(inviter)).GetProperty("token").GetString();

        var own = await inviter.Client.PostJsonAsync($"/v1/connections/invites/{token}/accept", null, Key());
        var accepted = await (await invitee.Client.PostJsonAsync($"/v1/connections/invites/{token}/accept", null, Key())).ReadJsonAsync();
        var replay = await third.Client.PostJsonAsync($"/v1/connections/invites/{token}/accept", null, Key());
        var selfConfirm = await invitee.Client.PostJsonAsync($"/v1/connections/{accepted.GetProperty("id").GetGuid()}/confirm", null, Key());
        var confirmed = await (await inviter.Client.PostJsonAsync(
            $"/v1/connections/{accepted.GetProperty("id").GetGuid()}/confirm", null, Key())).ReadJsonAsync();

        Assert.Equal("own_invite", (await own.ReadJsonAsync()).Code());
        Assert.Equal(("pending", inviter.Id), (accepted.GetProperty("status").GetString(), accepted.GetProperty("user_id").GetGuid()));
        Assert.Equal("invite_invalid", (await replay.ReadJsonAsync()).Code());
        Assert.Equal(HttpStatusCode.Forbidden, selfConfirm.StatusCode);
        Assert.Equal(("active", invitee.Id), (confirmed.GetProperty("status").GetString(), confirmed.GetProperty("user_id").GetGuid()));
    }

    [Fact]
    public async Task Code_invite_tolerates_formatting_and_expires()
    {
        var inviter = await app.CreateUserAsync();
        var invitee = await app.CreateUserAsync();
        var code = (await InviteAsync(inviter)).GetProperty("code").GetString()!;
        var expiring = (await InviteAsync(inviter)).GetProperty("code").GetString()!;

        var accepted = await invitee.Client.PostJsonAsync("/v1/connections/invites/accept",
            new { code = $"{code[..4].ToLowerInvariant()}-{code[4..]}" }, Key());
        app.Time.Advance(TimeSpan.FromHours(25));
        var expired = await (await app.CreateUserAsync()).Client.PostJsonAsync("/v1/connections/invites/accept",
            new { code = expiring }, Key());

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("invite_invalid", (await expired.ReadJsonAsync()).Code());
    }

    [Fact]
    public async Task Crossing_invitations_produce_one_active_connection()
    {
        var a = await app.CreateUserAsync();
        var b = await app.CreateUserAsync();
        var fromA = (await InviteAsync(a)).GetProperty("token").GetString();
        var fromB = (await InviteAsync(b)).GetProperty("token").GetString();

        var first = await (await b.Client.PostJsonAsync($"/v1/connections/invites/{fromA}/accept", null, Key())).ReadJsonAsync();
        var second = await (await a.Client.PostJsonAsync($"/v1/connections/invites/{fromB}/accept", null, Key())).ReadJsonAsync();
        var list = await (await a.Client.GetAsync("/v1/connections")).ReadJsonAsync();

        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal("active", second.GetProperty("status").GetString());
        Assert.Single(list.GetProperty("connections").EnumerateArray());
    }

    [Fact]
    public async Task Sharing_requires_an_active_connection()
    {
        var owner = await app.CreateUserAsync();
        var stranger = await app.CreateUserAsync();
        var id = Guid.NewGuid();
        await owner.Client.PostJsonAsync("/v1/nodes", Node(id), Key());

        var response = await ShareAsync(owner, id, stranger.Id);

        Assert.Equal("not_connected", (await response.ReadJsonAsync()).Code());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.GetAsync($"/v1/nodes/{id}")).StatusCode);
    }

    [Fact]
    public async Task Members_edit_and_untie_equally_but_only_the_owner_manages_and_deletes()
    {
        var (owner, member) = await ConnectedPairAsync();
        var id = Guid.NewGuid();
        await owner.Client.PostJsonAsync("/v1/nodes", Node(id), Key());

        var shared = await (await ShareAsync(owner, id, member.Id)).ReadJsonAsync();
        var edit = await member.Client.SendJsonAsync(HttpMethod.Patch, $"/v1/nodes/{id}",
            new { expected_version = shared.GetProperty("version").GetInt64(), title = "Подарок маме", timezone = "Europe/Moscow" }, Key());
        var untie = await member.Client.SendJsonAsync(HttpMethod.Put, $"/v1/nodes/{id}/occurrences/once/untie", null, Key());
        var stale = await owner.Client.SendJsonAsync(HttpMethod.Patch, $"/v1/nodes/{id}",
            new { expected_version = shared.GetProperty("version").GetInt64(), title = "Подарок папе", timezone = "Europe/Moscow" }, Key());
        var delete = await member.Client.SendJsonAsync(HttpMethod.Delete, $"/v1/nodes/{id}", null, Key());
        var addOther = await ShareAsync(member, id, (await app.CreateUserAsync()).Id);
        var ownerView = await (await owner.Client.GetAsync($"/v1/nodes/{id}")).ReadJsonAsync();

        Assert.Equal([member.Id], shared.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("user_id").GetGuid()));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, untie.StatusCode);
        Assert.Equal("version_conflict", (await stale.ReadJsonAsync()).Code());
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, addOther.StatusCode);
        Assert.Equal("Подарок маме", ownerView.GetProperty("title").GetString());
        Assert.Equal("untied", ownerView.GetProperty("occurrences")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task Members_receive_changes_and_a_tombstone_when_the_connection_ends()
    {
        var (owner, member) = await ConnectedPairAsync();
        var id = Guid.NewGuid();
        await owner.Client.PostJsonAsync("/v1/nodes", Node(id), Key());
        await ShareAsync(owner, id, member.Id);

        var afterShare = await (await member.Client.GetAsync("/v1/sync?cursor=0")).ReadJsonAsync();
        var cursor = afterShare.GetProperty("next_cursor").GetInt64();
        var connectionId = (await (await owner.Client.GetAsync("/v1/connections")).ReadJsonAsync())
            .GetProperty("connections")[0].GetProperty("id").GetGuid();
        await owner.Client.SendJsonAsync(HttpMethod.Delete, $"/v1/connections/{connectionId}", null, Key());
        var afterRemoval = await (await member.Client.GetAsync($"/v1/sync?cursor={cursor}")).ReadJsonAsync();

        Assert.Contains(afterShare.GetProperty("changes").EnumerateArray(),
            c => c.GetProperty("entity_id").GetString() == id.ToString() && c.GetProperty("operation").GetString() == "upsert");
        Assert.Contains(afterRemoval.GetProperty("changes").EnumerateArray(),
            c => c.GetProperty("entity_id").GetString() == id.ToString() && c.GetProperty("operation").GetString() == "delete");
        Assert.Equal(HttpStatusCode.NotFound, (await member.Client.GetAsync($"/v1/nodes/{id}")).StatusCode);
        Assert.Empty((await (await member.Client.GetAsync("/v1/feed")).ReadJsonAsync()).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Member_can_leave_and_connections_see_display_names()
    {
        var (owner, member) = await ConnectedPairAsync();
        var id = Guid.NewGuid();
        await owner.Client.PostJsonAsync("/v1/nodes", Node(id), Key());
        await ShareAsync(owner, id, member.Id);
        await member.Client.SendJsonAsync(HttpMethod.Patch, "/v1/me", new { display_name = "  Аня " }, Key());

        var name = (await (await owner.Client.GetAsync("/v1/connections")).ReadJsonAsync())
            .GetProperty("connections")[0].GetProperty("display_name").GetString();
        var leave = await member.Client.SendJsonAsync(HttpMethod.Delete, $"/v1/nodes/{id}/members/{member.Id}", null, Key());
        var members = (await (await owner.Client.GetAsync($"/v1/nodes/{id}")).ReadJsonAsync()).GetProperty("members");

        Assert.Equal("Аня", name);
        Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
        Assert.Empty(members.EnumerateArray());
    }
}
