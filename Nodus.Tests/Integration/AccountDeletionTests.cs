using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Nodus.Tests.Integration;

public class AccountDeletionTests(TestApp app) : IClassFixture<TestApp>
{
    private static string Key() => Guid.NewGuid().ToString();

    private static object Node(Guid id) => new { id, type = "one_time", title = "Общий узел", timezone = "Europe/Moscow" };

    private async Task ConnectAsync(TestUser inviter, TestUser invitee)
    {
        var token = (await (await inviter.Client.PostJsonAsync("/v1/connections/invites", null, Key())).ReadJsonAsync())
            .GetProperty("token").GetString();
        var connection = await (await invitee.Client.PostJsonAsync($"/v1/connections/invites/{token}/accept", null, Key())).ReadJsonAsync();
        await inviter.Client.PostJsonAsync($"/v1/connections/{connection.GetProperty("id").GetGuid()}/confirm", null, Key());
    }

    private static async Task<List<JsonElement>> ChangesAsync(TestUser user, long cursor) =>
        (await (await user.Client.GetAsync($"/v1/sync?cursor={cursor}&limit=1000")).ReadJsonAsync())
        .GetProperty("changes").EnumerateArray().ToList();

    private static async Task<long> CursorAsync(TestUser user) =>
        (await (await user.Client.GetAsync("/v1/sync/snapshot")).ReadJsonAsync()).GetProperty("cursor").GetInt64();

    [Fact]
    public async Task Deleting_an_account_removes_its_data_and_updates_everyone_affected()
    {
        var deleted = await app.CreateUserAsync();
        var member = await app.CreateUserAsync();
        var owner = await app.CreateUserAsync();
        await ConnectAsync(deleted, member);
        await ConnectAsync(owner, deleted);
        var (ownNode, foreignNode) = (Guid.NewGuid(), Guid.NewGuid());
        await deleted.Client.PostJsonAsync("/v1/nodes", Node(ownNode), Key());
        await deleted.Client.PostJsonAsync($"/v1/nodes/{ownNode}/members", new { user_id = member.Id }, Key());
        await owner.Client.PostJsonAsync("/v1/nodes", Node(foreignNode), Key());
        await owner.Client.PostJsonAsync($"/v1/nodes/{foreignNode}/members", new { user_id = deleted.Id }, Key());
        var email = $"{Guid.NewGuid():N}@example.com";
        await deleted.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        await deleted.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = app.Emails.LastCodeFor(email) });
        var (memberCursor, ownerCursor) = (await CursorAsync(member), await CursorAsync(owner));

        var response = await deleted.Client.DeleteAsync("/v1/me");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // The member loses the shared node and the connection.
        var memberChanges = await ChangesAsync(member, memberCursor);
        Assert.Contains(memberChanges, c => c.GetProperty("entity_id").GetString() == ownNode.ToString() &&
                                            c.GetProperty("operation").GetString() == "delete");
        Assert.Contains(memberChanges, c => c.GetProperty("entity_type").GetString() == "connection" &&
                                            c.GetProperty("operation").GetString() == "delete");
        Assert.Equal(HttpStatusCode.NotFound, (await member.Client.GetAsync($"/v1/nodes/{ownNode}")).StatusCode);
        // The owner keeps the node, without the deleted member.
        var ownerChanges = await ChangesAsync(owner, ownerCursor);
        var updated = ownerChanges.Single(c => c.GetProperty("entity_id").GetString() == foreignNode.ToString());
        Assert.Empty(updated.GetProperty("data").GetProperty("members").EnumerateArray());
        Assert.Empty((await (await owner.Client.GetAsync("/v1/connections")).ReadJsonAsync()).GetProperty("connections").EnumerateArray());
        // Nothing of the account is left and its tokens stop working at once.
        Assert.False(await app.WithDbAsync(db => db.Users.AnyAsync(u => u.Id == deleted.Id)));
        Assert.False(await app.WithDbAsync(db => db.Nodes.AnyAsync(n => n.Id == ownNode)));
        Assert.False(await app.WithDbAsync(db => db.EmailVerificationCodes.AnyAsync(c => c.Email == email)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await deleted.Client.GetAsync("/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.CreateClient().PostJsonAsync("/v1/auth/refresh", new { refresh_token = deleted.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task The_email_of_a_deleted_account_can_be_linked_again()
    {
        var first = await app.CreateUserAsync();
        var email = $"{Guid.NewGuid():N}@example.com";
        await first.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        await first.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = app.Emails.LastCodeFor(email) });
        await first.Client.DeleteAsync("/v1/me");

        var second = await app.CreateUserAsync();
        await second.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        var linked = await second.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = app.Emails.LastCodeFor(email) });

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
    }
}
