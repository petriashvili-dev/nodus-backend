using System.Net;
using System.Text.Json;

namespace Nodus.Tests.Integration;

public class TimezoneChangeTests(TestApp app) : IClassFixture<TestApp>
{
    private static string Key() => Guid.NewGuid().ToString();

    private async Task<JsonElement> ChangeAsync(TestUser user, string timezone)
    {
        var response = await user.Client.SendJsonAsync(HttpMethod.Put, "/v1/me/timezone", new { timezone }, Key());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static async Task<JsonElement> NodeAsync(TestUser user, Guid id) =>
        await (await user.Client.GetAsync($"/v1/nodes/{id}")).ReadJsonAsync();

    [Fact]
    public async Task Nodes_keep_their_wall_time_in_the_new_zone_and_past_stays_unchanged()
    {
        app.Time.Set(DateTimeOffset.Parse("2026-10-17T10:00:00+03:00"));
        var user = await app.CreateUserAsync();
        var (series, future, past, undated) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await user.Client.PostJsonAsync("/v1/nodes", new
        {
            id = series, type = "recurring", title = "Зарядка", timezone = "Europe/Moscow",
            recurrence = new { frequency = "daily", start_date = "2026-10-01", times = new[] { "09:00" } }
        }, Key());
        await user.Client.PostJsonAsync("/v1/nodes", new
        {
            id = future, type = "one_time", title = "Встреча", timezone = "Europe/Moscow", date = "2026-11-02", time = "18:30"
        }, Key());
        await user.Client.PostJsonAsync("/v1/nodes", new
        {
            id = past, type = "one_time", title = "Было", timezone = "Europe/Moscow", date = "2026-10-10", time = "12:00"
        }, Key());
        await user.Client.PostJsonAsync("/v1/nodes", new { id = undated, type = "one_time", title = "Когда-нибудь", timezone = "Europe/Moscow" }, Key());

        var result = await ChangeAsync(user, "America/New_York");

        Assert.Equal((3, 1, 0), (result.GetProperty("adapted_nodes").GetInt32(), result.GetProperty("skipped_past_nodes").GetInt32(),
            result.GetProperty("skipped_shared_nodes").GetInt32()));
        var meeting = await NodeAsync(user, future);
        Assert.Equal(("America/New_York", "18:30", "2026-11-02T23:30:00+00:00"), (meeting.GetProperty("timezone").GetString(),
            meeting.GetProperty("time").GetString(), meeting.GetProperty("scheduled_at").GetString()));
        Assert.Equal("Europe/Moscow", (await NodeAsync(user, past)).GetProperty("timezone").GetString());
        Assert.Equal("America/New_York", (await NodeAsync(user, undated)).GetProperty("timezone").GetString());

        var history = (await (await user.Client.GetAsync($"/v1/nodes/{series}/occurrences?from=2026-10-16&to=2026-10-18")).ReadJsonAsync())
            .GetProperty("occurrences").EnumerateArray()
            .Select(o => (o.GetProperty("date").GetString(), o.GetProperty("time").GetString(), o.GetProperty("scheduled_at").GetString()))
            .ToList();
        Assert.Equal([
            ("2026-10-16", "09:00", "2026-10-16T06:00:00+00:00"), // past: still Moscow
            ("2026-10-17", "09:00", "2026-10-17T06:00:00+00:00"), // already past today: unchanged
            ("2026-10-18", "09:00", "2026-10-18T13:00:00+00:00")  // future: 09:00 in New York
        ], history);
        Assert.Equal("America/New_York", (await (await user.Client.GetAsync("/v1/me")).ReadJsonAsync()).GetProperty("timezone").GetString());
    }

    [Fact]
    public async Task Repeating_the_change_does_nothing_and_shared_nodes_do_not_move()
    {
        var owner = await app.CreateUserAsync();
        var member = await app.CreateUserAsync();
        var token = (await (await owner.Client.PostJsonAsync("/v1/connections/invites", null, Key())).ReadJsonAsync())
            .GetProperty("token").GetString();
        var connection = await (await member.Client.PostJsonAsync($"/v1/connections/invites/{token}/accept", null, Key())).ReadJsonAsync();
        await owner.Client.PostJsonAsync($"/v1/connections/{connection.GetProperty("id").GetGuid()}/confirm", null, Key());
        var shared = Guid.NewGuid();
        await owner.Client.PostJsonAsync("/v1/nodes", new { id = shared, type = "one_time", title = "Ужин", timezone = "Europe/Moscow" }, Key());
        await owner.Client.PostJsonAsync($"/v1/nodes/{shared}/members", new { user_id = member.Id }, Key());

        var first = await ChangeAsync(owner, "Asia/Tbilisi");
        var second = await ChangeAsync(owner, "Asia/Tbilisi");
        var invalid = await owner.Client.SendJsonAsync(HttpMethod.Put, "/v1/me/timezone", new { timezone = "Moscow" }, Key());

        Assert.Equal(1, first.GetProperty("skipped_shared_nodes").GetInt32());
        Assert.Equal("Europe/Moscow", (await NodeAsync(owner, shared)).GetProperty("timezone").GetString());
        Assert.Equal(0, second.GetProperty("adapted_nodes").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
