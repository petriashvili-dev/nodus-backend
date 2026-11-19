using System.Net;
using Microsoft.EntityFrameworkCore;

namespace Nodus.Tests.Integration;

public class RefactoringRegressionTests(TestApp app) : IClassFixture<TestApp>
{
    private static string Key() => Guid.NewGuid().ToString();

    [Fact]
    public async Task Old_occurrence_states_stay_visible_in_history_and_sync_outside_the_current_window()
    {
        app.Time.Set(DateTimeOffset.Parse("2026-10-18T10:00:00+03:00"));
        var user = await app.CreateUserAsync();
        var id = Guid.NewGuid();
        await user.Client.PostJsonAsync("/v1/nodes", new
        {
            id, type = "recurring", title = "Зарядка", timezone = "Europe/Moscow",
            recurrence = new { frequency = "daily", start_date = "2026-09-01", times = new[] { "09:00" } }
        }, Key());

        var untie = await user.Client.SendJsonAsync(HttpMethod.Put, $"/v1/nodes/{id}/occurrences/g0-20260928-0/untie",
            new { completed_at = "2026-09-28T07:00:00Z" }, Key());
        var history = await (await user.Client.GetAsync($"/v1/nodes/{id}/occurrences?from=2026-09-27&to=2026-09-29")).ReadJsonAsync();
        var sync = await (await user.Client.GetAsync("/v1/sync?cursor=0")).ReadJsonAsync();

        Assert.Equal(HttpStatusCode.OK, untie.StatusCode);
        Assert.Equal(["tied", "untied", "tied"],
            history.GetProperty("occurrences").EnumerateArray().Select(o => o.GetProperty("state").GetString()));
        Assert.Contains(sync.GetProperty("changes").EnumerateArray(), c =>
            c.GetProperty("entity_id").GetString() == $"{id}:g0-20260928-0" && c.GetProperty("data").GetProperty("state").GetString() == "untied");
    }

    [Fact]
    public async Task Batch_with_a_taken_id_creates_nothing()
    {
        var user = await app.CreateUserAsync();
        var (taken, fresh) = (Guid.NewGuid(), Guid.NewGuid());
        await user.Client.PostJsonAsync("/v1/nodes", new { id = taken, type = "one_time", title = "A", timezone = "Europe/Moscow" }, Key());

        var response = await user.Client.PostJsonAsync("/v1/nodes/batch", new
        {
            nodes = new[]
            {
                new { id = fresh, type = "one_time", title = "B", timezone = "Europe/Moscow" },
                new { id = taken, type = "one_time", title = "C", timezone = "Europe/Moscow" }
            }
        }, Key());

        Assert.Equal("node_already_exists", (await response.ReadJsonAsync()).Code());
        Assert.False(await app.WithDbAsync(db => db.Nodes.AnyAsync(n => n.Id == fresh)));
    }

    [Fact]
    public async Task Default_feed_range_starts_from_yesterday_in_the_users_zone()
    {
        app.Time.Set(DateTimeOffset.Parse("2026-10-17T11:00:00Z")); // already 2026-10-18 01:00 in Kiritimati (UTC+14)
        var user = await app.CreateUserAsync();
        await user.Client.SendJsonAsync(HttpMethod.Put, "/v1/me/timezone", new { timezone = "Pacific/Kiritimati" }, Key());

        var feed = await (await user.Client.GetAsync("/v1/feed")).ReadJsonAsync();

        Assert.Equal("2026-10-17", feed.GetProperty("from").GetString());
    }

    [Fact]
    public async Task Version_endpoint_reports_the_build()
    {
        var version = await (await app.CreateClient().GetAsync("/version")).ReadJsonAsync();

        Assert.Equal("unknown", version.GetProperty("commit").GetString());
    }
}

public class SseConnectionLimitTests : IAsyncLifetime
{
    private readonly TestApp _app = new();

    public SseConnectionLimitTests() => _app.Settings["Sse:MaxConnectionsPerUser"] = "1";

    public Task InitializeAsync() => _app.InitializeAsync();
    public Task DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task Streams_above_the_per_user_limit_are_rejected()
    {
        var user = await _app.CreateUserAsync();

        using var first = await user.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/v1/events"),
            HttpCompletionOption.ResponseHeadersRead);
        using var second = await user.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/v1/events"),
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("too_many_connections", (await second.ReadJsonAsync()).Code());
    }
}
